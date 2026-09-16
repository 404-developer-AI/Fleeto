package agent

import (
	"context"
	"crypto"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/release"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
)

// The test binary doubles as a release binary: run with FLEETO_PROBE_VERSION set and "version --short", it prints that version.
func TestMain(m *testing.M) {
	if v := os.Getenv("FLEETO_PROBE_VERSION"); v != "" && len(os.Args) >= 3 && os.Args[1] == "version" && os.Args[2] == "--short" {
		fmt.Println(v)
		os.Exit(0)
	}
	os.Exit(m.Run())
}

type recordingController struct {
	mu      sync.Mutex
	state   svcctl.State
	started []string
}

func (c *recordingController) Query(string) (svcctl.State, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.state, nil
}

func (c *recordingController) Start(name string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.started = append(c.started, name)
	c.state = svcctl.StateRunning
	return nil
}

func (c *recordingController) Stop(context.Context, string, time.Duration) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.state = svcctl.StateStopped
	return nil
}

func (c *recordingController) WaitRunning(context.Context, string, time.Duration) error { return nil }

// An agent that is offered a release installs the missing watchdog: it downloads and verifies the binary, gets a certificate for a new
// watchdog key over its own session, writes the watchdog state and creates and starts the service, and reports the installation.
func TestTheAgentInstallsAMissingWatchdogFromAVerifiedRelease(t *testing.T) {
	g := newFakeGateway(t)
	store := enrollForTest(t, g)

	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	binary, err := os.ReadFile(self)
	if err != nil {
		t.Fatal(err)
	}
	t.Setenv("FLEETO_PROBE_VERSION", "0.2.1")
	sum := sha256.Sum256(binary)
	file := release.ExpectedFile(release.ComponentWatchdog, runtime.GOOS, runtime.GOARCH)
	var downloads sync.WaitGroup
	downloads.Add(1)
	var once sync.Once
	g.server.Config.Handler.(*http.ServeMux).HandleFunc("GET /v1/releases/0.2.1/"+file, func(w http.ResponseWriter, r *http.Request) {
		if cert, _ := g.clientCertificate(r); cert == nil {
			http.Error(w, "client certificate required", http.StatusUnauthorized)
			return
		}
		once.Do(downloads.Done)
		_, _ = w.Write(binary)
	})

	releasePub, releasePriv, _ := ed25519.GenerateKey(rand.Reader)
	manifest := []byte(fmt.Sprintf(`{"formatVersion":1,"version":"0.2.1","agentBinaries":[{"component":"watchdog","platform":%q,"architecture":%q,"file":%q,"sha256":%q,"size":%d}]}`,
		runtime.GOOS, runtime.GOARCH, file, hex.EncodeToString(sum[:]), len(binary)))

	watchdogDir := t.TempDir()
	programDir := t.TempDir()
	controller := &recordingController{state: svcctl.StateNotInstalled}
	var created []svcctl.Definition
	opts := testOptions(store)
	opts.Watchdog = &WatchdogOptions{
		StateDir: watchdogDir, ProgramDir: programDir, Controller: controller, Keys: []ed25519.PublicKey{releasePub}, UpdateMaxDelay: -1,
		UpdateTransientRetry: 200 * time.Millisecond,
		Create: func(def svcctl.Definition) error {
			controller.mu.Lock()
			created = append(created, def)
			controller.state = svcctl.StateStopped
			controller.mu.Unlock()
			return nil
		},
		Delete: func(context.Context, string, time.Duration) error { return nil },
	}
	startAgent(t, opts)
	c := g.accept(t)
	c.expect(t, "Hello", func(m *agentv1.AgentMessage) bool {
		return isType[*agentv1.AgentMessage_Hello](m) && m.GetHello().GetComponent() == agentv1.Component_COMPONENT_AGENT
	})
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_HelloAck{HelloAck: &agentv1.HelloAck{EndpointId: gwEndpoint, HeartbeatIntervalSeconds: 1}}})

	// An offer with a signature from an untrusted key changes nothing.
	_, otherPriv, _ := ed25519.GenerateKey(rand.Reader)
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_UpdateOffer{UpdateOffer: &agentv1.UpdateOffer{
		Manifest: manifest, Signature: ed25519.Sign(otherPriv, manifest), UpdateAllowed: true,
	}}})
	time.Sleep(300 * time.Millisecond)

	// A release the ring does not allow yet, newer than the agent itself: no watchdog of it is installed.
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_UpdateOffer{UpdateOffer: &agentv1.UpdateOffer{
		Manifest: manifest, Signature: ed25519.Sign(releasePriv, manifest), UpdateAllowed: false,
	}}})
	time.Sleep(300 * time.Millisecond)
	controller.mu.Lock()
	provisioned := len(created) > 0
	controller.mu.Unlock()
	if provisioned {
		t.Fatal("a watchdog was installed from a release the ring does not allow")
	}

	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_UpdateOffer{UpdateOffer: &agentv1.UpdateOffer{
		Manifest: manifest, Signature: ed25519.Sign(releasePriv, manifest), UpdateAllowed: true,
	}}})

	// The signer cannot answer right now: the agent reports the failure and asks again within the transient retry, not after an hour.
	c.expect(t, "a watchdog certificate request", func(m *agentv1.AgentMessage) bool {
		return isType[*agentv1.AgentMessage_WatchdogCertificate](m)
	})
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_WatchdogCertificate{WatchdogCertificate: &agentv1.WatchdogCertificateResponse{
		Error: "The watchdog certificate could not be issued right now. The agent retries later.", Temporary: true,
	}}})
	c.expect(t, "the failed report", func(m *agentv1.AgentMessage) bool {
		return m.GetUpdateStatus().GetState() == agentv1.UpdateState_UPDATE_STATE_FAILED &&
			strings.Contains(m.GetUpdateStatus().GetDetail(), "could not be issued right now")
	})

	request := c.expect(t, "a second watchdog certificate request", func(m *agentv1.AgentMessage) bool {
		return isType[*agentv1.AgentMessage_WatchdogCertificate](m)
	})
	downloads.Wait()
	agentState, _ := store.Load()
	agentCert, _ := agentState.Certificate()
	certDER, err := g.ca.IssueAgent(request.GetWatchdogCertificate().GetCsrDer(), gwEndpoint, gwInstance, time.Now().Add(-time.Minute), 90*24*time.Hour)
	if err != nil {
		t.Fatalf("the watchdog CSR is invalid: %v", err)
	}
	c.send(t, &agentv1.ServerMessage{Body: &agentv1.ServerMessage_WatchdogCertificate{WatchdogCertificate: &agentv1.WatchdogCertificateResponse{CertificateDer: certDER}}})

	installed := c.expect(t, "the installed report", func(m *agentv1.AgentMessage) bool {
		return m.GetUpdateStatus().GetState() == agentv1.UpdateState_UPDATE_STATE_INSTALLED
	})
	if installed.GetUpdateStatus().GetComponent() != agentv1.Component_COMPONENT_WATCHDOG || installed.GetUpdateStatus().GetVersion() != "0.2.1" {
		t.Fatalf("unexpected report %+v", installed.GetUpdateStatus())
	}

	watchdogState, err := state.NewStore(watchdogDir, platform.AccessCurrentUser).Load()
	if err != nil {
		t.Fatalf("the watchdog state was not written: %v", err)
	}
	if watchdogState.EndpointID != gwEndpoint || watchdogState.Server != agentState.Server {
		t.Fatalf("the watchdog state names another endpoint or server: %+v", watchdogState)
	}
	watchdogCert, _ := watchdogState.Certificate()
	if agentCert.PublicKey.(interface{ Equal(crypto.PublicKey) bool }).Equal(watchdogCert.PublicKey) {
		t.Fatal("the watchdog must have its own key, not the key of the agent")
	}
	key, err := keystore.Open(watchdogDir, watchdogState.Key)
	if err != nil || !keystore.SamePublicKey(watchdogCert, key) {
		t.Fatalf("the watchdog key does not match its certificate: %v", err)
	}
	_ = key.Close()

	if len(created) != 1 || created[0].Name != WatchdogServiceName || created[0].Executable != filepath.Join(programDir, platform.WatchdogBinaryName) {
		t.Fatalf("the watchdog service was not created as expected: %+v", created)
	}
	if data, err := os.ReadFile(created[0].Executable); err != nil || len(data) != len(binary) {
		t.Fatal("the watchdog binary was not installed")
	}
	controller.mu.Lock()
	started := append([]string(nil), controller.started...)
	controller.mu.Unlock()
	if len(started) == 0 || started[0] != WatchdogServiceName {
		t.Fatalf("the watchdog service was not started: %v", started)
	}

	// The next heartbeat reports the watchdog service.
	c.expect(t, "a heartbeat with the watchdog state", func(m *agentv1.AgentMessage) bool {
		return m.GetHeartbeat().GetPeer().GetState() == agentv1.ServiceState_SERVICE_STATE_RUNNING
	})
}
