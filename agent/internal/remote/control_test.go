package remote

import (
	"context"
	"encoding/json"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/screen"
)

func remoteControlToken(tk *agentv1.RemoteSessionToken) {
	tk.Kind = agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_REMOTE_CONTROL
	tk.Component = agentv1.Component_COMPONENT_AGENT
	tk.WindowsSessionId = 2
}

func TestARemoteControlTokenIsServedByTheAgentOnly(t *testing.T) {
	f := newFixture(t)
	control := f.sign(t, f.token(remoteControlToken))
	token, err := VerifyToken(control, f.trust, agentv1.Component_COMPONENT_AGENT, f.now)
	if err != nil {
		t.Fatalf("the agent refused a remote control token: %v", err)
	}
	if token.GetWindowsSessionId() != 2 {
		t.Fatalf("windows session %d", token.GetWindowsSessionId())
	}
	if _, err := VerifyToken(control, f.trust, agentv1.Component_COMPONENT_WATCHDOG, f.now); err == nil {
		t.Fatal("the watchdog accepted a remote control token")
	}
	background := f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) { tk.Component = agentv1.Component_COMPONENT_AGENT }))
	if _, err := VerifyToken(background, f.trust, agentv1.Component_COMPONENT_AGENT, f.now); err == nil {
		t.Fatal("the agent accepted a remote background token")
	}
}

// fakeScreen records the frames it gets and can send frames to the browser.
type fakeScreen struct {
	mu     sync.Mutex
	frames [][]byte
	send   func([]byte) error
	closed bool
}

func (s *fakeScreen) Handle(_ context.Context, frame []byte) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.frames = append(s.frames, append([]byte(nil), frame...))
}

func (s *fakeScreen) Close() {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.closed = true
}

func (s *fakeScreen) kinds() []byte {
	s.mu.Lock()
	defer s.mu.Unlock()
	var out []byte
	for _, f := range s.frames {
		out = append(out, f[0])
	}
	return out
}

func TestARemoteControlSessionCarriesTheScreenAndNothingElse(t *testing.T) {
	f := newFixture(t)
	token, err := VerifyToken(f.sign(t, f.token(remoteControlToken)), f.trust, agentv1.Component_COMPONENT_AGENT, f.now)
	if err != nil {
		t.Fatal(err)
	}
	endpoint, err := NewEndpointHandshake(token, f.endpoint)
	if err != nil {
		t.Fatal(err)
	}
	browserKeys, err := BrowserHandshake(f.browser, token.PayloadHash, endpoint.PublicKey, endpoint.Signature, f.spki(t), f.fingerprints(t))
	if err != nil {
		t.Fatal(err)
	}
	fake := &fakeScreen{}
	opened := false
	p := newPipe()
	session, err := NewSession(SessionOptions{
		Token: token, Keys: endpoint.Keys, Transport: endpointSide{p}, Hello: Hello{Hostname: "WS-01", Platform: "windows"},
		Open: func(string, int, int) (Terminal, error) { opened = true; return newFakeTerminal(), nil },
		Screen: func(_ *Token, send func([]byte) error) ScreenHandler {
			fake.send = send
			return fake
		},
		Now: time.Now, Tick: time.Hour,
	})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan string, 1)
	go func() { done <- session.Run(context.Background()) }()
	send, _ := NewCipher(browserKeys.BrowserToEndpoint)
	recv, _ := NewCipher(browserKeys.EndpointToBrowser)
	b := &browser{t: t, p: p, send: send, recv: recv}

	kind, body := b.read()
	var hello Hello
	if kind != FrameHello || json.Unmarshal(body, &hello) != nil || hello.Kind != "remote_control" || hello.WindowsSession != 2 || hello.MaxFileBytes != 0 {
		t.Fatalf("hello %x %s", kind, body)
	}

	// Terminal and file frames are ignored in a remote control session.
	b.write(FrameOpen, []byte(`{"channel":1,"service":"terminal","shell":"powershell","cols":80,"rows":24}`))
	b.write(FrameRequest, []byte(`{"id":"r1","op":"list","path":"C:\\"}`))
	b.write(screen.FrameStart, []byte(`{"monitor":0}`))
	b.write(screen.FramePointer, []byte(`{"x":10,"y":20,"buttons":1}`))
	b.write(screen.FrameUpdate, []byte{0, 0, 0, 1}) // endpoint-to-browser type: never passed on
	b.write(screen.FrameKey, []byte(`{"code":"KeyA","key":"a","down":true}`))

	deadline := time.Now().Add(5 * time.Second)
	for len(fake.kinds()) < 3 && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
	if got := fake.kinds(); string(got) != string([]byte{screen.FrameStart, screen.FramePointer, screen.FrameKey}) {
		t.Fatalf("the screen got frames %x", got)
	}
	if opened {
		t.Fatal("a remote control session opened a terminal")
	}

	// What the screen sends reaches the browser, encrypted like every frame; no response to the file request came before it.
	if err := fake.send(append([]byte{screen.FrameInfo}, []byte(`{"monitors":[],"monitor":0}`)...)); err != nil {
		t.Fatal(err)
	}
	if kind, _ := b.read(); kind != screen.FrameInfo {
		t.Fatalf("expected the screen info, got %x", kind)
	}

	b.write(FrameEnd, []byte(`{"reason":"done"}`))
	select {
	case reason := <-done:
		if !strings.Contains(reason, "technician") {
			t.Fatalf("ended with %q", reason)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the session did not end")
	}
	fake.mu.Lock()
	closed := fake.closed
	fake.mu.Unlock()
	if !closed {
		t.Fatal("the screen was not closed when the session ended")
	}
}

func TestAServiceWithoutAScreenRefusesRemoteControl(t *testing.T) {
	f := newFixture(t)
	relay, sockets := relayServer(t)
	server := f.server(t, relay, true)
	server.Component = agentv1.Component_COMPONENT_AGENT
	participant, refusal := server.Offer(context.Background(), &agentv1.RemoteSessionOffer{Session: f.sign(t, f.token(remoteControlToken))})
	if participant != participantID || !strings.Contains(refusal, "not supported on this platform") {
		t.Fatalf("refusal %q for %q", refusal, participant)
	}
	select {
	case <-sockets:
		t.Fatal("a refused offer connected the relay")
	case <-time.After(200 * time.Millisecond):
	}
}
