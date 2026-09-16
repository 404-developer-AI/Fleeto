package remote

import (
	"bytes"
	"context"
	"crypto/ecdh"
	"crypto/ecdsa"
	"crypto/ed25519"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/binary"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"strings"
	"sync"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

const (
	instanceID    = "6f1d3c1e-5a4b-4f7e-9a31-2b8f0c1d2e3f"
	endpointID    = "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	participantID = "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f"
	sessionID     = "9f8e7d6c-5b4a-4938-8271-605f4e3d2c1b"
	keyID         = "a1b2c3d4e5f60718"
)

type fixture struct {
	signingKey ed25519.PrivateKey
	trust      signedconfig.Trust
	browser    *ecdh.PrivateKey
	endpoint   *ecdsa.PrivateKey
	now        time.Time
}

func newFixture(t *testing.T) *fixture {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	browser, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	endpoint, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return &fixture{
		signingKey: priv,
		trust:      signedconfig.Trust{SigningKey: pub, KeyID: keyID, InstanceID: instanceID, EndpointID: endpointID},
		browser:    browser,
		endpoint:   endpoint,
		now:        time.Date(2026, 9, 16, 12, 0, 0, 0, time.UTC),
	}
}

func (f *fixture) token(edit func(*agentv1.RemoteSessionToken)) *agentv1.RemoteSessionToken {
	token := &agentv1.RemoteSessionToken{
		ParticipantId: participantID, SessionId: sessionID, InstanceId: instanceID, EndpointId: endpointID,
		Kind: agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_REMOTE_BACKGROUND, Component: agentv1.Component_COMPONENT_WATCHDOG,
		TechnicianId: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d", TechnicianName: "Tess Tech", BrowserPublicKey: f.browser.PublicKey().Bytes(),
		IssuedAt: timestamppb.New(f.now), ValidUntil: timestamppb.New(f.now.Add(time.Minute)), IdleTimeoutSeconds: 1800,
	}
	if edit != nil {
		edit(token)
	}
	return token
}

// sign mirrors Ed25519.Sign(privateKey, SignatureContexts.RemoteSession, payload) in fleeto-signer.
func (f *fixture) sign(t *testing.T, token *agentv1.RemoteSessionToken) *agentv1.SignedRemoteSession {
	t.Helper()
	payload, err := proto.Marshal(token)
	if err != nil {
		t.Fatal(err)
	}
	message := append(append([]byte(TokenContext), 0), payload...)
	return &agentv1.SignedRemoteSession{Payload: payload, Signature: ed25519.Sign(f.signingKey, message), KeyId: keyID}
}

func TestValidTokenIsAccepted(t *testing.T) {
	f := newFixture(t)
	token, err := VerifyToken(f.sign(t, f.token(nil)), f.trust, agentv1.Component_COMPONENT_WATCHDOG, f.now)
	if err != nil {
		t.Fatalf("expected acceptance, got %v", err)
	}
	if token.GetParticipantId() != participantID || token.IdleTimeout() != 30*time.Minute {
		t.Fatalf("unexpected token %v", token)
	}
}

func TestTokensThatDoNotBelongHereAreRefused(t *testing.T) {
	f := newFixture(t)
	_, otherKey, _ := ed25519.GenerateKey(rand.Reader)
	cases := map[string]func() *agentv1.SignedRemoteSession{
		"another instance": func() *agentv1.SignedRemoteSession {
			return f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) { tk.InstanceId = "11111111-2222-4333-8444-555555555555" }))
		},
		"another endpoint": func() *agentv1.SignedRemoteSession {
			return f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) { tk.EndpointId = "11111111-2222-4333-8444-555555555555" }))
		},
		"the agent instead of the watchdog": func() *agentv1.SignedRemoteSession {
			return f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) { tk.Component = agentv1.Component_COMPONENT_AGENT }))
		},
		"remote control at the watchdog": func() *agentv1.SignedRemoteSession {
			return f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) {
				tk.Kind = agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_REMOTE_CONTROL
			}))
		},
		"expired": func() *agentv1.SignedRemoteSession {
			return f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) {
				tk.IssuedAt = timestamppb.New(f.now.Add(-10 * time.Minute))
				tk.ValidUntil = timestamppb.New(f.now.Add(-9 * time.Minute))
			}))
		},
		"a long validity": func() *agentv1.SignedRemoteSession {
			return f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) { tk.ValidUntil = timestamppb.New(f.now.Add(time.Hour)) }))
		},
		"no browser key": func() *agentv1.SignedRemoteSession {
			return f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) { tk.BrowserPublicKey = make([]byte, 32) }))
		},
		"another signing key": func() *agentv1.SignedRemoteSession {
			signed := f.sign(t, f.token(nil))
			signed.Signature = ed25519.Sign(otherKey, append(append([]byte(TokenContext), 0), signed.Payload...))
			return signed
		},
		"the job context": func() *agentv1.SignedRemoteSession {
			signed := f.sign(t, f.token(nil))
			signed.Signature = ed25519.Sign(f.signingKey, append(append([]byte("fleeto-job-v1"), 0), signed.Payload...))
			return signed
		},
		"a changed payload": func() *agentv1.SignedRemoteSession {
			signed := f.sign(t, f.token(nil))
			changed, _ := proto.Marshal(f.token(func(tk *agentv1.RemoteSessionToken) { tk.IdleTimeoutSeconds = 28800 }))
			signed.Payload = changed
			return signed
		},
	}
	for name, build := range cases {
		t.Run(name, func(t *testing.T) {
			if _, err := VerifyToken(build(), f.trust, agentv1.Component_COMPONENT_WATCHDOG, f.now); err == nil {
				t.Fatal("expected refusal")
			}
		})
	}
}

