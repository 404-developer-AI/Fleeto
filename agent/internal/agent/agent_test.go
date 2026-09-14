package agent

import (
	"context"
	"crypto"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/pem"
	"errors"
	"io"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/checks"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/testpki"
)

const (
	gwEndpoint = "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	gwInstance = "6f1d3c1e-5a4b-4f7e-9a31-2b8f0c1d2e3f"
	gwKeyID    = "a1b2c3d4e5f60718"
	gwToken    = "fet_testtoken_0123456789"
)

// gwConn is one accepted agent session on the fake gateway.
type gwConn struct {
	conn       *websocket.Conn
	clientCert *x509.Certificate
	done       chan struct{}
}

// fakeGateway implements the gateway side of protocol v1 closely enough to drive the agent.
type fakeGateway struct {
	t          *testing.T
	server     *httptest.Server
	ca         *testpki.CA
	signingPub ed25519.PublicKey
	signingKey ed25519.PrivateKey
	conns      chan *gwConn
	// certLifetime and certBackdate shape issued certificates, so a test can force a renewal.
	certLifetime time.Duration
	certBackdate time.Duration
	issued       atomic.Int32
	mu           sync.Mutex
	open         []*gwConn
}

func newFakeGateway(t *testing.T) *fakeGateway {
	t.Helper()
	ca, err := testpki.NewCA("Fleeto Instance CA test")
	if err != nil {
		t.Fatal(err)
	}
	pub, priv, _ := ed25519.GenerateKey(rand.Reader)
	g := &fakeGateway{t: t, ca: ca, signingPub: pub, signingKey: priv, conns: make(chan *gwConn, 8),
		certLifetime: 90 * 24 * time.Hour, certBackdate: time.Minute}
	serverCert, err := ca.ServerCertificate("127.0.0.1")
	if err != nil {
		t.Fatal(err)
	}
	mux := http.NewServeMux()
	mux.HandleFunc("GET /v1/ca", func(w http.ResponseWriter, _ *http.Request) {
		w.Header().Set("Content-Type", "application/x-pem-file")
		_ = pem.Encode(w, &pem.Block{Type: "CERTIFICATE", Bytes: ca.Cert.Raw})
	})
	mux.HandleFunc("POST /v1/enroll", g.enroll)
	mux.HandleFunc("GET /v1/connect", g.connect)
	g.server = httptest.NewUnstartedServer(mux)
	g.server.TLS = &tls.Config{
		Certificates: []tls.Certificate{serverCert},
		ClientAuth:   tls.VerifyClientCertIfGiven,
		ClientCAs:    ca.Pool(),
	}
	g.server.StartTLS()
	t.Cleanup(func() {
		g.mu.Lock()
		for _, c := range g.open {
			c.conn.CloseNow()
			close(c.done)
		}
		g.open = nil
		g.mu.Unlock()
		g.server.Close()
	})
	return g
}

func (g *fakeGateway) addr() string { return strings.TrimPrefix(g.server.URL, "https://") }

func (g *fakeGateway) enroll(w http.ResponseWriter, r *http.Request) {
	body, _ := io.ReadAll(r.Body)
	var req agentv1.EnrollRequest
	if err := proto.Unmarshal(body, &req); err != nil || req.GetToken() != gwToken {
		w.Header().Set("Content-Type", "application/problem+json")
		w.WriteHeader(http.StatusUnauthorized)
		_, _ = w.Write([]byte(`{"detail":"The enrollment token is not valid."}`))
		return
	}
	certDER, err := g.ca.IssueAgent(req.GetCsrDer(), gwEndpoint, gwInstance, time.Now().Add(-g.certBackdate), g.certLifetime)
	if err != nil {
		http.Error(w, err.Error(), http.StatusBadRequest)
		return
	}
	g.issued.Add(1)
	data, _ := proto.Marshal(&agentv1.EnrollResponse{
		EndpointId: gwEndpoint, InstanceId: gwInstance, CertificateDer: certDER, CaCertificateDer: g.ca.Cert.Raw,
		InstanceSigningPublicKey: g.signingPub, InstanceSigningKeyId: gwKeyID,
	})
	w.Header().Set("Content-Type", "application/x-protobuf")
	_, _ = w.Write(data)
}

