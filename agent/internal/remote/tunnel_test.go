package remote

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"io"
	"net"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

// echoTarget stands in for pveproxy: it echoes what a channel sends. dials counts the connections the endpoint made.
func echoTarget(t *testing.T) (*Tunnel, *atomic.Int32) {
	t.Helper()
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() { _ = listener.Close() })
	go func() {
		for {
			conn, err := listener.Accept()
			if err != nil {
				return
			}
			go func() { _, _ = io.Copy(conn, conn); _ = conn.Close() }()
		}
	}()
	var dials atomic.Int32
	return &Tunnel{
		Dial: func(ctx context.Context) (net.Conn, error) {
			dials.Add(1)
			var d net.Dialer
			return d.DialContext(ctx, "tcp", listener.Addr().String())
		},
		Certificate: strings.Repeat("ab", 32),
	}, &dials
}

func startTunnelSession(t *testing.T, kind agentv1.RemoteSessionKind, component agentv1.Component, tunnel *Tunnel, terminals *atomic.Int32) (*browser, Hello) {
	t.Helper()
	f := newFixture(t)
	token, err := VerifyToken(f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) { tk.Kind = kind; tk.Component = component })), f.trust, component, f.now)
	if err != nil {
		t.Fatal(err)
	}
	endpoint, err := NewEndpointHandshake(token, f.endpoint)
	if err != nil {
		t.Fatal(err)
	}
	keys, err := BrowserHandshake(f.browser, token.PayloadHash, endpoint.PublicKey, endpoint.Signature, f.spki(t), f.fingerprints(t))
	if err != nil {
		t.Fatal(err)
	}
	p := newPipe()
	session, err := NewSession(SessionOptions{
		Token: token, Keys: endpoint.Keys, Transport: endpointSide{p}, Hello: Hello{Hostname: "PVE-01", Shells: []string{"bash"}}, Tunnel: tunnel,
		Open: func(string, int, int) (Terminal, error) { terminals.Add(1); return newFakeTerminal(), nil },
		Now:  time.Now, Tick: time.Hour,
	})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	go session.Run(ctx)
	send, _ := NewCipher(keys.BrowserToEndpoint)
	recv, _ := NewCipher(keys.EndpointToBrowser)
	b := &browser{t: t, p: p, send: send, recv: recv}
	kindByte, body := b.read()
	var hello Hello
	if kindByte != FrameHello || json.Unmarshal(body, &hello) != nil {
		t.Fatalf("expected hello, got %x", kindByte)
	}
	return b, hello
}

func channelFrame(_ byte, channel int) []byte {
	body, _ := json.Marshal(map[string]int{"channel": channel})
	return body
}

func TestATunnelChannelReachesTheFixedTargetAndNothingElse(t *testing.T) {
	tunnel, dials := echoTarget(t)
	var terminals atomic.Int32
	b, hello := startTunnelSession(t, agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_PROXMOX_WEB, agentv1.Component_COMPONENT_AGENT, tunnel, &terminals)
	if hello.Kind != "proxmox_web" || hello.TunnelCertificate != tunnel.Certificate || len(hello.Shells) != 0 || hello.Scripts {
		t.Fatalf("hello = %+v", hello)
	}

	// A terminal, a file request and a screen frame are ignored in a session of the web interface.
	b.write(FrameOpen, []byte(`{"channel":1,"service":"terminal","shell":"bash","cols":80,"rows":24}`))
	b.write(FrameRequest, []byte(`{"id":"r1","op":"list","path":"/"}`))

	b.write(FrameTunnelOpen, channelFrame(FrameTunnelOpen, 7))
	kind, body := b.read()
	if kind != FrameTunnelOpened || string(body) != `{"channel":7}` {
		t.Fatalf("opened: %x %s", kind, body)
	}
	data := append([]byte{0, 7}, []byte("GET / HTTP/1.1\r\n\r\n")...)
	b.write(FrameTunnelData, data)
	kind, body = b.read()
	if kind != FrameTunnelData || binary.BigEndian.Uint16(body) != 7 || string(body[2:]) != "GET / HTTP/1.1\r\n\r\n" {
		t.Fatalf("data: %x %q", kind, body)
	}
	if terminals.Load() != 0 || dials.Load() != 1 {
		t.Fatalf("terminals %d, dials %d", terminals.Load(), dials.Load())
	}

	// Closing from Fleeto Connect closes the connection; the target's end is not reported back for a channel already gone.
	b.write(FrameTunnelClose, channelFrame(FrameTunnelClose, 7))
}