func TestATokenOpensOneSessionOnly(t *testing.T) {
	replay := NewReplay()
	now := time.Now()
	if !replay.Accept(participantID, now.Add(time.Minute), now) {
		t.Fatal("first use refused")
	}
	if replay.Accept(strings.ToUpper(participantID), now.Add(time.Minute), now.Add(time.Second)) {
		t.Fatal("second use accepted")
	}
}

func TestOnlyAManagedSignedConfigurationServesSessions(t *testing.T) {
	f := newFixture(t)
	signConfig := func(tier agentv1.Tier, key ed25519.PrivateKey) []byte {
		payload, _ := proto.Marshal(&agentv1.AgentConfig{InstanceId: instanceID, EndpointId: endpointID, Version: 4, Tier: tier})
		message := append(append([]byte(signedconfig.Context), 0), payload...)
		stored, _ := proto.Marshal(&agentv1.SignedConfig{Payload: payload, Signature: ed25519.Sign(key, message), KeyId: keyID})
		return stored
	}
	_, other, _ := ed25519.GenerateKey(rand.Reader)
	if !ManagedConfig(signConfig(agentv1.Tier_TIER_MANAGED, f.signingKey), f.trust) {
		t.Fatal("a managed configuration was not accepted")
	}
	if ManagedConfig(signConfig(agentv1.Tier_TIER_AGENT_ONLY, f.signingKey), f.trust) {
		t.Fatal("an agent-only configuration was accepted")
	}
	if ManagedConfig(signConfig(agentv1.Tier_TIER_MANAGED, other), f.trust) {
		t.Fatal("a configuration signed with another key was accepted")
	}
	if ManagedConfig(nil, f.trust) {
		t.Fatal("a missing configuration was accepted")
	}
}

// handshake runs both sides of the key exchange like browser and endpoint do.
func (f *fixture) handshake(t *testing.T) (*Token, *EndpointHandshake, Keys) {
	t.Helper()
	token, err := VerifyToken(f.sign(t, f.token(nil)), f.trust, agentv1.Component_COMPONENT_WATCHDOG, f.now)
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
	return token, endpoint, browserKeys
}

// spki is the certificate public key the endpoint sends; fingerprints is what the instance recorded for it.
func (f *fixture) spki(t *testing.T) []byte {
	t.Helper()
	der, err := x509.MarshalPKIXPublicKey(&f.endpoint.PublicKey)
	if err != nil {
		t.Fatal(err)
	}
	return der
}

func (f *fixture) fingerprints(t *testing.T) []string {
	sum := sha256.Sum256(f.spki(t))
	return []string{"0000", hex.EncodeToString(sum[:])}
}

func TestBrowserAndEndpointDeriveTheSameKeys(t *testing.T) {
	f := newFixture(t)
	_, endpoint, browserKeys := f.handshake(t)
	if browserKeys != endpoint.Keys || browserKeys.BrowserToEndpoint == browserKeys.EndpointToBrowser {
		t.Fatal("the derived keys differ or are not directional")
	}
}

