package watchdog

import (
	"context"
	"crypto/rand"
	"crypto/tls"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
	"github.com/404-developer-AI/Fleeto/agent/internal/testpki"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
)

const (
	testEndpoint = "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	testInstance = "6f1d3c1e-5a4b-4f7e-9a31-2b8f0c1d2e3f"
)

type stoppedAgent struct {
	mu     sync.Mutex
	state  svcctl.State
	starts int
}

func (s *stoppedAgent) Query(string) (svcctl.State, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.state, nil
}

func (s *stoppedAgent) Start(string) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.starts++
	return nil
}

func (s *stoppedAgent) Stop(context.Context, string, time.Duration) error        { return nil }
func (s *stoppedAgent) WaitRunning(context.Context, string, time.Duration) error { return nil }

// A provisioned watchdog connects as the watchdog, reports the agent service it keeps running, and stops connecting once revoked while it
// keeps supervising the agent.
func TestTheWatchdogReportsTheAgentAndStopsConnectingWhenRevoked(t *testing.T) {
	ca, err := testpki.NewCA("Fleeto Instance CA test")
	if err != nil {
		t.Fatal(err)
	}
	conns := make(chan *websocket.Conn, 4)
	var connects sync.WaitGroup
	server := httptest.NewUnstartedServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/connect" || r.TLS == nil || len(r.TLS.PeerCertificates) == 0 {
			http.Error(w, "client certificate required", http.StatusUnauthorized)
			return
		}
		conn, err := websocket.Accept(w, r, nil)
		if err != nil {
			return
		}
		connects.Add(1)
		conns <- conn
		<-r.Context().Done()
	}))
	serverCert, _ := ca.ServerCertificate("127.0.0.1")
	server.TLS = &tls.Config{Certificates: []tls.Certificate{serverCert}, ClientAuth: tls.RequestClientCert}
	server.StartTLS()
	defer server.Close()

	dir := t.TempDir()
	key, ref, err := keystore.Create(dir, state.KeyRef{Kind: keystore.KindFile}, platform.AccessCurrentUser)
	if err != nil {
		t.Fatal(err)
	}
	csr, _ := keystore.CreateCSR(rand.Reader, key, "test-host")
	_ = key.Close()
	certDER, err := ca.IssueAgent(csr, testEndpoint, testInstance, time.Now().Add(-time.Minute), 90*24*time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	store := state.NewStore(dir, platform.AccessCurrentUser)
	if err := store.Save(&state.State{
		Server: strings.TrimPrefix(server.URL, "https://"), EndpointID: testEndpoint, InstanceID: testInstance,
		CACertificatePEM: state.EncodeCertificatePEM(ca.Cert.Raw), SigningPublicKey: "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=",
		CertificatePEM: state.EncodeCertificatePEM(certDER), Key: ref, EnrolledAt: time.Now(),
	}); err != nil {
		t.Fatal(err)
	}

	agent := &stoppedAgent{state: svcctl.StateStopped}
	w, err := New(Options{Store: store, AgentStateDir: t.TempDir(), ProgramDir: t.TempDir(), Controller: agent,
		Logger: slog.New(slog.NewTextHandler(io.Discard, nil)), Keys: nil, UpdateMaxDelay: -1})
	if err != nil {
		t.Fatalf("new watchdog: %v", err)
	}
	defer w.Close()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	done := make(chan error, 1)
	go func() { done <- w.Run(ctx) }()

	var conn *websocket.Conn
	select {
	case conn = <-conns:
	case <-time.After(15 * time.Second):
		t.Fatal("the watchdog did not connect")
	}
	read := func(what string, match func(*agentv1.AgentMessage) bool) {
		t.Helper()
		readCtx, cancelRead := context.WithTimeout(context.Background(), 15*time.Second)
		defer cancelRead()
		for {
			_, data, err := conn.Read(readCtx)
			if err != nil {
				t.Fatalf("waiting for %s: %v", what, err)
			}
			var msg agentv1.AgentMessage
			if proto.Unmarshal(data, &msg) == nil && match(&msg) {
				return
			}
		}
	}
	send := func(msg *agentv1.ServerMessage) {
		data, _ := proto.Marshal(msg)
		_ = conn.Write(context.Background(), websocket.MessageBinary, data)
	}

	read("the watchdog Hello", func(m *agentv1.AgentMessage) bool {
		return m.GetHello().GetComponent() == agentv1.Component_COMPONENT_WATCHDOG
	})
	send(&agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{EndpointId: testEndpoint, HeartbeatIntervalSeconds: 1}}})
	read("a heartbeat that reports the stopped agent", func(m *agentv1.AgentMessage) bool {
		return m.GetHeartbeat().GetPeer().GetState() == agentv1.ServiceState_SERVICE_STATE_STOPPED
	})
	agent.mu.Lock()
	starts := agent.starts
	agent.mu.Unlock()
	if starts == 0 {
		t.Fatal("the watchdog did not start the stopped agent service")
	}
	if h, err := update.ReadHealth(dir); err != nil || !h.Connected {
		t.Fatalf("the watchdog health file does not say connected: %+v %v", h, err)
	}

	send(&agentv1.ServerMessage{Body: &agentv1.ServerMessage_Disconnect{Disconnect: &agentv1.Disconnect{
		Code: agentv1.DisconnectCode_DISCONNECT_CODE_REVOKED, Reason: "revoked in a test",
	}}})
	deadline := time.Now().Add(10 * time.Second)
	for {
		if st, err := store.Load(); err == nil && st.Revoked {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("the revocation was not recorded")
		}
		time.Sleep(50 * time.Millisecond)
	}
	select {
	case conn = <-conns:
		t.Fatal("a revoked watchdog connected again")
	case <-time.After(2 * time.Second):
	}
	// The session closes gracefully (up to a few seconds for the close handshake) before the health file says disconnected.
	for deadline := time.Now().Add(10 * time.Second); ; time.Sleep(100 * time.Millisecond) {
		if h, err := update.ReadHealth(dir); err == nil && !h.Connected {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("a revoked watchdog still says it is connected")
		}
	}
	cancel()
	select {
	case <-done:
	case <-time.After(10 * time.Second):
		t.Fatal("the watchdog did not stop")
	}
}
