package connect

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"net/http/httputil"
	"strings"
	"sync"
	"time"
)

const (
	// gatePath is the one-time address Fleeto Connect opens the browser at; it sets the cookie and redirects to the web interface.
	gatePath = "/.fleeto-connect/"
	// cookieName is the cookie that lets the browser in. Removed before a request reaches pveproxy.
	cookieName = "fleeto_connect"
	// upstreamHost is what pveproxy sees as Host and Origin: the connection arrives on its loopback address.
	upstreamHost = "127.0.0.1:8006"
)

// Dialer opens one raw connection to pveproxy through the session.
type Dialer func(ctx context.Context) (net.Conn, error)

// Proxy serves the web interface of the host to the browser on a loopback address.
type Proxy struct {
	// URL is where the browser opens the web interface the first time; it works once.
	URL string
	// Base is the address of the web interface without the one-time part, for messages.
	Base string

	server    *http.Server
	listeners []net.Listener
	gate      string
	cookie    string
	gateMu    sync.Mutex
	gateUsed  bool
	logger    *slog.Logger
}

// NewProxy listens on 127.0.0.1 with a port the system chooses, under a host name of the endpoint below localhost (the browser resolves
// *.localhost to loopback, treats it as a secure context and keeps the cookies of every host name apart), and forwards to pveproxy over
// TLS that accepts only the certificate with the given SHA-256.
func NewProxy(dial Dialer, certificate, hostLabel string, logger *slog.Logger) (*Proxy, error) {
	if logger == nil {
		logger = slog.New(slog.DiscardHandler)
	}
	pin, err := hex.DecodeString(certificate)
	if err != nil || len(pin) != sha256.Size {
		return nil, errors.New("the endpoint reported no valid certificate of the Proxmox web interface")
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		return nil, fmt.Errorf("Fleeto Connect could not open a local address: %w", err)
	}
	p := &Proxy{listeners: []net.Listener{listener}, gate: randomToken(), cookie: randomToken(), logger: logger}
	port := listener.Addr().(*net.TCPAddr).Port
	// Browsers try ::1 first for a name below localhost; without IPv6 they use 127.0.0.1 alone.
	if v6, err := net.Listen("tcp", fmt.Sprintf("[::1]:%d", port)); err == nil {
		p.listeners = append(p.listeners, v6)
	}
	p.Base = fmt.Sprintf("http://%s.localhost:%d/", hostLabel, port)
	p.URL = p.Base + strings.TrimPrefix(gatePath, "/") + p.gate

	transport := &http.Transport{
		Proxy: nil,
		DialTLSContext: func(ctx context.Context, _, _ string) (net.Conn, error) {
			raw, err := dial(ctx)
			if err != nil {
				return nil, err
			}
			conn := tls.Client(raw, &tls.Config{
				MinVersion: tls.VersionTLS12,
				// pveproxy usually serves a certificate of the cluster's own CA or a self-signed one; it is accepted by its exact hash, which
				// the agent read from pveproxy on the host itself, and nothing else.
				InsecureSkipVerify: true, //nolint:gosec // verified by VerifyConnection below
				VerifyConnection: func(state tls.ConnectionState) error {
					if len(state.PeerCertificates) == 0 {
						return errors.New("pveproxy presented no certificate")
					}
					sum := sha256.Sum256(state.PeerCertificates[0].Raw)
					if subtle.ConstantTimeCompare(sum[:], pin) != 1 {
						return errors.New("pveproxy presented another certificate than the endpoint reported")
					}
					return nil
				},
				NextProtos: []string{"http/1.1"},
			})
			if err := conn.HandshakeContext(ctx); err != nil {
				_ = raw.Close()
				return nil, err
			}
			return conn, nil
		},
		ForceAttemptHTTP2:     false,
		TLSNextProto:          map[string]func(string, *tls.Conn) http.RoundTripper{},
		MaxIdleConnsPerHost:   8,
		IdleConnTimeout:       90 * time.Second,
		ResponseHeaderTimeout: 5 * time.Minute,
	}
	reverse := &httputil.ReverseProxy{
		Rewrite: func(r *httputil.ProxyRequest) {
			r.Out.URL.Scheme = "https"
			r.Out.URL.Host = upstreamHost
			r.Out.Host = upstreamHost
			removeCookie(r.Out)
			if r.Out.Header.Get("Origin") != "" {
				r.Out.Header.Set("Origin", "https://"+upstreamHost)
			}
			if referer := r.Out.Header.Get("Referer"); referer != "" {
				if i := strings.Index(referer, "://"); i >= 0 {
					rest := referer[i+3:]
					if j := strings.IndexByte(rest, '/'); j >= 0 {
						r.Out.Header.Set("Referer", "https://"+upstreamHost+rest[j:])
					}
				}
			}
			r.Out.Header.Del("X-Forwarded-For")
		},
		Transport:     transport,
		FlushInterval: -1,
		ErrorHandler: func(w http.ResponseWriter, r *http.Request, err error) {
			p.logger.Warn("a request to the Proxmox web interface failed", "error", err)
			http.Error(w, "Fleeto Connect could not reach the Proxmox web interface: "+err.Error(), http.StatusBadGateway)
		},
	}
	p.server = &http.Server{Handler: p.guard(reverse), ReadHeaderTimeout: 30 * time.Second}
	return p, nil
}

