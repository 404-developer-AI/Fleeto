package checks

import (
	"context"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"
)

// hostPattern mirrors the server's validation: a host name or an IP address, nothing that could become an option or a URL.
var hostPattern = regexp.MustCompile(`^[A-Za-z0-9._:\-\[\]%]{1,253}$`)

// Unreachable is the value of a network check that got no answer. The server treats it as critical.
const Unreachable = -1

// maxBodyBytes bounds how much of an HTTP response is read for the "contains" test.
const maxBodyBytes = 1 << 20

func validHost(host string) bool {
	return hostPattern.MatchString(host) && !strings.HasPrefix(host, "-")
}

// resolve returns the first address of host, preferring IPv4.
func resolve(ctx context.Context, host string) (net.IP, error) {
	host = strings.Trim(host, "[]")
	if ip := net.ParseIP(host); ip != nil {
		return ip, nil
	}
	addrs, err := net.DefaultResolver.LookupIPAddr(ctx, host)
	if err != nil {
		return nil, err
	}
	for _, a := range addrs {
		if a.IP.To4() != nil {
			return a.IP, nil
		}
	}
	if len(addrs) > 0 {
		return addrs[0].IP, nil
	}
	return nil, fmt.Errorf("no address for %s", host)
}

func pingCheck(ctx context.Context, params map[string]string) Measurement {
	host := strings.TrimSpace(params["host"])
	if !validHost(host) {
		return Measurement{Target: host, Error: "the ping check has no valid host parameter"}
	}
	count := intParam(params, "count", 3, 1, 10)
	ip, err := resolve(ctx, host)
	if err != nil {
		return Measurement{Target: host, Value: Unreachable, Detail: fmt.Sprintf("The host name could not be resolved: %v", err)}
	}
	var total time.Duration
	received := 0
	var lastErr error
	for i := 0; i < count; i++ {
		if ctx.Err() != nil {
			break
		}
		rtt, err := pingOnce(ctx, ip, uint16(i+1), 2*time.Second)
		if err != nil {
			lastErr = err
			continue
		}
		received++
		total += rtt
	}
	if received == 0 {
		detail := fmt.Sprintf("No reply from %s (0 of %d)", ip, count)
		if lastErr != nil && !errors.Is(lastErr, errNoReply) {
			detail += ": " + lastErr.Error()
		}
		return Measurement{Target: host, Value: Unreachable, Detail: detail}
	}
	avg := float64(total) / float64(received) / float64(time.Millisecond)
	return Measurement{Target: host, Value: round(avg, 1), Detail: fmt.Sprintf("%d of %d replies from %s, %.1f ms average", received, count, ip, avg)}
}

// errNoReply means an echo request timed out without an answer.
var errNoReply = errors.New("no reply")

func tcpCheck(ctx context.Context, params map[string]string) Measurement {
	host := strings.TrimSpace(params["host"])
	port := intParam(params, "port", 0, 1, 65535)
	target := net.JoinHostPort(strings.Trim(host, "[]"), strconv.Itoa(port))
	if !validHost(host) || port == 0 {
		return Measurement{Target: target, Error: "the TCP check needs a valid host and port"}
	}
	timeout := time.Duration(intParam(params, "timeout_seconds", 5, 1, 60)) * time.Second
	dialer := net.Dialer{Timeout: timeout}
	start := time.Now()
	conn, err := dialer.DialContext(ctx, "tcp", target)
	if err != nil {
		return Measurement{Target: target, Value: Unreachable, Detail: fmt.Sprintf("Could not connect: %v", err)}
	}
	elapsed := time.Since(start)
	_ = conn.Close()
	ms := float64(elapsed) / float64(time.Millisecond)
	return Measurement{Target: target, Value: round(ms, 1), Detail: fmt.Sprintf("Connected in %.1f ms", ms)}
}

// CertificateTarget is the target of the certificate result of an HTTP(S) check.
const CertificateTarget = "certificate"