func TestARelayCannotSwapTheEndpointKey(t *testing.T) {
	f := newFixture(t)
	token, endpoint, _ := f.handshake(t)

	// The relay puts its own key in the endpoint's place, with or without the endpoint's signature.
	relayKey, _ := ecdh.X25519().GenerateKey(rand.Reader)
	if _, err := BrowserHandshake(f.browser, token.PayloadHash, relayKey.PublicKey().Bytes(), endpoint.Signature, f.spki(t), f.fingerprints(t)); err == nil {
		t.Fatal("a swapped endpoint key was accepted")
	}
	relaySigner, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	forged, err := endpointHandshake(token, relaySigner, relayKey)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := BrowserHandshake(f.browser, token.PayloadHash, forged.PublicKey, forged.Signature, f.spki(t), f.fingerprints(t)); err == nil {
		t.Fatal("a key signed by another certificate key was accepted")
	}
	// The relay also sends its own certificate key: its fingerprint is not one the instance recorded.
	relaySpki, _ := x509.MarshalPKIXPublicKey(&relaySigner.PublicKey)
	if _, err := BrowserHandshake(f.browser, token.PayloadHash, forged.PublicKey, forged.Signature, relaySpki, f.fingerprints(t)); err == nil {
		t.Fatal("a certificate key the instance never recorded was accepted")
	}

	// A signature made for another token cannot be replayed onto this one.
	other := f.sign(t, f.token(func(tk *agentv1.RemoteSessionToken) { tk.ParticipantId = "22222222-3333-4444-8555-666666666666" }))
	otherToken, err := VerifyToken(other, f.trust, agentv1.Component_COMPONENT_WATCHDOG, f.now)
	if err != nil {
		t.Fatal(err)
	}
	if _, err := BrowserHandshake(f.browser, otherToken.PayloadHash, endpoint.PublicKey, endpoint.Signature, f.spki(t), f.fingerprints(t)); err == nil {
		t.Fatal("the signature of another token was accepted")
	}
}

func TestARelayThatDoesNotHoldTheBrowserKeyCannotReadOrWrite(t *testing.T) {
	f := newFixture(t)
	_, endpoint, _ := f.handshake(t)
	send, _ := NewCipher(endpoint.Keys.EndpointToBrowser)
	frame, _ := send.Seal([]byte("secret output"))

	// A relay with its own X25519 key derives other keys from the same public values.
	relay, _ := ecdh.X25519().GenerateKey(rand.Reader)
	endpointPublic, _ := ecdh.X25519().NewPublicKey(endpoint.PublicKey)
	shared, _ := relay.ECDH(endpointPublic)
	guess, _ := DeriveKeys(shared, [32]byte{}, f.browser.PublicKey().Bytes(), endpoint.PublicKey)
	receive, _ := NewCipher(guess.EndpointToBrowser)
	if _, err := receive.Open(frame); !errors.Is(err, ErrFrame) {
		t.Fatal("a relay without the browser key decrypted a frame")
	}
	if bytes.Contains(frame, []byte("secret")) {
		t.Fatal("the frame carries plaintext")
	}
}