func (g *fakeGateway) connect(w http.ResponseWriter, r *http.Request) {
	if r.TLS == nil || len(r.TLS.PeerCertificates) == 0 {
		http.Error(w, "client certificate required", http.StatusUnauthorized)
		return
	}
	conn, err := websocket.Accept(w, r, nil)
	if err != nil {
		return
	}
	conn.SetReadLimit(MaxMessageBytes)
	c := &gwConn{conn: conn, clientCert: r.TLS.PeerCertificates[0], done: make(chan struct{})}
	g.mu.Lock()
	g.open = append(g.open, c)
	g.mu.Unlock()
	g.conns <- c
	<-c.done
}

func (g *fakeGateway) accept(t *testing.T) *gwConn {
	t.Helper()
	select {
	case c := <-g.conns:
		return c
	case <-time.After(15 * time.Second):
		t.Fatal("the agent did not connect")
		return nil
	}
}

func (g *fakeGateway) signed(t *testing.T, cfg *agentv1.AgentConfig, key ed25519.PrivateKey) *agentv1.SignedConfig {
	t.Helper()
	payload, err := proto.MarshalOptions{Deterministic: true}.Marshal(cfg)
	if err != nil {
		t.Fatal(err)
	}
	msg := append(append([]byte(signedconfig.Context), 0), payload...)
	return &agentv1.SignedConfig{Payload: payload, Signature: ed25519.Sign(key, msg), KeyId: gwKeyID}
}

func (c *gwConn) send(t *testing.T, msg *agentv1.ServerMessage) {
	t.Helper()
	data, _ := proto.Marshal(msg)
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	if err := c.conn.Write(ctx, websocket.MessageBinary, data); err != nil {
		t.Fatalf("gateway write: %v", err)
	}
}

// expect reads agent messages until match returns true; other messages (heartbeats, inventory) are skipped.
func (c *gwConn) expect(t *testing.T, what string, match func(*agentv1.AgentMessage) bool) *agentv1.AgentMessage {
	t.Helper()
	ctx, cancel := context.WithTimeout(context.Background(), 15*time.Second)
	defer cancel()
	for {
		_, data, err := c.conn.Read(ctx)
		if err != nil {
			t.Fatalf("waiting for %s: %v", what, err)
		}
		var msg agentv1.AgentMessage
		if err := proto.Unmarshal(data, &msg); err != nil {
			t.Fatalf("agent sent an unreadable message: %v", err)
		}
		if match(&msg) {
			return &msg
		}
	}
}

func (c *gwConn) close() {
	c.conn.CloseNow()
}

type fixedCollector struct{}

func (fixedCollector) Collect(context.Context, *agentv1.CheckSpec) []checks.Measurement {
	return []checks.Measurement{{Value: 12.5, Target: "C:", Detail: "fake"}}
}

func testInventory(context.Context) *agentv1.Inventory {
	return &agentv1.Inventory{Hostname: "test-host", Os: &agentv1.OsInfo{Platform: "windows", Name: "Windows 11 Pro"}}
}

func testOptions(store *state.Store) Options {
	return Options{
		Store:              store,
		Logger:             logging.Discard(),
		Collector:          fixedCollector{},
		Inventory:          testInventory,
		OSInfo:             func(context.Context) *agentv1.OsInfo { return &agentv1.OsInfo{Platform: "windows"} },
		BatchMinResults:    3,
		BatchFlushInterval: 300 * time.Millisecond,
		BackoffBase:        50 * time.Millisecond,
		BackoffMax:         200 * time.Millisecond,
		HandshakeTimeout:   5 * time.Second,
	}
}