// Serve serves until Close.
func (p *Proxy) Serve() error {
	errs := make(chan error, len(p.listeners))
	for _, listener := range p.listeners {
		go func() { errs <- p.server.Serve(listener) }()
	}
	err := <-errs
	if errors.Is(err, http.ErrServerClosed) {
		return nil
	}
	return err
}

// Close stops the local address and every connection through it.
func (p *Proxy) Close() error { return p.server.Close() }

// guard lets in only the browser that opened the one-time address: other local users and programs get 403.
func (p *Proxy) guard(next http.Handler) http.Handler {
	return http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasSuffix(strings.ToLower(hostOnly(r.Host)), ".localhost") {
			// A name that is not ours (DNS rebinding) never reaches the host.
			http.Error(w, "Forbidden", http.StatusForbidden)
			return
		}
		if strings.HasPrefix(r.URL.Path, gatePath) {
			p.open(w, r)
			return
		}
		cookie, err := r.Cookie(cookieName)
		if err != nil || subtle.ConstantTimeCompare([]byte(cookie.Value), []byte(p.cookie)) != 1 {
			http.Error(w, "This address belongs to a Fleeto Connect session of another browser. Open the Proxmox web interface from Fleeto.",
				http.StatusForbidden)
			return
		}
		next.ServeHTTP(w, r)
	})
}

func (p *Proxy) open(w http.ResponseWriter, r *http.Request) {
	given := strings.TrimPrefix(r.URL.Path, gatePath)
	p.gateMu.Lock()
	ok := !p.gateUsed && subtle.ConstantTimeCompare([]byte(given), []byte(p.gate)) == 1
	if ok {
		p.gateUsed = true
	}
	p.gateMu.Unlock()
	if !ok {
		http.Error(w, "This link of Fleeto Connect was used before. Open the Proxmox web interface from Fleeto again.", http.StatusForbidden)
		return
	}
	http.SetCookie(w, &http.Cookie{Name: cookieName, Value: p.cookie, Path: "/", HttpOnly: true, SameSite: http.SameSiteStrictMode})
	w.Header().Set("Cache-Control", "no-store")
	http.Redirect(w, r, "/", http.StatusSeeOther)
}

func removeCookie(r *http.Request) {
	cookies := r.Cookies()
	r.Header.Del("Cookie")
	for _, c := range cookies {
		if c.Name != cookieName {
			r.AddCookie(c)
		}
	}
}

func hostOnly(host string) string {
	if h, _, err := net.SplitHostPort(host); err == nil {
		return h
	}
	return host
}

func randomToken() string {
	b := make([]byte, 32)
	if _, err := rand.Read(b); err != nil {
		panic(err)
	}
	return base64.RawURLEncoding.EncodeToString(b)
}

// HostLabel is the host name of an endpoint below localhost: its name made safe, then a part of its id, so two endpoints with the same
// name never share the cookies of their sign-in.
func HostLabel(hostname, endpointID string) string {
	var b strings.Builder
	for _, r := range strings.ToLower(hostname) {
		switch {
		case r >= 'a' && r <= 'z', r >= '0' && r <= '9':
			b.WriteRune(r)
		case r == '-' || r == '.' || r == '_':
			b.WriteByte('-')
		}
		if b.Len() >= 40 {
			break
		}
	}
	label := strings.Trim(b.String(), "-")
	if label == "" {
		label = "proxmox"
	}
	id := strings.ToLower(strings.ReplaceAll(endpointID, "-", ""))
	if len(id) > 8 {
		id = id[:8]
	}
	return label + "-" + id
}
