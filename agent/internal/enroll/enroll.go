// Package enroll exchanges an enrollment token for an agent certificate.
//
// The agent has no pinned CA before enrollment. Its only trust anchor is the CA fingerprint from the install command,
// which the technician copied from the web UI over HTTPS. Enrollment is two steps:
//
//  1. GET /v1/ca without verification and without sending anything secret. The response is a PEM bundle; the agent
//     keeps only the CA certificate whose SHA-256 equals the fingerprint and refuses to continue if there is none.
//     (TLS stacks leave a self-signed root out of the handshake, so the CA cannot be taken from the chain.)
//  2. POST /v1/enroll with the token over a new connection verified normally against that CA as the only root and for
//     the server host name. A gateway that cannot prove it holds a certificate from the pinned CA never sees the token.
package enroll

import (
	"bytes"
	"context"
	"crypto"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"crypto/subtle"
	"crypto/tls"
	"crypto/x509"
	"encoding/hex"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"mime"
	"net"
	"net/http"
	"strconv"
	"strings"
	"time"
	"unicode"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

const (
	// Path of the enrollment endpoint on the gateway.
	Path = "/v1/enroll"
	// ContentType of protobuf bodies.
	ContentType = "application/x-protobuf"
	// MaxResponseBytes bounds the response the agent reads.
	MaxResponseBytes = 1 << 20
	// TokenPrefix is the prefix of enrollment tokens.
	TokenPrefix = "fet_"
)

// Request holds everything needed to enroll.
type Request struct {
	Server        string
	Token         string
	CAFingerprint string
	Key           crypto.Signer
	Hostname      string
	OS            *agentv1.OsInfo
	AgentVersion  string
	// Timeout for the whole exchange. The gateway waits up to 30 s for the signer. Default 90 s.
	Timeout time.Duration
}

// Result is the verified enrollment response.
type Result struct {
	EndpointID       string
	InstanceID       string
	CertificateDER   []byte
	Certificate      *x509.Certificate
	CACertificateDER []byte
	CACertificate    *x509.Certificate
	SigningKey       ed25519.PublicKey
	SigningKeyID     string
}

// Error is an enrollment failure with a message for the technician.
type Error struct {
	Status int
	Detail string
}

func (e *Error) Error() string {
	if e.Status == 0 {
		return e.Detail
	}
	return fmt.Sprintf("the gateway refused the enrollment (HTTP %d): %s", e.Status, e.Detail)
}

// ValidateServer checks a host:port value.
func ValidateServer(server string) (host string, err error) {
	if strings.Contains(server, "://") || strings.ContainsAny(server, "/?#@ ") {
		return "", fmt.Errorf("--server must be host:port, e.g. agents.rmm.example:443, not %q", server)
	}
	host, port, err := net.SplitHostPort(server)
	if err != nil || host == "" {
		return "", fmt.Errorf("--server must be host:port, e.g. agents.rmm.example:443, not %q", server)
	}
	if p, err := strconv.Atoi(port); err != nil || p < 1 || p > 65535 {
		return "", fmt.Errorf("the port in --server %q is invalid", server)
	}
	return host, nil
}

// ValidateToken checks the shape of an enrollment token without revealing it in the error.
func ValidateToken(token string) error {
	if !strings.HasPrefix(token, TokenPrefix) || len(token) < len(TokenPrefix)+8 || len(token) > 512 {
		return errors.New("--token must be an enrollment token starting with fet_; copy the install command from the site page again")
	}
	for _, r := range token {
		if r > unicode.MaxASCII || unicode.IsSpace(r) || unicode.IsControl(r) {
			return errors.New("--token contains invalid characters; copy the install command from the site page again")
		}
	}
	return nil
}

// ParseFingerprint accepts 64 hex characters, optionally separated by colons, and returns the 32 bytes.
func ParseFingerprint(value string) ([]byte, error) {
	cleaned := strings.ToLower(strings.NewReplacer(":", "", " ", "", "-", "").Replace(strings.TrimSpace(value)))
	raw, err := hex.DecodeString(cleaned)
	if err != nil || len(raw) != sha256.Size {
		return nil, errors.New("--ca-fingerprint must be the SHA-256 fingerprint of the instance CA certificate (64 hex characters)")
	}
	return raw, nil
}

// CAPath is the public endpoint that serves the instance CA certificates as PEM.
const CAPath = "/v1/ca"

// maxCABundleBytes bounds the CA bundle the agent reads before anything is verified.
const maxCABundleBytes = 64 << 10

// SelectPinnedCA returns the CA certificate in a PEM bundle whose SHA-256 equals fingerprint.
func SelectPinnedCA(bundle []byte, fingerprint []byte) (*x509.Certificate, error) {
	rest := bundle
	for {
		var block *pem.Block
		block, rest = pem.Decode(rest)
		if block == nil {
			break
		}
		if block.Type != "CERTIFICATE" {
			continue
		}
		sum := sha256.Sum256(block.Bytes)
		if subtle.ConstantTimeCompare(sum[:], fingerprint) != 1 {
			continue
		}
		cert, err := x509.ParseCertificate(block.Bytes)
		if err != nil || !cert.IsCA || !cert.BasicConstraintsValid {
			return nil, errors.New("the certificate matching --ca-fingerprint is not a valid CA certificate")
		}
		return cert, nil
	}
	return nil, errors.New("the gateway does not serve the instance CA from --ca-fingerprint: " +
		"check that --server points at this instance and copy the install command again")
}

// fetchPinnedCA downloads the CA bundle over an unverified connection (nothing secret is sent) and selects the CA
// matching the fingerprint.
func fetchPinnedCA(ctx context.Context, server, host string, fingerprint []byte) (*x509.Certificate, error) {
	transport := &http.Transport{
		Proxy: nil,
		TLSClientConfig: &tls.Config{
			MinVersion: tls.VersionTLS12,
			ServerName: host,
			// No trust anchor exists yet. The response is only used after its fingerprint matches, and the token is
			// sent later over a connection verified against that CA.
			InsecureSkipVerify: true, //nolint:gosec // see comment above
		},
		TLSHandshakeTimeout: 15 * time.Second,
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{
		Transport: transport,
		Timeout:   30 * time.Second,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return errors.New("the gateway answered with a redirect, which enrollment does not follow")
		},
	}
	httpReq, err := http.NewRequestWithContext(ctx, http.MethodGet, "https://"+server+CAPath, nil)
	if err != nil {
		return nil, fmt.Errorf("build CA request: %w", err)
	}
	resp, err := client.Do(httpReq)
	if err != nil {
		return nil, fmt.Errorf("cannot reach the gateway at %s: %w", server, err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, maxCABundleBytes+1))
	if err != nil {
		return nil, fmt.Errorf("read the instance CA from the gateway: %w", err)
	}
	if resp.StatusCode != http.StatusOK {
		return nil, &Error{Status: resp.StatusCode, Detail: ProblemDetail(resp, data)}
	}
	if len(data) > maxCABundleBytes {
		return nil, errors.New("the CA bundle from the gateway is too large")
	}
	return SelectPinnedCA(data, fingerprint)
}

// Enroll performs the enrollment exchange and verifies the response.
func Enroll(ctx context.Context, req Request) (*Result, error) {
	host, err := ValidateServer(req.Server)
	if err != nil {
		return nil, err
	}
	if err := ValidateToken(req.Token); err != nil {
		return nil, err
	}
	fingerprint, err := ParseFingerprint(req.CAFingerprint)
	if err != nil {
		return nil, err
	}
	csr, err := keystore.CreateCSR(rand.Reader, req.Key, req.Hostname)
	if err != nil {
		return nil, err
	}
	body, err := proto.Marshal(&agentv1.EnrollRequest{
		Token: req.Token, CsrDer: csr, Hostname: req.Hostname, Os: req.OS, AgentVersion: req.AgentVersion,
	})
	if err != nil {
		return nil, fmt.Errorf("encode enrollment request: %w", err)
	}

	timeout := req.Timeout
	if timeout == 0 {
		timeout = 90 * time.Second
	}
	ca, err := fetchPinnedCA(ctx, req.Server, host, fingerprint)
	if err != nil {
		return nil, err
	}
	roots := x509.NewCertPool()
	roots.AddCert(ca)
	transport := &http.Transport{
		// No proxy: agent traffic goes straight to the gateway, whose certificate is verified end to end.
		Proxy: nil,
		TLSClientConfig: &tls.Config{
			MinVersion: tls.VersionTLS12,
			ServerName: host,
			// Standard verification with the pinned instance CA as the only root.
			RootCAs: roots,
		},
		ForceAttemptHTTP2:   true,
		TLSHandshakeTimeout: 15 * time.Second,
	}
	defer transport.CloseIdleConnections()
	client := &http.Client{
		Transport: transport,
		Timeout:   timeout,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return errors.New("the gateway answered with a redirect, which enrollment does not follow")
		},
	}

	httpReq, err := http.NewRequestWithContext(ctx, http.MethodPost, "https://"+req.Server+Path, bytes.NewReader(body))
	if err != nil {
		return nil, fmt.Errorf("build enrollment request: %w", err)
	}
	httpReq.Header.Set("Content-Type", ContentType)
	httpReq.Header.Set("Accept", ContentType+", application/problem+json")
	httpReq.Header.Set("User-Agent", "fleeto-agent/"+req.AgentVersion)

	resp, err := client.Do(httpReq)
	if err != nil {
		var unknownAuthority x509.UnknownAuthorityError
		var hostname x509.HostnameError
		switch {
		case errors.As(err, &unknownAuthority):
			return nil, errors.New("the gateway certificate does not belong to the instance CA from --ca-fingerprint: " +
				"check that --server points at this instance and copy the install command again")
		case errors.As(err, &hostname):
			return nil, fmt.Errorf("the gateway certificate is not valid for %s under the pinned instance CA: %w", host, err)
		}
		return nil, fmt.Errorf("cannot reach the gateway at %s: %w", req.Server, err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, MaxResponseBytes+1))
	if err != nil {
		return nil, fmt.Errorf("read enrollment response: %w", err)
	}
	if len(data) > MaxResponseBytes {
		return nil, errors.New("the enrollment response is too large")
	}
	if resp.StatusCode != http.StatusOK {
		return nil, &Error{Status: resp.StatusCode, Detail: ProblemDetail(resp, data)}
	}
	if mt, _, _ := mime.ParseMediaType(resp.Header.Get("Content-Type")); mt != ContentType {
		return nil, fmt.Errorf("the gateway answered with content type %q instead of %s", resp.Header.Get("Content-Type"), ContentType)
	}
	var out agentv1.EnrollResponse
	if err := proto.Unmarshal(data, &out); err != nil {
		return nil, fmt.Errorf("the enrollment response cannot be parsed: %w", err)
	}
	return verifyResponse(&out, fingerprint, req.Key, time.Now())
}