func enrollForTest(t *testing.T, g *fakeGateway) *state.Store {
	t.Helper()
	dir := t.TempDir()
	_, err := Enroll(context.Background(), EnrollParams{
		StateDir: dir, Access: platform.AccessCurrentUser, Key: state.KeyRef{Kind: keystore.KindFile},
		Server: g.addr(), Token: gwToken, CAFingerprint: g.ca.Fingerprint(), Logger: logging.Discard(),
	})
	if err != nil {
		t.Fatalf("enroll: %v", err)
	}
	return state.NewStore(dir, platform.AccessCurrentUser)
}

func startAgent(t *testing.T, opts Options) (*Agent, chan error, context.CancelFunc) {
	t.Helper()
	a, err := New(opts)
	if err != nil {
		t.Fatalf("new agent: %v", err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan error, 1)
	go func() {
		done <- a.Run(ctx)
		// Closed after the result, so the cleanup does not wait when a test already received it.
		close(done)
	}()
	t.Cleanup(func() {
		cancel()
		select {
		case <-done:
		case <-time.After(10 * time.Second):
			t.Error("the agent did not stop")
		}
		_ = a.Close()
	})
	return a, done, cancel
}

func isType[T any](m *agentv1.AgentMessage) bool {
	_, ok := m.GetBody().(T)
	return ok
}

func TestFailedEnrollmentLeavesNothingBehind(t *testing.T) {
	g := newFakeGateway(t)
	dir := t.TempDir()
	_, err := Enroll(context.Background(), EnrollParams{
		StateDir: dir, Access: platform.AccessCurrentUser, Key: state.KeyRef{Kind: keystore.KindFile},
		Server: g.addr(), Token: "fet_wrongtoken_000000", CAFingerprint: g.ca.Fingerprint(), Logger: logging.Discard(),
	})
	if err == nil || !strings.Contains(err.Error(), "not valid") {
		t.Fatalf("expected the problem detail, got %v", err)
	}
	if _, err := state.NewStore(dir, platform.AccessCurrentUser).Load(); !errors.Is(err, state.ErrNotEnrolled) {
		t.Fatalf("no state may be written, got %v", err)
	}
	if _, err := keystore.Open(dir, state.KeyRef{Kind: keystore.KindFile, File: keystore.DefaultFileName}); err == nil {
		t.Fatal("the key must be removed after a failed enrollment")
	}
}

// Hello → config → results → no ack → reconnect → same batch resent → ack → next sequence → revoke.
func TestSessionConfigResultsAckResendAndRevocation(t *testing.T) {
	g := newFakeGateway(t)
	store := enrollForTest(t, g)
	a, done, _ := startAgent(t, testOptions(store))

	// Session 1.
	c1 := g.accept(t)
	hello := c1.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) }).GetHello()
	if hello.GetConfigVersion() != 0 || hello.GetHostname() == "" {
		t.Fatalf("unexpected hello %v", hello)
	}
	c1.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{EndpointId: gwEndpoint, HeartbeatIntervalSeconds: 30}}})

	c1.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Ping{Ping: &agentv1.Ping{Nonce: 77}}})
	pong := c1.expect(t, "Pong", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Pong](m) })
	if pong.GetPong().GetNonce() != 77 {
		t.Fatalf("pong nonce %d", pong.GetPong().GetNonce())
	}

	cfg := &agentv1.AgentConfig{InstanceId: gwInstance, EndpointId: gwEndpoint, Version: 1, Tier: agentv1.Tier_TIER_MANAGED,
		Checks: []*agentv1.CheckSpec{{Id: "disk", Type: agentv1.CheckType_CHECK_TYPE_DISK_FREE, IntervalSeconds: 1, Parameters: map[string]string{"drive": "C:"}}}}
	c1.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Config{Config: g.signed(t, cfg, g.signingKey)}})
	applied := c1.expect(t, "ConfigApplied", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_ConfigApplied](m) }).GetConfigApplied()
	if applied.GetError() != "" || applied.GetConfigVersion() != 1 {
		t.Fatalf("config refused: %v", applied)
	}

	// A configuration signed with another key is refused and the applied version stays 1.
	_, rogue, _ := ed25519.GenerateKey(rand.Reader)
	cfg2 := proto.Clone(cfg).(*agentv1.AgentConfig)
	cfg2.Version = 2
	c1.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Config{Config: g.signed(t, cfg2, rogue)}})
	refused := c1.expect(t, "ConfigApplied error", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_ConfigApplied](m) }).GetConfigApplied()
	if refused.GetError() == "" || refused.GetConfigVersion() != 1 {
		t.Fatalf("a rogue config must be refused: %v", refused)
	}

	batch1 := c1.expect(t, "batch 1", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_CheckResults](m) }).GetCheckResults()
	if batch1.GetSequence() != 1 || len(batch1.GetResults()) == 0 {
		t.Fatalf("unexpected first batch: %v", batch1)
	}
	r := batch1.GetResults()[0]
	if r.GetCheckId() != "disk" || r.GetConfigVersion() != 1 || r.GetTarget() != "C:" || r.GetCollectedAt() == nil {
		t.Fatalf("unexpected result %v", r)
	}
	// No ack: drop the connection.
	c1.close()

	// Session 2: the agent reports config version 1 and resends batch 1 unchanged.
	c2 := g.accept(t)
	hello2 := c2.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) }).GetHello()
	if hello2.GetConfigVersion() != 1 {
		t.Fatalf("expected config version 1 in Hello, got %d", hello2.GetConfigVersion())
	}
	c2.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{EndpointId: gwEndpoint, HeartbeatIntervalSeconds: 30}}})
	resent := c2.expect(t, "resent batch", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_CheckResults](m) }).GetCheckResults()
	if resent.GetSequence() != 1 || !proto.Equal(resent, batch1) {
		t.Fatalf("expected batch 1 resent unchanged, got sequence %d with %d results", resent.GetSequence(), len(resent.GetResults()))
	}
	c2.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_BatchAck{BatchAck: &agentv1.BatchAck{Sequence: 1}}})
	batch2 := c2.expect(t, "batch 2", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_CheckResults](m) }).GetCheckResults()
	if batch2.GetSequence() != 2 {
		t.Fatalf("expected sequence 2, got %d", batch2.GetSequence())
	}
	c2.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_BatchAck{BatchAck: &agentv1.BatchAck{Sequence: 2}}})

	// Inventory on request, with a hash that matches the content.
	c2.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_InventoryRequest{InventoryRequest: &agentv1.InventoryRequest{}}})
	inv := c2.expect(t, "inventory", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Inventory](m) }).GetInventory()
	if inv.GetInventory().GetHostname() != "test-host" || len(inv.GetHash()) != 64 {
		t.Fatalf("unexpected inventory report %v", inv)
	}

	// Revocation: the agent stops and remembers it.
	c2.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Disconnect{Disconnect: &agentv1.Disconnect{Code: agentv1.DisconnectCode_DISCONNECT_CODE_REVOKED, Reason: "Endpoint deleted"}}})
	select {
	case err := <-done:
		if !errors.Is(err, ErrRevoked) {
			t.Fatalf("expected ErrRevoked, got %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the agent did not stop after revocation")
	}
	st, err := store.Load()
	if err != nil || !st.Revoked || st.AppliedConfigVersion != 1 {
		t.Fatalf("expected revoked state with config version 1, got %+v %v", st, err)
	}
	select {
	case extra := <-g.conns:
		t.Fatalf("a revoked agent must not reconnect (got a connection with %s)", extra.clientCert.Subject)
	case <-time.After(500 * time.Millisecond):
	}
	_ = a
}

