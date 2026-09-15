package enroll

import (
	"context"
	"crypto/ecdsa"
	"crypto/ed25519"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/tls"
	"encoding/pem"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/testpki"
)

const (
	testEndpoint = "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	testInstance = "6f1d3c1e-5a4b-4f7e-9a31-2b8f0c1d2e3f"
	testToken    = "fet_abcdef_0123456789secret" // gitleaks:allow (test value, never a real token)
)

type fakeGateway struct {
	server *httptest.Server
	// requests counts enrollment POSTs only: the requests that carry the token.
	requests atomic.Int32
	ca       *testpki.CA
	// servedCA is what GET /v1/ca returns; defaults to ca.
	servedCA *testpki.CA
	// respond overrides the response; nil issues a certificate from ca.
	respond func(w http.ResponseWriter, req *agentv1.EnrollRequest)
}

func newFakeGateway(t *testing.T, ca *testpki.CA, serverCert tls.Certificate) *fakeGateway {
	t.Helper()
	g := &fakeGateway{ca: ca, servedCA: ca}
	g.server = httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.Method == http.MethodGet && r.URL.Path == CAPath {
			w.Header().Set("Content-Type", "application/x-pem-file")
			_ = pem.Encode(w, &pem.Block{Type: "CERTIFICATE", Bytes: g.servedCA.Cert.Raw})
			return
		}
		g.requests.Add(1)
		body, _ := io.ReadAll(r.Body)
		var req agentv1.EnrollRequest
		if r.URL.Path != Path || r.Header.Get("Content-Type") != ContentType || proto.Unmarshal(body, &req) != nil {
			http.Error(w, "bad request", http.StatusBadRequest)
			return
		}
		if g.respond != nil {
			g.respond(w, &req)
			return
		}
		if req.GetToken() != testToken {
			w.Header().Set("Content-Type", "application/problem+json")
			w.WriteHeader(http.StatusUnauthorized)
			_, _ = w.Write([]byte(`{"title":"Unauthorized","status":401,"detail":"The enrollment token has expired. Create a new token for the site and run the install command again."}`))
			return
		}
		certDER, err := ca.IssueAgent(req.GetCsrDer(), testEndpoint, testInstance, time.Now().Add(-time.Minute), 90*24*time.Hour)
		if err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		writeResponse(w, &agentv1.EnrollResponse{
			EndpointId: testEndpoint, InstanceId: testInstance, CertificateDer: certDER, CaCertificateDer: ca.Cert.Raw,
			InstanceSigningPublicKey: make([]byte, ed25519.PublicKeySize), InstanceSigningKeyId: "a1b2c3d4e5f60718",
		})
	}))
	g.server.TLS = &tls.Config{Certificates: []tls.Certificate{serverCert}}
	g.server.StartTLS()
	t.Cleanup(g.server.Close)
	return g
}

func writeResponse(w http.ResponseWriter, resp *agentv1.EnrollResponse) {
	data, _ := proto.Marshal(resp)
	w.Header().Set("Content-Type", ContentType)
	_, _ = w.Write(data)
}

func (g *fakeGateway) addr() string {
	return strings.TrimPrefix(g.server.URL, "https://")
}

func newRequest(t *testing.T, server, fingerprint string) Request {
	t.Helper()
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return Request{
		Server: server, Token: testToken, CAFingerprint: fingerprint, Key: key, Hostname: "host1",
		OS: &agentv1.OsInfo{Platform: "windows"}, AgentVersion: "0.1.0", Timeout: 10 * time.Second,
	}
}

func mustCA(t *testing.T) *testpki.CA {
	t.Helper()
	ca, err := testpki.NewCA("Fleeto Instance CA test")
	if err != nil {
		t.Fatal(err)
	}
	return ca
}

func mustServerCert(t *testing.T, ca *testpki.CA, names ...string) tls.Certificate {
	t.Helper()
	cert, err := ca.ServerCertificate(names...)
	if err != nil {
		t.Fatal(err)
	}
	return cert
}

func TestEnrollmentSucceedsWithMatchingFingerprint(t *testing.T) {
	ca := mustCA(t)
	g := newFakeGateway(t, ca, mustServerCert(t, ca, "127.0.0.1"))
	// Colon-separated upper-case fingerprints are accepted too.
	fp := strings.ToUpper(ca.Fingerprint()[:2]) + ":" + ca.Fingerprint()[2:]
	res, err := Enroll(context.Background(), newRequest(t, g.addr(), fp))
	if err != nil {
		t.Fatalf("enroll: %v", err)
	}
	if res.EndpointID != testEndpoint || res.InstanceID != testInstance || res.SigningKeyID == "" {
		t.Fatalf("unexpected result %+v", res)
	}
}

func TestEnrollmentIsRefusedBeforeSendingWhenFingerprintDiffers(t *testing.T) {
	ca := mustCA(t)
	other := mustCA(t)
	g := newFakeGateway(t, ca, mustServerCert(t, ca, "127.0.0.1"))
	_, err := Enroll(context.Background(), newRequest(t, g.addr(), other.Fingerprint()))
	if err == nil || !strings.Contains(err.Error(), "--ca-fingerprint") {
		t.Fatalf("expected fingerprint refusal, got %v", err)
	}
	if n := g.requests.Load(); n != 0 {
		t.Fatalf("the request must not be sent, but the gateway received %d", n)
	}
}