func TestASessionKeepsAtMostSoManyTunnelChannels(t *testing.T) {
	tunnel, _ := echoTarget(t)
	var terminals atomic.Int32
	b, _ := startTunnelSession(t, agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_PROXMOX_WEB, agentv1.Component_COMPONENT_AGENT, tunnel, &terminals)
	for channel := 1; channel <= MaxTunnelChannels+1; channel++ {
		b.write(FrameTunnelOpen, channelFrame(FrameTunnelOpen, channel))
	}
	refused := 0
	for range MaxTunnelChannels + 1 {
		kind, body := b.read()
		var opened tunnelBody
		if kind != FrameTunnelOpened || json.Unmarshal(body, &opened) != nil {
			t.Fatalf("unexpected %x %s", kind, body)
		}
		if opened.Error != "" {
			refused++
		}
	}
	if refused != 1 {
		t.Fatalf("refused %d channels", refused)
	}
}

func TestOtherKindsOfSessionOpenNoTunnel(t *testing.T) {
	tunnel, dials := echoTarget(t)
	var terminals atomic.Int32
	b, hello := startTunnelSession(t, agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_REMOTE_BACKGROUND, agentv1.Component_COMPONENT_WATCHDOG, tunnel, &terminals)
	if hello.Kind != "remote_background" || hello.TunnelCertificate != "" {
		t.Fatalf("hello = %+v", hello)
	}
	b.write(FrameTunnelOpen, channelFrame(FrameTunnelOpen, 1))
	// The terminal still works, which shows the frame before it was read and ignored.
	b.write(FrameOpen, []byte(`{"channel":1,"service":"terminal","shell":"bash","cols":80,"rows":24}`))
	if kind, _ := b.read(); kind != FrameOpened {
		t.Fatalf("expected the terminal to open, got %x", kind)
	}
	if dials.Load() != 0 {
		t.Fatal("a remote background session opened a tunnel")
	}
}

func TestTheWebInterfaceIsServedByTheAgentOnly(t *testing.T) {
	f := newFixture(t)
	signed := f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) {
		tk.Kind = agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_PROXMOX_WEB
	}))
	if _, err := VerifyToken(signed, f.trust, agentv1.Component_COMPONENT_WATCHDOG, f.now); err == nil {
		t.Fatal("the watchdog accepted a token of the Proxmox web interface")
	}
	offer := &agentv1.RemoteSessionOffer{Session: f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) {
		tk.Kind = agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_PROXMOX_WEB
		tk.Component = agentv1.Component_COMPONENT_AGENT
	}))}
	server := &Server{
		Component: agentv1.Component_COMPONENT_AGENT,
		Trust:     func() (signedconfig.Trust, error) { return f.trust, nil },
		Managed:   func() bool { return true },
		Now:       func() time.Time { return f.now },
	}
	if _, refusal := server.Offer(context.Background(), offer); !strings.Contains(refusal, "does not serve the Proxmox web interface") {
		t.Fatalf("without a tunnel: %q", refusal)
	}
	server.Tunnel = func(context.Context) (*Tunnel, error) { return nil, ErrNoTunnel }
	if _, refusal := server.Offer(context.Background(), offer); !strings.Contains(refusal, "not a Proxmox VE host") {
		t.Fatalf("not a Proxmox VE host: %q", refusal)
	}
}