func TestAgentOnlyConfigurationRunsNoChecks(t *testing.T) {
	g := newFakeGateway(t)
	store := enrollForTest(t, g)
	a, _, _ := startAgent(t, testOptions(store))
	c := g.accept(t)
	c.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) })
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{HeartbeatIntervalSeconds: 1}}})
	cfg := &agentv1.AgentConfig{InstanceId: gwInstance, EndpointId: gwEndpoint, Version: 4, Tier: agentv1.Tier_TIER_AGENT_ONLY,
		Checks: []*agentv1.CheckSpec{{Id: "cpu", Type: agentv1.CheckType_CHECK_TYPE_UPTIME, IntervalSeconds: 1}}}
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_Config{Config: g.signed(t, cfg, g.signingKey)}})
	applied := c.expect(t, "ConfigApplied", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_ConfigApplied](m) }).GetConfigApplied()
	if applied.GetConfigVersion() != 4 || applied.GetError() != "" {
		t.Fatalf("unexpected ConfigApplied %v", applied)
	}
	// Heartbeats keep coming (interval 1 s), but no check results for 2.5 s.
	deadline := time.Now().Add(2500 * time.Millisecond)
	ctx, cancel := context.WithDeadline(context.Background(), deadline)
	defer cancel()
	heartbeats := 0
	for {
		_, data, err := c.conn.Read(ctx)
		if err != nil {
			break
		}
		var msg agentv1.AgentMessage
		_ = proto.Unmarshal(data, &msg)
		if isType[*agentv1.AgentMessage_CheckResults](&msg) {
			t.Fatal("an agent-only endpoint sent check results")
		}
		if isType[*agentv1.AgentMessage_Heartbeat](&msg) {
			heartbeats++
		}
	}
	if heartbeats == 0 {
		t.Fatal("expected heartbeats on an agent-only endpoint")
	}
	if a.scheduler.Count() != 0 || a.buffer.Total() != 0 {
		t.Fatalf("expected no checks and no results, got %d checks and %d results", a.scheduler.Count(), a.buffer.Total())
	}
}

