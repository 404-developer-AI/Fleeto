package remote

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
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
		Screen: func(peer ScreenPeer) ScreenHandler {
			fake.send = peer.Send
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
	if kind != FrameHello || json.Unmarshal(body, &hello) != nil || hello.Kind != "remote_control" || hello.WindowsSession != 2 || hello.Clipboard {
		t.Fatalf("hello %x %s", kind, body)
	}

	// Terminal and file explorer frames are ignored in a remote control session.
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

	// The file explorer request is answered with a refusal, never a listing.
	kind, body = b.read()
	if kind != FrameResponse || !strings.Contains(string(body), `"ok":false`) || !strings.Contains(string(body), "not available in a remote control session") {
		t.Fatalf("response %x %s", kind, body)
	}

	// What the screen sends reaches the browser, encrypted like every frame.
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

// clipboardScreen is a screen that stages pasted files in a folder and offers one copied file.
type clipboardScreen struct {
	fakeScreen
	root   string
	copied string
	placed chan []string
}

func (s *clipboardScreen) StagingBatch() (string, error) { return os.MkdirTemp(s.root, "batch-") }

func (s *clipboardScreen) OpenCopiedFile(index int) (*os.File, string, error) {
	if index != 0 {
		return nil, "", errors.New("that file is no longer on the endpoint clipboard; copy it again")
	}
	f, err := os.Open(s.copied)
	return f, s.copied, err
}

func (s *clipboardScreen) PlaceFiles(paths []string) error {
	s.placed <- paths
	return nil
}

func startControl(t *testing.T, clipboard bool, screenHandler ScreenHandler) *backgroundSession {
	t.Helper()
	f := newFixture(t)
	token, err := VerifyToken(f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) {
		remoteControlToken(tk)
		tk.ClipboardEnabled = clipboard
	})), f.trust, agentv1.Component_COMPONENT_AGENT, f.now)
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
	p := newPipe()
	actions := make(chan action, 32)
	session, err := NewSession(SessionOptions{
		Token: token, Keys: endpoint.Keys, Transport: endpointSide{p}, Hello: Hello{Hostname: "WS-01", Platform: "windows"},
		Screen: func(ScreenPeer) ScreenHandler { return screenHandler },
		Report: func(verb, target, detail string) { actions <- action{verb, target, detail} },
		Now:    time.Now, Tick: time.Hour,
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
	if kind != FrameHello || json.Unmarshal(body, &hello) != nil || hello.Clipboard != clipboard {
		t.Fatalf("hello %x %s", kind, body)
	}
	t.Cleanup(func() { b.write(FrameEnd, []byte(`{}`)) })
	return &backgroundSession{t: t, browser: b, done: done, actions: actions}
}

func TestClipboardFilesArePastedIntoTheStagingFolderAndCopiedFilesDownloaded(t *testing.T) {
	copied := filepath.Join(t.TempDir(), "report.txt")
	if err := os.WriteFile(copied, []byte("quarterly numbers"), 0o600); err != nil {
		t.Fatal(err)
	}
	screenHandler := &clipboardScreen{root: t.TempDir(), copied: copied, placed: make(chan []string, 1)}
	bs := startControl(t, true, screenHandler)

	batch := bs.ok("clipboard.begin", nil)["batch"]
	upload := bs.ok("clipboard.upload", map[string]any{"batch": batch, "name": "notes.txt", "size": 5})
	transfer := uint32(upload["transfer"].(float64))
	chunk := binary.BigEndian.AppendUint32(nil, transfer)
	bs.browser.write(FrameChunk, append(chunk, "hello"...))
	bs.browser.write(FrameTransfer, mustJSON(transferBody{Transfer: transfer, Kind: "end"}))
	uploaded := bs.expectAction("clipboard.upload")
	if !strings.HasPrefix(uploaded.target, screenHandler.root) || filepath.Base(uploaded.target) != "notes.txt" {
		t.Fatalf("uploaded to %q", uploaded.target)
	}

	if count := bs.ok("clipboard.place", map[string]any{"batch": batch})["count"]; count != float64(1) {
		t.Fatalf("placed %v files", count)
	}
	placed := <-screenHandler.placed
	if len(placed) != 1 || placed[0] != uploaded.target {
		t.Fatalf("placed %v", placed)
	}
	if data, err := os.ReadFile(placed[0]); err != nil || string(data) != "hello" {
		t.Fatalf("staged file %q %v", data, err)
	}

	download := bs.ok("clipboard.download", map[string]any{"index": 0})
	if a := bs.expectAction("clipboard.download"); a.target != copied {
		t.Fatalf("downloaded %q", a.target)
	}
	id := uint32(download["transfer"].(float64))
	var received []byte
	for {
		kind, payload := bs.browser.read()
		if kind == FrameChunk && binary.BigEndian.Uint32(payload[:4]) == id {
			received = append(received, payload[4:]...)
		}
		if kind == FrameTransfer {
			var tb transferBody
			_ = json.Unmarshal(payload, &tb)
			if tb.Transfer == id && tb.Kind == "end" {
				break
			}
		}
	}
	if string(received) != "quarterly numbers" {
		t.Fatalf("received %q", received)
	}

	if result := bs.request("clipboard.download", map[string]any{"index": 3}); result["ok"] == true {
		t.Fatal("a file that is not on the clipboard was downloaded")
	}
	if result := bs.request("clipboard.upload", map[string]any{"batch": 99, "name": "x.txt", "size": 1}); result["ok"] == true {
		t.Fatal("an upload into an unknown batch was accepted")
	}
	if result := bs.request("download", map[string]any{"path": copied}); result["ok"] == true {
		t.Fatal("the file explorer download works in a remote control session")
	}
}

func TestClipboardFilesAreRefusedWhenThePolicyTurnsTheClipboardOff(t *testing.T) {
	screenHandler := &clipboardScreen{root: t.TempDir(), placed: make(chan []string, 1)}
	bs := startControl(t, false, screenHandler)
	result := bs.request("clipboard.begin", nil)
	if result["ok"] == true || !strings.Contains(result["error"].(string), "turned off") {
		t.Fatalf("result %v", result)
	}
}