func TestTamperedReplayedReorderedAndReflectedFramesFail(t *testing.T) {
	f := newFixture(t)
	_, endpoint, browserKeys := f.handshake(t)
	seal := func(key [32]byte, messages ...string) [][]byte {
		c, _ := NewCipher(key)
		out := make([][]byte, 0, len(messages))
		for _, m := range messages {
			frame, err := c.Seal([]byte(m))
			if err != nil {
				t.Fatal(err)
			}
			out = append(out, frame)
		}
		return out
	}
	frames := seal(browserKeys.BrowserToEndpoint, "one", "two", "three")

	t.Run("in order", func(t *testing.T) {
		c, _ := NewCipher(endpoint.Keys.BrowserToEndpoint)
		for i, frame := range frames {
			if plaintext, err := c.Open(frame); err != nil || string(plaintext) != []string{"one", "two", "three"}[i] {
				t.Fatalf("frame %d: %q %v", i, plaintext, err)
			}
		}
	})
	t.Run("tampered", func(t *testing.T) {
		c, _ := NewCipher(endpoint.Keys.BrowserToEndpoint)
		changed := bytes.Clone(frames[0])
		changed[0] ^= 1
		if _, err := c.Open(changed); !errors.Is(err, ErrFrame) {
			t.Fatal("a changed frame was accepted")
		}
		// After a failure the session is over, even for the genuine frame.
		if _, err := c.Open(frames[0]); !errors.Is(err, ErrFrame) {
			t.Fatal("the cipher kept working after a failure")
		}
	})
	t.Run("replayed", func(t *testing.T) {
		c, _ := NewCipher(endpoint.Keys.BrowserToEndpoint)
		_, _ = c.Open(frames[0])
		if _, err := c.Open(frames[0]); !errors.Is(err, ErrFrame) {
			t.Fatal("a replayed frame was accepted")
		}
	})
	t.Run("reordered or dropped", func(t *testing.T) {
		c, _ := NewCipher(endpoint.Keys.BrowserToEndpoint)
		if _, err := c.Open(frames[1]); !errors.Is(err, ErrFrame) {
			t.Fatal("a frame out of order was accepted")
		}
	})
	t.Run("reflected", func(t *testing.T) {
		// The relay sends the endpoint's own output back to it.
		out := seal(endpoint.Keys.EndpointToBrowser, "output")
		c, _ := NewCipher(endpoint.Keys.BrowserToEndpoint)
		if _, err := c.Open(out[0]); !errors.Is(err, ErrFrame) {
			t.Fatal("a reflected frame was accepted")
		}
	})
	t.Run("truncated", func(t *testing.T) {
		c, _ := NewCipher(endpoint.Keys.BrowserToEndpoint)
		if _, err := c.Open(frames[0][:len(frames[0])-1]); !errors.Is(err, ErrFrame) {
			t.Fatal("a truncated frame was accepted")
		}
	})
}

// pipe is an in-memory relay; tamper may change what passes from browser to endpoint.
type pipe struct {
	toEndpoint chan []byte
	toBrowser  chan []byte
	closed     chan struct{}
	once       sync.Once
}

func newPipe() *pipe {
	return &pipe{toEndpoint: make(chan []byte, 64), toBrowser: make(chan []byte, 64), closed: make(chan struct{})}
}

type endpointSide struct{ p *pipe }