func TestCertificateIsRenewedAfterTwoThirdsOfItsLifetime(t *testing.T) {
	g := newFakeGateway(t)
	// 90 minutes lifetime, 70 minutes in: past two thirds.
	g.certLifetime = 90 * time.Minute
	g.certBackdate = 70 * time.Minute
	store := enrollForTest(t, g)
	before, _ := store.Load()
	oldCert, _ := before.Certificate()
	startAgent(t, testOptions(store))

	c1 := g.accept(t)
	c1.expect(t, "Hello", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_Hello](m) })
	c1.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{HeartbeatIntervalSeconds: 30}}})
	req := c1.expect(t, "renewal request", func(m *agentv1.AgentMessage) bool { return isType[*agentv1.AgentMessage_RenewCertificate](m) }).GetRenewCertificate()
	csr, err := x509.ParseCertificateRequest(req.GetCsrDer())
	if err != nil || csr.CheckSignature() != nil {
		t.Fatalf("invalid renewal CSR: %v", err)
	}
	if !csr.PublicKey.(interface{ Equal(crypto.PublicKey) bool }).Equal(oldCert.PublicKey) {
		t.Fatal("the renewal must use the same key")
	}

	// First answer: refused. The agent keeps its certificate and does not reconnect.
	c1.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_RenewCertificate{RenewCertificate: &agentv1.RenewCertificateResponse{Error: "The certificate is revoked."}}})
	time.Sleep(300 * time.Millisecond)
	if st, _ := store.Load(); st.CertificatePEM != before.CertificatePEM {
		t.Fatal("a refused renewal must keep the current certificate")
	}

	// A valid answer: stored, and the agent reconnects with the new certificate.
	newDER, err := g.ca.IssueAgent(req.GetCsrDer(), gwEndpoint, gwInstance, time.Now().Add(-time.Minute), 90*24*time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	c1.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_RenewCertificate{RenewCertificate: &agentv1.RenewCertificateResponse{CertificateDer: newDER}}})
	c2 := g.accept(t)
	if string(c2.clientCert.Raw) != string(newDER) {
		t.Fatal("the agent must reconnect with the renewed certificate")
	}
	st, _ := store.Load()
	if cert, _ := st.Certificate(); string(cert.Raw) != string(newDER) {
		t.Fatal("the renewed certificate must be stored")
	}
}
