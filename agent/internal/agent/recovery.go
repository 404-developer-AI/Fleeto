package agent

import (
	"bytes"
	"context"
	"crypto/rand"
	"crypto/tls"
	"errors"
	"fmt"
	"io"
	"mime"
	"net/http"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/enroll"
	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
)

const (
	// RecoverPath renews an expired, never revoked agent certificate with that certificate.
	RecoverPath = "/v1/recover"
	// CertificateStateHeader on a 401 from the connect path says the certificate expired and can be recovered.
	CertificateStateHeader  = "Fleeto-Certificate"
	certificateExpiredValue = "expired"
)

// errRecoveryRefused means the gateway will not recover this certificate; only enrolling again helps.
var errRecoveryRefused = errors.New("certificate recovery refused")

// recoverCertificate renews an expired certificate over mTLS with that certificate (the gateway accepts it for this request
// only, within a year after expiry). The CSR uses the same key. On success the new certificate is stored and the session
// reconnects with it.
func (a *Agent) recoverCertificate(ctx context.Context, st *state.State, tlsConf *tls.Config) error {
	now := a.opts.Now()
	if now.Before(a.nextRecovery) {
		return fmt.Errorf("the agent certificate has expired; the next recovery attempt is at %s", a.nextRecovery.UTC().Format(time.RFC3339))
	}
	a.nextRecovery = now.Add(a.opts.RenewalRetry)

	hostname := inventory.Hostname()
	csr, err := keystore.CreateCSR(rand.Reader, a.key, hostname)
	if err != nil {
		return fmt.Errorf("certificate recovery: %w", err)
	}
	body, err := proto.Marshal(&agentv1.RecoverRequest{CsrDer: csr, Hostname: hostname, AgentVersion: version.Version})
	if err != nil {
		return fmt.Errorf("encode the recovery request: %w", err)
	}

	transport := &http.Transport{Proxy: nil, TLSClientConfig: tlsConf, TLSHandshakeTimeout: a.opts.HandshakeTimeout}
	defer transport.CloseIdleConnections()
	client := &http.Client{
		Transport: transport,
		Timeout:   90 * time.Second,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return errors.New("the gateway answered with a redirect, which recovery does not follow")
		},
	}
	req, err := http.NewRequestWithContext(ctx, http.MethodPost, "https://"+st.Server+RecoverPath, bytes.NewReader(body))
	if err != nil {
		return fmt.Errorf("build the recovery request: %w", err)
	}
	req.Header.Set("Content-Type", enroll.ContentType)
	req.Header.Set("Accept", enroll.ContentType+", application/problem+json")
	req.Header.Set("User-Agent", "fleetify-agent/"+version.Version)

	resp, err := client.Do(req)
	if err != nil {
		return fmt.Errorf("cannot reach the gateway at %s for certificate recovery: %w", st.Server, err)
	}
	defer resp.Body.Close()
	data, err := io.ReadAll(io.LimitReader(resp.Body, enroll.MaxResponseBytes+1))
	if err != nil {
		return fmt.Errorf("read the recovery response: %w", err)
	}
	if len(data) > enroll.MaxResponseBytes {
		return errors.New("the recovery response is too large")
	}
	if resp.StatusCode != http.StatusOK {
		detail := enroll.ProblemDetail(resp, data)
		if resp.StatusCode == http.StatusUnauthorized {
			a.logger.Error("the gateway refused to recover the expired certificate; enroll the agent again from the endpoint menu",
				"reason", detail, "retryAt", a.nextRecovery.UTC().Format(time.RFC3339))
			return fmt.Errorf("%w: %s", errRecoveryRefused, detail)
		}
		return fmt.Errorf("certificate recovery failed (HTTP %d): %s", resp.StatusCode, detail)
	}
	if mt, _, _ := mime.ParseMediaType(resp.Header.Get("Content-Type")); mt != enroll.ContentType {
		return fmt.Errorf("the gateway answered the recovery with content type %q", resp.Header.Get("Content-Type"))
	}
	var out agentv1.RecoverResponse
	if err := proto.Unmarshal(data, &out); err != nil {
		return fmt.Errorf("the recovery response cannot be parsed: %w", err)
	}

	ca, err := st.CACertificate()
	if err != nil {
		return err
	}
	cert, err := enroll.ValidateAgentCertificate(out.GetCertificateDer(), ca, a.key, st.EndpointID, now)
	if err != nil {
		return fmt.Errorf("the recovered certificate is invalid: %w", err)
	}
	updated, err := a.store.Update(func(s *state.State) error {
		s.CertificatePEM = state.EncodeCertificatePEM(cert.Raw)
		s.CertificateRenewedAt = now.UTC()
		return nil
	})
	if err != nil {
		return fmt.Errorf("store the recovered certificate: %w", err)
	}
	a.mu.Lock()
	a.st = updated
	a.mu.Unlock()
	a.nextRecovery = time.Time{}
	a.nextRenewal = time.Time{}
	a.logger.Info("expired certificate recovered", "expires", cert.NotAfter.UTC().Format(time.RFC3339))
	return nil
}