func (e endpointSide) Read(ctx context.Context) ([]byte, error) {
	select {
	case frame := <-e.p.toEndpoint:
		return frame, nil
	case <-e.p.closed:
		return nil, io.EOF
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func (e endpointSide) Write(ctx context.Context, frame []byte) error {
	select {
	case e.p.toBrowser <- frame:
		return nil
	case <-e.p.closed:
		return io.EOF
	case <-ctx.Done():
		return ctx.Err()
	}
}

func (e endpointSide) Close(string) error {
	e.p.once.Do(func() { close(e.p.closed) })
	return nil
}

// fakeTerminal echoes its input as output.
type fakeTerminal struct {
	output chan []byte
	done   chan struct{}
	once   sync.Once
}

func newFakeTerminal() *fakeTerminal {
	return &fakeTerminal{output: make(chan []byte, 16), done: make(chan struct{})}
}

func (t *fakeTerminal) Read(p []byte) (int, error) {
	select {
	case data := <-t.output:
		return copy(p, data), nil
	case <-t.done:
		return 0, io.EOF
	}
}

func (t *fakeTerminal) Write(p []byte) (int, error) {
	if string(p) == "exit\r" {
		t.once.Do(func() { close(t.done) })
		return len(p), nil
	}
	t.output <- append([]byte("echo:"), p...)
	return len(p), nil
}

func (t *fakeTerminal) Resize(int, int) error { return nil }
func (t *fakeTerminal) Close() error          { t.once.Do(func() { close(t.done) }); return nil }
func (t *fakeTerminal) Wait() (int, error)    { <-t.done; return 0, nil }
func (t *fakeTerminal) PTY() bool             { return true }

type browser struct {
	t    *testing.T
	p    *pipe
	send *Cipher
	recv *Cipher
}

func (b *browser) write(kind byte, body []byte) {
	frame, err := b.send.Seal(append([]byte{kind}, body...))
	if err != nil {
		b.t.Fatal(err)
	}
	b.p.toEndpoint <- frame
}

func (b *browser) read() (byte, []byte) {
	select {
	case frame := <-b.p.toBrowser:
		plaintext, err := b.recv.Open(frame)
		if err != nil {
			b.t.Fatal(err)
		}
		return plaintext[0], plaintext[1:]
	case <-time.After(5 * time.Second):
		b.t.Fatal("no frame from the endpoint")
		return 0, nil
	}
}

func startSession(t *testing.T, now func() time.Time, tick time.Duration) (*browser, chan string) {
	t.Helper()
	f := newFixture(t)
	token, endpoint, browserKeys := f.handshake(t)
	p := newPipe()
	session, err := NewSession(SessionOptions{
		Token: token, Keys: endpoint.Keys, Transport: endpointSide{p}, Hello: Hello{Hostname: "SRV-01", Platform: "windows", Shells: []string{"powershell"}},
		Open: func(shell string, cols, rows int) (Terminal, error) { return newFakeTerminal(), nil }, Now: now, Tick: tick,
	})
	if err != nil {
		t.Fatal(err)
	}
	done := make(chan string, 1)
	go func() { done <- session.Run(context.Background()) }()
	send, _ := NewCipher(browserKeys.BrowserToEndpoint)
	recv, _ := NewCipher(browserKeys.EndpointToBrowser)
	return &browser{t: t, p: p, send: send, recv: recv}, done
}

func TestASessionRunsATerminalOverEncryptedFrames(t *testing.T) {
	b, done := startSession(t, time.Now, time.Hour)
	kind, body := b.read()
	var hello Hello
	if kind != FrameHello || json.Unmarshal(body, &hello) != nil || hello.Hostname != "SRV-01" || hello.IdleTimeoutSeconds != 1800 {
		t.Fatalf("unexpected hello %x %s", kind, body)
	}

	b.write(FrameOpen, []byte(`{"channel":1,"service":"terminal","shell":"powershell","cols":120,"rows":30}`))
	if kind, body := b.read(); kind != FrameOpened || !strings.Contains(string(body), `"pty":true`) {
		t.Fatalf("unexpected opened %x %s", kind, body)
	}
	data := binary.BigEndian.AppendUint16(nil, 1)
	b.write(FrameData, append(data, "dir\r"...))
	if kind, body := b.read(); kind != FrameData || string(body[2:]) != "echo:dir\r" {
		t.Fatalf("unexpected output %x %q", kind, body)
	}
	b.write(FrameData, append(binary.BigEndian.AppendUint16(nil, 1), "exit\r"...))
	if kind, body := b.read(); kind != FrameClosed || !strings.Contains(string(body), `"exitCode":0`) {
		t.Fatalf("unexpected closed %x %s", kind, body)
	}

	b.write(FrameEnd, []byte(`{"reason":"done"}`))
	select {
	case reason := <-done:
		if reason != "ended by the technician" {
			t.Fatalf("unexpected reason %q", reason)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the session did not end")
	}
}

func TestAForgedFrameEndsTheSession(t *testing.T) {
	b, done := startSession(t, time.Now, time.Hour)
	b.read()
	b.p.toEndpoint <- bytes.Repeat([]byte{7}, 64)
	select {
	case reason := <-done:
		if !strings.Contains(reason, "authentication") {
			t.Fatalf("unexpected reason %q", reason)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the session kept running after a forged frame")
	}
}

func TestAnIdleSessionIsWarnedAndClosed(t *testing.T) {
	var mu sync.Mutex
	clock := time.Now()
	now := func() time.Time { mu.Lock(); defer mu.Unlock(); return clock }
	advance := func(d time.Duration) { mu.Lock(); clock = clock.Add(d); mu.Unlock() }
	b, done := startSession(t, now, 10*time.Millisecond)
	b.read()

	advance(29 * time.Minute)
	if kind, _ := b.read(); kind != FrameIdleWarning {
		t.Fatalf("expected an idle warning, got %x", kind)
	}
	advance(2 * time.Minute)
	if kind, body := b.read(); kind != FrameEnd || !strings.Contains(string(body), "30 minutes without input") {
		t.Fatalf("expected the end, got %x %s", kind, body)
	}
	select {
	case reason := <-done:
		if reason != "idle timeout" {
			t.Fatalf("unexpected reason %q", reason)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the idle session did not end")
	}
}
