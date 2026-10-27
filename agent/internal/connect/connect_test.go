package connect

import (
	"context"
	"crypto/ecdh"
	"crypto/ecdsa"
	"crypto/ed25519"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net"
	"net/http"
	"net/http/cookiejar"
	"net/http/httptest"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/remote"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

const (
	instanceID    = "6f1d3c1e-5a4b-4f7e-9a31-2b8f0c1d2e3f"
	endpointID    = "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	participantID = "1c2d3e4f-5a6b-4c7d-8e9f-0a1b2c3d4e5f"
	sessionID     = "9f8e7d6c-5b4a-4938-8271-605f4e3d2c1b"
	keyID         = "a1b2c3d4e5f60718"
)

// fakePveproxy answers like pveproxy: a page that sets a cookie, the headers it received, and a WebSocket that echoes (the consoles).
type fakePveproxy struct {
	server *httptest.Server
	mu     sync.Mutex
	seen   []*http.Request
}

func newFakePveproxy(t *testing.T) *fakePveproxy {
	t.Helper()
	f := &fakePveproxy{}
	mux := http.NewServeMux()
	mux.HandleFunc("/", func(w http.ResponseWriter, r *http.Request) {
		f.mu.Lock()
		f.seen = append(f.seen, r.Clone(context.Background()))
		f.mu.Unlock()
		http.SetCookie(w, &http.Cookie{Name: "PVEAuthCookie", Value: "ticket", Path: "/"})
		_, _ = io.WriteString(w, "pve ok")
	})
	mux.HandleFunc("/ws", func(w http.ResponseWriter, r *http.Request) {
		conn, err := websocket.Accept(w, r, &websocket.AcceptOptions{InsecureSkipVerify: true})
		if err != nil {
			return
		}
		defer conn.CloseNow()
		for {
			typ, data, err := conn.Read(r.Context())
			if err != nil {
				return
			}
			_ = conn.Write(r.Context(), typ, append([]byte("echo "), data...))
		}
	})
	f.server = httptest.NewTLSServer(mux)
	t.Cleanup(f.server.Close)
	return f
}

func (f *fakePveproxy) certificate() string {
	sum := sha256.Sum256(f.server.Certificate().Raw)
	return hex.EncodeToString(sum[:])
}

func (f *fakePveproxy) last() *http.Request {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.seen[len(f.seen)-1]
}

// relay pairs the Fleeto Connect side and the endpoint side as the gateway does: the token first, "ready" with the endpoint's signed key,
// then frames it cannot read.
type relay struct {
	toEndpoint chan []byte
	toConnect  chan message
	closed     chan struct{}
	once       sync.Once
	hello      chan []byte
}

type message struct {
	text, binary []byte
}

func newRelay() *relay {
	return &relay{toEndpoint: make(chan []byte, 256), toConnect: make(chan message, 256), closed: make(chan struct{}), hello: make(chan []byte, 1)}
}

func (r *relay) close() { r.once.Do(func() { close(r.closed) }) }

type connectSide struct{ r *relay }

func (c connectSide) ReadText(ctx context.Context) ([]byte, []byte, error) {
	// As on a WebSocket, what was sent before the close arrives first.
	select {
	case m := <-c.r.toConnect:
		return m.text, m.binary, nil
	default:
	}
	select {
	case m := <-c.r.toConnect:
		return m.text, m.binary, nil
	case <-c.r.closed:
		return nil, nil, io.EOF
	case <-ctx.Done():
		return nil, nil, ctx.Err()
	}
}

func (c connectSide) WriteText(_ context.Context, text []byte) error {
	c.r.hello <- text
	return nil
}

func (c connectSide) Write(_ context.Context, frame []byte) error {
	select {
	case c.r.toEndpoint <- frame:
		return nil
	case <-c.r.closed:
		return io.EOF
	}
}

func (c connectSide) Close(string) error { c.r.close(); return nil }

type endpointSide struct{ r *relay }

func (e endpointSide) Read(ctx context.Context) ([]byte, error) {
	select {
	case frame := <-e.r.toEndpoint:
		return frame, nil
	default:
	}
	select {
	case frame := <-e.r.toEndpoint:
		return frame, nil
	case <-e.r.closed:
		return nil, io.EOF
	case <-ctx.Done():
		return nil, ctx.Err()
	}
}

func (e endpointSide) Write(_ context.Context, frame []byte) error {
	select {
	case e.r.toConnect <- message{binary: frame}:
		return nil
	case <-e.r.closed:
		return io.EOF
	}
}

func (e endpointSide) Close(string) error { e.r.close(); return nil }

type harness struct {
	pve     *fakePveproxy
	session *Session
	agent   chan string
	relay   *relay
}

// start runs an agent session of kind Proxmox web interface with the tunnel to the fake pveproxy, and opens the Fleeto Connect side.
func start(t *testing.T, kind agentv1.RemoteSessionKind, tunnel func(*fakePveproxy) *remote.Tunnel) (*harness, error) {
	t.Helper()
	pve := newFakePveproxy(t)
	signingPublic, signingKey, _ := ed25519.GenerateKey(rand.Reader)
	trust := signedconfig.Trust{SigningKey: signingPublic, KeyID: keyID, InstanceID: instanceID, EndpointID: endpointID}
	helperKey, _ := ecdh.X25519().GenerateKey(rand.Reader)
	certificateKey, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	now := time.Now()
	payload, _ := proto.Marshal(&agentv1.RemoteSessionToken{
		ParticipantId: participantID, SessionId: sessionID, InstanceId: instanceID, EndpointId: endpointID, Kind: kind,
		Component: agentv1.Component_COMPONENT_AGENT, TechnicianId: "7a6b5c4d-3e2f-4a1b-9c8d-7e6f5a4b3c2d", TechnicianName: "Tess Tech",
		BrowserPublicKey: helperKey.PublicKey().Bytes(), IssuedAt: timestamppb.New(now), ValidUntil: timestamppb.New(now.Add(time.Minute)),
		IdleTimeoutSeconds: 1800,
	})
	signature := ed25519.Sign(signingKey, append(append([]byte(remote.TokenContext), 0), payload...))
	token, err := remote.VerifyToken(&agentv1.SignedRemoteSession{Payload: payload, Signature: signature, KeyId: keyID}, trust,
		agentv1.Component_COMPONENT_AGENT, now)
	if err != nil {
		t.Fatal(err)
	}
	handshake, err := remote.NewEndpointHandshake(token, certificateKey)
	if err != nil {
		t.Fatal(err)
	}
	spki, _ := x509.MarshalPKIXPublicKey(&certificateKey.PublicKey)
	sum := sha256.Sum256(spki)

	r := newRelay()
	t.Cleanup(r.close)
	agentSession, err := remote.NewSession(remote.SessionOptions{
		Token: token, Keys: handshake.Keys, Transport: endpointSide{r}, Hello: remote.Hello{Hostname: "pve-01", Version: "test"}, Tunnel: tunnel(pve),
	})
	if err != nil {
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	t.Cleanup(cancel)
	agentDone := make(chan string, 1)
	go func() {
		// The gateway's part: wait for the token, answer with the endpoint's signed key, then start the endpoint.
		<-r.hello
		ready, _ := json.Marshal(map[string]any{
			"type": "ready", "endpointPublicKey": handshake.PublicKey, "signature": handshake.Signature, "certificatePublicKey": spki,
		})
		r.toConnect <- message{text: ready}
		agentDone <- agentSession.Run(ctx)
	}()
	ticket := &Ticket{
		ParticipantID: participantID, Token: base64.StdEncoding.EncodeToString(payload), Signature: base64.StdEncoding.EncodeToString(signature),
		KeyID: keyID, Fingerprints: []string{hex.EncodeToString(sum[:])}, Endpoint: "pve-01", EndpointID: endpointID,
	}
	session, err := Open(ctx, connectSide{r}, ticket, helperKey)
	if err != nil {
		return nil, err
	}
	go session.Run(ctx)
	return &harness{pve: pve, session: session, agent: agentDone, relay: r}, nil
}

func pveTunnel(pve *fakePveproxy) *remote.Tunnel {
	return &remote.Tunnel{
		Dial: func(ctx context.Context) (net.Conn, error) {
			var d net.Dialer
			return d.DialContext(ctx, "tcp", pve.server.Listener.Addr().String())
		},
		Certificate: pve.certificate(),
	}
}

// browser is a client that resolves every name below localhost to the loopback address, as browsers do, and keeps cookies.
func browser(t *testing.T) *http.Client {
	t.Helper()
	jar, _ := cookiejar.New(nil)
	return &http.Client{Jar: jar, Transport: &http.Transport{
		DialContext: func(ctx context.Context, network, address string) (net.Conn, error) {
			_, port, _ := net.SplitHostPort(address)
			var d net.Dialer
			return d.DialContext(ctx, "tcp", "127.0.0.1:"+port)
		},
	}}
}

func serve(t *testing.T, h *harness, certificate string) *Proxy {
	t.Helper()
	proxy, err := NewProxy(h.session.Dial, certificate, HostLabel("PVE-01.lab", endpointID), nil)
	if err != nil {
		t.Fatal(err)
	}
	go func() { _ = proxy.Serve() }()
	t.Cleanup(func() { _ = proxy.Close() })
	return proxy
}

func get(t *testing.T, client *http.Client, url string) (int, string) {
	t.Helper()
	response, err := client.Get(url)
	if err != nil {
		t.Fatal(err)
	}
	defer response.Body.Close()
	body, _ := io.ReadAll(response.Body)
	return response.StatusCode, string(body)
}

func TestTheWebInterfaceOpensThroughTheSessionForTheBrowserThatOpenedTheLinkOnly(t *testing.T) {
	h, err := start(t, agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_PROXMOX_WEB, pveTunnel)
	if err != nil {
		t.Fatal(err)
	}
	if h.session.Hello.Kind != "proxmox_web" || h.session.Hello.TunnelCertificate != h.pve.certificate() {
		t.Fatalf("hello = %+v", h.session.Hello)
	}
	proxy := serve(t, h, h.session.Hello.TunnelCertificate)
	if !strings.HasPrefix(proxy.Base, "http://pve-01-lab-0b6a4e2c.localhost:") {
		t.Fatalf("base = %s", proxy.Base)
	}

	// Another program on the PC, without the cookie: refused before anything reaches the host.
	other := browser(t)
	if code, _ := get(t, other, proxy.Base); code != http.StatusForbidden {
		t.Fatalf("without the cookie: %d", code)
	}

	client := browser(t)
	if code, body := get(t, client, proxy.URL); code != http.StatusOK || body != "pve ok" {
		t.Fatalf("first open: %d %q", code, body)
	}
	seen := h.pve.last()
	if seen.Host != upstreamHost {
		t.Fatalf("pveproxy saw host %q", seen.Host)
	}
	if _, err := seen.Cookie(cookieName); err == nil {
		t.Fatal("the cookie of Fleeto Connect reached pveproxy")
	}
	// The cookie of Proxmox comes back with the next request.
	if code, _ := get(t, client, proxy.Base+"api2/json/version"); code != http.StatusOK {
		t.Fatalf("second request: %d", code)
	}
	if c, err := h.pve.last().Cookie("PVEAuthCookie"); err != nil || c.Value != "ticket" {
		t.Fatalf("PVEAuthCookie did not reach pveproxy: %v", err)
	}
	// The one-time link works once.
	if code, _ := get(t, other, proxy.URL); code != http.StatusForbidden {
		t.Fatalf("link used twice: %d", code)
	}
	// A name that is not below localhost (DNS rebinding) is refused, also with the cookie.
	request, _ := http.NewRequest(http.MethodGet, proxy.Base, nil)
	request.Host = "attacker.example"
	response, err := client.Do(request)
	if err != nil {
		t.Fatal(err)
	}
	response.Body.Close()
	if response.StatusCode != http.StatusForbidden {
		t.Fatalf("foreign host: %d", response.StatusCode)
	}

	// A console: a WebSocket through the tunnel.
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()
	conn, _, err := websocket.Dial(ctx, strings.Replace(proxy.Base, "http://", "ws://", 1)+"ws", &websocket.DialOptions{HTTPClient: client})
	if err != nil {
		t.Fatal(err)
	}
	defer conn.CloseNow()
	if err := conn.Write(ctx, websocket.MessageText, []byte("hello")); err != nil {
		t.Fatal(err)
	}
	if _, data, err := conn.Read(ctx); err != nil || string(data) != "echo hello" {
		t.Fatalf("console: %q %v", data, err)
	}

	// Ending from Fleeto Connect ends the session on the endpoint.
	h.session.End("done")
	select {
	case <-h.agent:
	case <-time.After(10 * time.Second):
		t.Fatal("the endpoint session did not end")
	}
}

func TestAnotherCertificateThanTheEndpointReportedIsRefused(t *testing.T) {
	h, err := start(t, agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_PROXMOX_WEB, pveTunnel)
	if err != nil {
		t.Fatal(err)
	}
	// As if the tunnel reached another server than the one the agent read the certificate of.
	proxy := serve(t, h, strings.Repeat("ab", 32))
	if code, body := get(t, browser(t), proxy.URL); code != http.StatusBadGateway || !strings.Contains(body, "another certificate") {
		t.Fatalf("pinned certificate: %d %q", code, body)
	}
}

func TestOnlyASessionOfTheProxmoxWebInterfaceIsAccepted(t *testing.T) {
	// A remote background token on the agent component is refused by the token check already; a session that does not offer the web
	// interface (an agent without a tunnel) ends with the reason and Fleeto Connect does not open it.
	_, err := start(t, agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_PROXMOX_WEB, func(*fakePveproxy) *remote.Tunnel { return nil })
	if err == nil || !strings.Contains(err.Error(), "does not serve the Proxmox web interface") {
		t.Fatalf("err = %v", err)
	}
}

func TestLinksAndInstances(t *testing.T) {
	secret := strings.Repeat("A", 43)
	link, err := ParseLink("fleeto-connect://open?instance=RMM.Customer.example&claim=" + participantID + "." + secret)
	if err != nil || link.Instance != "rmm.customer.example" || link.ClaimID != participantID {
		t.Fatalf("link = %+v, %v", link, err)
	}
	for _, bad := range []string{
		"https://open?instance=rmm.customer.example&claim=" + participantID + "." + secret,
		"fleeto-connect://run?instance=rmm.customer.example&claim=" + participantID + "." + secret,
		"fleeto-connect://open?instance=10.0.0.1&claim=" + participantID + "." + secret,
		"fleeto-connect://open?instance=rmm.customer.example/evil&claim=" + participantID + "." + secret,
		"fleeto-connect://open?instance=user@rmm.customer.example&claim=" + participantID + "." + secret,
		"fleeto-connect://open?instance=rmm.customer.example&claim=" + participantID + ".short",
		"fleeto-connect://open?instance=rmm.customer.example&claim=not-a-guid." + secret,
	} {
		if _, err := ParseLink(bad); err == nil {
			t.Errorf("accepted %s", bad)
		}
	}
	if checkRelay(link, "wss://rmm.customer.example/relay/v1/sessions/"+participantID) != nil {
		t.Error("the relay of the instance was refused")
	}
	for _, bad := range []string{"wss://evil.example/relay/", "ws://rmm.customer.example/relay/", "https://rmm.customer.example/relay/"} {
		if checkRelay(link, bad) == nil {
			t.Errorf("accepted relay %s", bad)
		}
	}
}

type fakeUI struct {
	answer bool
	asked  int
	errors []string
}

func (u *fakeUI) Confirm(string, string) bool { u.asked++; return u.answer }
func (u *fakeUI) Error(_, message string)     { u.errors = append(u.errors, message) }
func (u *fakeUI) OpenBrowser(string) error    { return errors.New("no browser in tests") }

func TestAnInstanceIsUsedOnlyAfterTheTechnicianAllowedIt(t *testing.T) {
	var claims int
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		claims++
		w.WriteHeader(http.StatusBadRequest)
		_, _ = io.WriteString(w, `{"title":"Claim refused","detail":"This link was used before."}`)
	}))
	defer server.Close()
	ui := &fakeUI{}
	client := server.Client()
	// The test certificate names example.com; the claim goes to localhost.
	client.Transport.(*http.Transport).TLSClientConfig.ServerName = "example.com"
	app := &App{Version: "test", UI: ui, ConfigDir: t.TempDir(), HTTP: client, Logger: nilLogger()}
	link := "fleeto-connect://open?instance=" + strings.TrimPrefix(server.URL, "https://127.0.0.1") + "&claim=" + participantID + "." + strings.Repeat("A", 43)
	// The test server listens on an address; only a name is a valid instance, so the claim goes to localhost.
	link = strings.Replace(link, "instance=", "instance=localhost", 1)

	if err := app.Open(context.Background(), link); !errors.Is(err, ErrDeclined) || claims != 0 || ui.asked != 1 {
		t.Fatalf("declined: err %v, claims %d, asked %d", err, claims, ui.asked)
	}
	ui.answer = true
	if err := app.Open(context.Background(), link); err == nil || claims != 1 || !strings.Contains(ui.errors[0], "used before") {
		t.Fatalf("allowed: err %v, claims %d, errors %v", err, claims, ui.errors)
	}
	// Remembered: not asked again.
	_ = app.Open(context.Background(), link)
	if ui.asked != 2 || claims != 2 {
		t.Fatalf("asked %d, claims %d", ui.asked, claims)
	}
}

func nilLogger() *slog.Logger { return slog.New(slog.DiscardHandler) }