func httpCheck(ctx context.Context, params map[string]string) []Measurement {
	raw := strings.TrimSpace(params["url"])
	u, err := url.Parse(raw)
	if err != nil || (u.Scheme != "http" && u.Scheme != "https") || u.Host == "" || u.User != nil || hasControlChars(raw) {
		return []Measurement{{Error: "the HTTP check has no valid http or https URL"}}
	}
	ranges, ok := parseStatusRanges(params["expected_status"])
	if !ok {
		return []Measurement{{Error: "the HTTP check has an invalid expected_status parameter"}}
	}
	contains := params["contains"]
	if hasControlChars(contains) || len(contains) > 200 {
		return []Measurement{{Error: "the HTTP check has an invalid contains parameter"}}
	}
	timeout := time.Duration(intParam(params, "timeout_seconds", 10, 1, 60)) * time.Second
	insecure := params["ignore_certificate_errors"] == "true"

	transport := &http.Transport{
		Proxy:                 nil,
		DisableKeepAlives:     true,
		TLSHandshakeTimeout:   timeout,
		ResponseHeaderTimeout: timeout,
		// #nosec G402 -- only when the technician chose to accept an untrusted certificate for an internal site.
		TLSClientConfig: &tls.Config{MinVersion: tls.VersionTLS12, InsecureSkipVerify: insecure},
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{
		Transport: transport,
		Timeout:   timeout,
		CheckRedirect: func(req *http.Request, via []*http.Request) error {
			if len(via) >= 5 {
				return errors.New("more than 5 redirects")
			}
			if req.URL.Scheme != "http" && req.URL.Scheme != "https" {
				return fmt.Errorf("redirect to unsupported scheme %s", req.URL.Scheme)
			}
			return nil
		},
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodGet, u.String(), nil)
	if err != nil {
		return []Measurement{{Error: fmt.Sprintf("the request could not be built: %v", err)}}
	}
	req.Header.Set("User-Agent", "Fleeto-Agent-Check")
	start := time.Now()
	resp, err := client.Do(req)
	if err != nil {
		return []Measurement{{Value: Unreachable, Detail: describeHTTPError(err)}}
	}
	defer resp.Body.Close()
	var body []byte
	if contains != "" {
		body, _ = io.ReadAll(io.LimitReader(resp.Body, maxBodyBytes))
	} else {
		_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, maxBodyBytes))
	}
	ms := float64(time.Since(start)) / float64(time.Millisecond)

	var out []Measurement
	switch {
	case !statusAllowed(resp.StatusCode, ranges):
		out = append(out, Measurement{Value: Unreachable, Detail: fmt.Sprintf("Status %d, expected %s (%.0f ms)", resp.StatusCode, displayRanges(params["expected_status"]), ms)})
	case contains != "" && !strings.Contains(string(body), contains):
		out = append(out, Measurement{Value: Unreachable, Detail: fmt.Sprintf("Status %d, but the response does not contain the expected text (%.0f ms)", resp.StatusCode, ms)})
	default:
		out = append(out, Measurement{Value: round(ms, 1), Detail: fmt.Sprintf("Status %d in %.0f ms", resp.StatusCode, ms)})
	}
	if resp.TLS != nil && len(resp.TLS.PeerCertificates) > 0 {
		leaf := resp.TLS.PeerCertificates[0]
		days := leaf.NotAfter.Sub(time.Now()).Hours() / 24
		out = append(out, Measurement{Target: CertificateTarget, Value: round(days, 1),
			Detail: fmt.Sprintf("Expires %s, issued by %s", leaf.NotAfter.UTC().Format("2006-01-02"), leaf.Issuer.CommonName)})
	}
	return out
}

func describeHTTPError(err error) string {
	var certErr *tls.CertificateVerificationError
	switch {
	case errors.As(err, &certErr):
		return "The TLS certificate is not trusted: " + certErr.Err.Error()
	case errors.Is(err, context.DeadlineExceeded) || strings.Contains(err.Error(), "Client.Timeout"):
		return "No response within the timeout"
	default:
		return "The request failed: " + err.Error()
	}
}

type statusRange struct{ low, high int }

// parseStatusRanges parses "200-399" or "200,301-302". Empty means 200-399.
func parseStatusRanges(value string) ([]statusRange, bool) {
	value = strings.TrimSpace(value)
	if value == "" {
		return []statusRange{{200, 399}}, true
	}
	var out []statusRange
	for _, part := range strings.Split(value, ",") {
		bounds := strings.SplitN(strings.TrimSpace(part), "-", 2)
		low, err := strconv.Atoi(bounds[0])
		if err != nil || low < 100 || low > 599 {
			return nil, false
		}
		high := low
		if len(bounds) == 2 {
			high, err = strconv.Atoi(bounds[1])
			if err != nil || high < low || high > 599 {
				return nil, false
			}
		}
		out = append(out, statusRange{low, high})
	}
	return out, len(out) > 0
}

func statusAllowed(code int, ranges []statusRange) bool {
	for _, r := range ranges {
		if code >= r.low && code <= r.high {
			return true
		}
	}
	return false
}

func displayRanges(value string) string {
	if strings.TrimSpace(value) == "" {
		return "200-399"
	}
	return value
}