// Windows SChannel (and most TLS stacks) leave the self-signed root out of the handshake; the CA endpoint covers it.
func TestEnrollmentSucceedsWhenGatewayLeavesTheCAOutOfTheChain(t *testing.T) {
	ca := mustCA(t)
	cert := mustServerCert(t, ca, "127.0.0.1")
	cert.Certificate = cert.Certificate[:1]
	g := newFakeGateway(t, ca, cert)
	if _, err := Enroll(context.Background(), newRequest(t, g.addr(), ca.Fingerprint())); err != nil {
		t.Fatalf("enroll: %v", err)
	}
}

func TestEnrollmentIsRefusedBeforeSendingWhenTheServedCADoesNotMatch(t *testing.T) {
	ca := mustCA(t)
	g := newFakeGateway(t, ca, mustServerCert(t, ca, "127.0.0.1"))
	g.servedCA = mustCA(t)
	_, err := Enroll(context.Background(), newRequest(t, g.addr(), ca.Fingerprint()))
	if err == nil || !strings.Contains(err.Error(), "--ca-fingerprint") {
		t.Fatalf("expected fingerprint refusal, got %v", err)
	}
	if g.requests.Load() != 0 {
		t.Fatal("the request must not be sent")
	}
}

func TestEnrollmentIsRefusedWhenLeafIsFromAnotherCAButPinnedCAIsServed(t *testing.T) {
	pinned := mustCA(t)
	attacker := mustCA(t)
	cert := mustServerCert(t, attacker, "127.0.0.1")
	cert.Certificate = [][]byte{cert.Certificate[0], pinned.Cert.Raw}
	g := newFakeGateway(t, attacker, cert)
	g.servedCA = pinned
	if _, err := Enroll(context.Background(), newRequest(t, g.addr(), pinned.Fingerprint())); err == nil {
		t.Fatal("expected refusal of a leaf that does not chain to the pinned CA")
	}
	if g.requests.Load() != 0 {
		t.Fatal("the request must not be sent")
	}
}

func TestEnrollmentIsRefusedWhenLeafIsForAnotherHost(t *testing.T) {
	ca := mustCA(t)
	g := newFakeGateway(t, ca, mustServerCert(t, ca, "agents.other.example"))
	_, err := Enroll(context.Background(), newRequest(t, g.addr(), ca.Fingerprint()))
	if err == nil || !strings.Contains(err.Error(), "not valid for") {
		t.Fatalf("expected host name refusal, got %v", err)
	}
	if g.requests.Load() != 0 {
		t.Fatal("the request must not be sent")
	}
}

func TestProblemDetailIsShownToTheTechnician(t *testing.T) {
	ca := mustCA(t)
	g := newFakeGateway(t, ca, mustServerCert(t, ca, "127.0.0.1"))
	req := newRequest(t, g.addr(), ca.Fingerprint())
	req.Token = "fet_abcdef_wrongsecret"
	_, err := Enroll(context.Background(), req)
	var enrollErr *Error
	if err == nil || !asError(err, &enrollErr) || enrollErr.Status != 401 || !strings.Contains(enrollErr.Detail, "has expired") {
		t.Fatalf("expected problem detail, got %v", err)
	}
}

func TestResponseWithOtherCAOrForeignKeyIsRefused(t *testing.T) {
	ca := mustCA(t)
	other := mustCA(t)
	g := newFakeGateway(t, ca, mustServerCert(t, ca, "127.0.0.1"))

	g.respond = func(w http.ResponseWriter, req *agentv1.EnrollRequest) {
		certDER, _ := other.IssueAgent(req.GetCsrDer(), testEndpoint, testInstance, time.Now().Add(-time.Minute), time.Hour)
		writeResponse(w, &agentv1.EnrollResponse{EndpointId: testEndpoint, InstanceId: testInstance, CertificateDer: certDER,
			CaCertificateDer: other.Cert.Raw, InstanceSigningPublicKey: make([]byte, 32), InstanceSigningKeyId: "k"})
	}
	if _, err := Enroll(context.Background(), newRequest(t, g.addr(), ca.Fingerprint())); err == nil {
		t.Fatal("expected refusal of a response with a different CA")
	}

	g.respond = func(w http.ResponseWriter, _ *agentv1.EnrollRequest) {
		foreign := newRequest(t, "x:1", "")
		csr, _ := createCSRForTest(foreign)
		certDER, _ := ca.IssueAgent(csr, testEndpoint, testInstance, time.Now().Add(-time.Minute), time.Hour)
		writeResponse(w, &agentv1.EnrollResponse{EndpointId: testEndpoint, InstanceId: testInstance, CertificateDer: certDER,
			CaCertificateDer: ca.Cert.Raw, InstanceSigningPublicKey: make([]byte, 32), InstanceSigningKeyId: "k"})
	}
	_, err := Enroll(context.Background(), newRequest(t, g.addr(), ca.Fingerprint()))
	if err == nil || !strings.Contains(err.Error(), "public key") {
		t.Fatalf("expected refusal of a certificate for another key, got %v", err)
	}
}

func TestInputValidation(t *testing.T) {
	for _, s := range []string{"https://host:443", "host", "host:0", "host:99999", ":443", "user@host:443"} {
		if _, err := ValidateServer(s); err == nil {
			t.Errorf("server %q must be rejected", s)
		}
	}
	if _, err := ValidateServer("localhost:7200"); err != nil {
		t.Error(err)
	}
	for _, tok := range []string{"", "abc", "fet_", "fet_abc def12345", "flt_abcdefghijkl"} {
		if ValidateToken(tok) == nil {
			t.Errorf("token %q must be rejected", tok)
		}
	}
	if _, err := ParseFingerprint("abcd"); err == nil {
		t.Error("short fingerprint must be rejected")
	}
}