func verifyResponse(out *agentv1.EnrollResponse, fingerprint []byte, key crypto.Signer, now time.Time) (*Result, error) {
	sum := sha256.Sum256(out.GetCaCertificateDer())
	if subtle.ConstantTimeCompare(sum[:], fingerprint) != 1 {
		return nil, errors.New("the CA certificate in the enrollment response does not match --ca-fingerprint")
	}
	ca, err := x509.ParseCertificate(out.GetCaCertificateDer())
	if err != nil || !ca.IsCA {
		return nil, errors.New("the CA certificate in the enrollment response is invalid")
	}
	if out.GetEndpointId() == "" || out.GetInstanceId() == "" {
		return nil, errors.New("the enrollment response has no endpoint or instance id")
	}
	cert, err := ValidateAgentCertificate(out.GetCertificateDer(), ca, key, out.GetEndpointId(), now)
	if err != nil {
		return nil, err
	}
	if len(out.GetInstanceSigningPublicKey()) != ed25519.PublicKeySize || out.GetInstanceSigningKeyId() == "" {
		return nil, errors.New("the enrollment response has no valid instance signing public key")
	}
	return &Result{
		EndpointID:       out.GetEndpointId(),
		InstanceID:       out.GetInstanceId(),
		CertificateDER:   out.GetCertificateDer(),
		Certificate:      cert,
		CACertificateDER: out.GetCaCertificateDer(),
		CACertificate:    ca,
		SigningKey:       ed25519.PublicKey(out.GetInstanceSigningPublicKey()),
		SigningKeyID:     out.GetInstanceSigningKeyId(),
	}, nil
}

