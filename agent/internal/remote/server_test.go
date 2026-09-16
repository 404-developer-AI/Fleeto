package remote

import (
	"context"
	"crypto/sha256"
	"crypto/tls"
	"crypto/x509"
	"encoding/binary"
	"encoding/hex"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

// relayServer plays the gateway's endpoint side: it accepts /v1/relay/<participant>, hands the socket to the test, and records the path.
func relayServer(t *testing.T) (*httptest.Server, chan *websocket.Conn) {
	t.Helper()
	sockets := make(chan *websocket.Conn, 1)
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if !strings.HasPrefix(r.URL.Path, RelayPath) || !ValidID(strings.TrimPrefix(r.URL.Path, RelayPath)) {
			http.NotFound(w, r)
			return
		}
		conn, err := websocket.Accept(w, r, nil)
		if err != nil {
			return
		}
		conn.SetReadLimit(MaxFrameBytes + 1024)
		sockets <- conn
		<-r.Context().Done()
	}))
	t.Cleanup(server.Close)
	return server, sockets
}

func (f *fixture) server(t *testing.T, relay *httptest.Server, managed bool) *Server {
	t.Helper()
	host := strings.TrimPrefix(relay.URL, "https://")
	pool := x509.NewCertPool()
	pool.AddCert(relay.Certificate())
	return &Server{
		Component: agentv1.Component_COMPONENT_WATCHDOG,
		Trust:     func() (signedconfig.Trust, error) { return f.trust, nil },
		Managed:   func() bool { return managed },
		Signer:    f.endpoint,
		CertificatePublicKey: func() ([]byte, error) {
			return f.spki(t), nil
		},
		Dial: WebSocketDialer(func() (string, *tls.Config, error) {
			return host, &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS12}, nil
		}),
		Hello: func() Hello {
			return Hello{Hostname: "SRV-01", Platform: "windows", Shells: []string{"powershell"}, PTY: true}
		},
		Open: func(string, int, int) (Terminal, error) { return newFakeTerminal(), nil },
		Now:  func() time.Time { return f.now },
	}
}

func TestAnOfferConnectsTheRelayAndServesATerminal(t *testing.T) {
	f := newFixture(t)
	relay, sockets := relayServer(t)
	server := f.server(t, relay, true)
	signed := f.sign(t, f.token(nil))
	ctx, cancel := context.WithTimeout(context.Background(), 20*time.Second)
	defer cancel()

	participant, refusal := server.Offer(ctx, &agentv1.RemoteSessionOffer{Session: signed})
	if refusal != "" || participant != participantID {
		t.Fatalf("offer refused: %q (%s)", refusal, participant)
	}
	conn := <-sockets
	defer conn.CloseNow()

	// The gateway receives the hello and passes key, signature and certificate key to the browser, which verifies them.
	_, data, err := conn.Read(ctx)
	if err != nil {
		t.Fatal(err)
	}
	var hello agentv1.RelayEndpointHello
	if err := proto.Unmarshal(data, &hello); err != nil || hello.GetParticipantId() != participantID {
		t.Fatalf("unexpected hello %v %v", &hello, err)
	}
	sum := sha256.Sum256(hello.GetCertificatePublicKey())
	keys, err := BrowserHandshake(f.browser, sha256.Sum256(signed.GetPayload()), hello.GetEndpointPublicKey(), hello.GetSignature(),
		hello.GetCertificatePublicKey(), []string{hex.EncodeToString(sum[:])})
	if err != nil {
		t.Fatal(err)
	}
	send, _ := NewCipher(keys.BrowserToEndpoint)
	receive, _ := NewCipher(keys.EndpointToBrowser)
	read := func() (byte, []byte) {
		_, frame, err := conn.Read(ctx)
		if err != nil {
			t.Fatal(err)
		}
		plaintext, err := receive.Open(frame)
		if err != nil {
			t.Fatal(err)
		}
		return plaintext[0], plaintext[1:]
	}
	write := func(kind byte, body []byte) {
		frame, _ := send.Seal(append([]byte{kind}, body...))
		if err := conn.Write(ctx, websocket.MessageBinary, frame); err != nil {
			t.Fatal(err)
		}
	}

	if kind, _ := read(); kind != FrameHello {
		t.Fatalf("expected hello, got %x", kind)
	}
	write(FrameOpen, []byte(`{"channel":3,"service":"terminal","shell":"powershell","cols":80,"rows":24}`))
	if kind, _ := read(); kind != FrameOpened {
		t.Fatalf("expected opened, got %x", kind)
	}
	write(FrameData, append(binary.BigEndian.AppendUint16(nil, 3), "whoami\r"...))
	if kind, body := read(); kind != FrameData || string(body[2:]) != "echo:whoami\r" {
		t.Fatalf("unexpected output %x %q", kind, body)
	}
	write(FrameEnd, []byte(`{}`))
}

func TestOffersThatMustNotStartAreRefusedWithTheReason(t *testing.T) {
	f := newFixture(t)
	relay, sockets := relayServer(t)
	ctx := context.Background()

	agentOnly := f.server(t, relay, false)
	if _, refusal := agentOnly.Offer(ctx, &agentv1.RemoteSessionOffer{Session: f.sign(t, f.token(nil))}); !strings.Contains(refusal, "agent-only") {
		t.Fatalf("an agent-only endpoint accepted the session: %q", refusal)
	}

	forged := f.sign(t, f.token(nil))
	forged.Signature[0] ^= 1
	server := f.server(t, relay, true)
	if participant, refusal := server.Offer(ctx, &agentv1.RemoteSessionOffer{Session: forged}); refusal == "" || participant != participantID {
		t.Fatalf("a forged token was accepted or not addressed: %q %q", refusal, participant)
	}

	signed := f.sign(t, f.token(nil))
	if _, refusal := server.Offer(ctx, &agentv1.RemoteSessionOffer{Session: signed}); refusal != "" {
		t.Fatalf("a valid offer was refused: %q", refusal)
	}
	conn := <-sockets
	defer conn.CloseNow()
	if _, refusal := server.Offer(ctx, &agentv1.RemoteSessionOffer{Session: signed}); !strings.Contains(refusal, "used before") {
		t.Fatalf("a token was used twice: %q", refusal)
	}
	select {
	case <-sockets:
		t.Fatal("a refused offer connected the relay")
	case <-time.After(200 * time.Millisecond):
	}
}