// ValidateAgentCertificate checks an issued agent certificate: it carries our public key, chains to the pinned CA
// for client authentication, is currently valid and names the endpoint.
func ValidateAgentCertificate(der []byte, ca *x509.Certificate, key crypto.Signer, endpointID string, now time.Time) (*x509.Certificate, error) {
	cert, err := x509.ParseCertificate(der)
	if err != nil {
		return nil, fmt.Errorf("the agent certificate cannot be parsed: %w", err)
	}
	if !keystore.SamePublicKey(cert, key) {
		return nil, errors.New("the issued agent certificate does not carry this agent's public key")
	}
	roots := x509.NewCertPool()
	roots.AddCert(ca)
	if _, err := cert.Verify(x509.VerifyOptions{
		Roots:       roots,
		CurrentTime: now,
		KeyUsages:   []x509.ExtKeyUsage{x509.ExtKeyUsageClientAuth},
	}); err != nil {
		return nil, fmt.Errorf("the issued agent certificate does not chain to the instance CA: %w", err)
	}
	want := "urn:fleeto:endpoint:" + strings.ToLower(endpointID)
	for _, uri := range cert.URIs {
		if strings.EqualFold(uri.String(), want) {
			return cert, nil
		}
	}
	return nil, errors.New("the issued agent certificate is not issued for this endpoint")
}

type problem struct {
	Title  string `json:"title"`
	Detail string `json:"detail"`
}

// ProblemDetail extracts a technician-facing message from a problem+json body.
func ProblemDetail(resp *http.Response, data []byte) string {
	var p problem
	if mt, _, _ := mime.ParseMediaType(resp.Header.Get("Content-Type")); mt == "application/problem+json" || mt == "application/json" {
		_ = json.Unmarshal(data, &p)
	}
	msg := p.Detail
	if msg == "" {
		msg = p.Title
	}
	if msg == "" {
		msg = http.StatusText(resp.StatusCode)
	}
	return sanitize(msg, 500)
}

// sanitize removes control characters so a server message cannot rewrite the technician's terminal.
func sanitize(s string, max int) string {
	var b strings.Builder
	for _, r := range s {
		if unicode.IsControl(r) {
			r = ' '
		}
		b.WriteRune(r)
		if b.Len() >= max {
			break
		}
	}
	return strings.TrimSpace(b.String())
}
