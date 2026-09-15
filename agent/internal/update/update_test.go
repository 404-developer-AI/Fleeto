package update

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/release"
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

func discard() *slog.Logger { return slog.New(slog.NewTextHandler(io.Discard, nil)) }

// fakeController simulates a service whose binary is a file; OnStart runs when the service starts (the new version connecting).
type fakeController struct {
	mu       sync.Mutex
	state    svcctl.State
	stopErr  error
	startErr error
	starts   int
	stops    int
	onStart  func()
}

func (f *fakeController) Query(string) (svcctl.State, error) {
	f.mu.Lock()
	defer f.mu.Unlock()
	return f.state, nil
}

func (f *fakeController) Start(string) error {
	f.mu.Lock()
	f.starts++
	if f.startErr != nil {
		f.mu.Unlock()
		return f.startErr
	}
	f.state = svcctl.StateRunning
	onStart := f.onStart
	f.mu.Unlock()
	if onStart != nil {
		onStart()
	}
	return nil
}

func (f *fakeController) Stop(context.Context, string, time.Duration) error {
	f.mu.Lock()
	defer f.mu.Unlock()
	f.stops++
	if f.stopErr != nil {
		return f.stopErr
	}
	f.state = svcctl.StateStopped
	return nil
}

func (f *fakeController) WaitRunning(context.Context, string, time.Duration) error { return nil }

func writeFile(t *testing.T, path, content string) (sha string, size int64) {
	t.Helper()
	if err := os.WriteFile(path, []byte(content), 0o700); err != nil {
		t.Fatal(err)
	}
	sum := sha256.Sum256([]byte(content))
	return hex.EncodeToString(sum[:]), int64(len(content))
}

func readFile(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func TestInstallReplacesTheBinaryWhenTheNewVersionComesUp(t *testing.T) {
	dir := t.TempDir()
	exe := filepath.Join(dir, "fleeto-agent.exe")
	writeFile(t, exe, "old binary")
	staged := filepath.Join(dir, "staged")
	sha, size := writeFile(t, staged, "new binary")
	controller := &fakeController{state: svcctl.StateRunning}

	outcome, err := Install(context.Background(), InstallRequest{
		Controller: controller, Service: "fleeto-agent", Exe: exe, Staged: staged, SHA256: sha, Size: size, Version: "0.2.2",
		JournalDir: dir, Access: platform.AccessCurrentUser, Logger: discard(),
		Healthy: func(context.Context, time.Time) error { return nil },
	})
	if outcome != Installed || err != nil {
		t.Fatalf("expected installed, got %v: %v", outcome, err)
	}
	if readFile(t, exe) != "new binary" || readFile(t, exe+".previous") != "old binary" {
		t.Fatal("the binaries are not where they should be")
	}
	if controller.stops != 1 || controller.starts != 1 {
		t.Fatalf("expected one stop and one start, got %d and %d", controller.stops, controller.starts)
	}
	if _, err := os.Stat(filepath.Join(dir, JournalFileName)); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the journal was not removed")
	}
}

func TestInstallRollsBackWhenTheNewVersionDoesNotComeUp(t *testing.T) {
	dir := t.TempDir()
	exe := filepath.Join(dir, "fleeto-agent.exe")
	writeFile(t, exe, "old binary")
	staged := filepath.Join(dir, "staged")
	sha, size := writeFile(t, staged, "broken binary")
	controller := &fakeController{state: svcctl.StateRunning}

	outcome, err := Install(context.Background(), InstallRequest{
		Controller: controller, Service: "fleeto-agent", Exe: exe, Staged: staged, SHA256: sha, Size: size, Version: "0.2.2",
		JournalDir: dir, Access: platform.AccessCurrentUser, Logger: discard(),
		Healthy: func(context.Context, time.Time) error { return ErrNotHealthy },
	})
	if outcome != RolledBack || !errors.Is(err, ErrNotHealthy) {
		t.Fatalf("expected a rollback, got %v: %v", outcome, err)
	}
	if readFile(t, exe) != "old binary" {
		t.Fatal("the previous binary was not restored")
	}
	if controller.state != svcctl.StateRunning || controller.starts != 2 {
		t.Fatalf("the previous version was not started again (starts %d)", controller.starts)
	}
}

func TestInstallRefusesAStagedBinaryThatChanged(t *testing.T) {
	dir := t.TempDir()
	exe := filepath.Join(dir, "fleeto-agent.exe")
	writeFile(t, exe, "old binary")
	staged := filepath.Join(dir, "staged")
	sha, size := writeFile(t, staged, "new binary")
	writeFile(t, staged, "tampered!!")
	controller := &fakeController{state: svcctl.StateRunning}

	outcome, err := Install(context.Background(), InstallRequest{
		Controller: controller, Service: "fleeto-agent", Exe: exe, Staged: staged, SHA256: sha, Size: size, Version: "0.2.2",
		JournalDir: dir, Access: platform.AccessCurrentUser, Logger: discard(),
	})
	if outcome != Failed || err == nil || controller.stops != 0 || readFile(t, exe) != "old binary" {
		t.Fatalf("a changed staged binary must be refused before the service is touched: %v %v", outcome, err)
	}
}

func TestInstallLeavesTheServiceRunningWhenItCannotStop(t *testing.T) {
	dir := t.TempDir()
	exe := filepath.Join(dir, "fleeto-agent.exe")
	writeFile(t, exe, "old binary")
	staged := filepath.Join(dir, "staged")
	sha, size := writeFile(t, staged, "new binary")
	controller := &fakeController{state: svcctl.StateRunning, stopErr: errors.New("access denied")}

	outcome, _ := Install(context.Background(), InstallRequest{
		Controller: controller, Service: "fleeto-agent", Exe: exe, Staged: staged, SHA256: sha, Size: size, Version: "0.2.2",
		JournalDir: dir, Access: platform.AccessCurrentUser, Logger: discard(),
	})
	if outcome != Failed || readFile(t, exe) != "old binary" {
		t.Fatalf("expected a failure without changes, got %v", outcome)
	}
	if _, err := os.Stat(exe + ".new"); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the new copy was left behind")
	}
}

func TestRecoverRestoresThePreviousBinaryAfterACrash(t *testing.T) {
	dir := t.TempDir()
	exe := filepath.Join(dir, "fleeto-agent.exe")
	writeFile(t, exe+".previous", "old binary")
	data, _ := json.Marshal(Journal{Service: "fleeto-agent", Exe: exe, Previous: exe + ".previous", Version: "0.2.2"})
	if err := os.WriteFile(filepath.Join(dir, JournalFileName), data, 0o600); err != nil {
		t.Fatal(err)
	}

	Recover(dir, discard())
	if readFile(t, exe) != "old binary" {
		t.Fatal("the previous binary was not restored")
	}
	if _, err := os.Stat(filepath.Join(dir, JournalFileName)); !errors.Is(err, os.ErrNotExist) {
		t.Fatal("the journal was not removed")
	}
}

func TestDownloadChecksSizeAndHash(t *testing.T) {
	content := "binary content"
	sum := sha256.Sum256([]byte(content))
	var status = http.StatusOK
	var body = content
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/releases/0.2.2/windows-amd64/fleeto-agent.exe" {
			http.NotFound(w, r)
			return
		}
		if status != http.StatusOK {
			w.Header().Set("Retry-After", "120")
			w.WriteHeader(status)
			return
		}
		_, _ = io.WriteString(w, body)
	}))
	defer server.Close()
	host := strings.TrimPrefix(server.URL, "https://")
	b := release.Binary{Component: "agent", Platform: "windows", Architecture: "amd64", File: "windows-amd64/fleeto-agent.exe",
		SHA256: hex.EncodeToString(sum[:]), Size: int64(len(content))}
	dest := filepath.Join(t.TempDir(), "staged.exe")

	if err := Download(context.Background(), server.Client(), host, "0.2.2", b, dest, platform.AccessCurrentUser); err != nil {
		t.Fatalf("a correct download failed: %v", err)
	}
	if readFile(t, dest) != content {
		t.Fatal("the downloaded content differs")
	}

	body = "binary contenX"
	if err := Download(context.Background(), server.Client(), host, "0.2.2", b, dest+"2", platform.AccessCurrentUser); err == nil {
		t.Fatal("a download with the wrong hash was accepted")
	}
	body = content + " and more"
	if err := Download(context.Background(), server.Client(), host, "0.2.2", b, dest+"3", platform.AccessCurrentUser); err == nil {
		t.Fatal("a download with the wrong size was accepted")
	}
	for _, path := range []string{dest + "2", dest + "3", dest + "2.part", dest + "3.part"} {
		if _, err := os.Stat(path); !errors.Is(err, os.ErrNotExist) {
			t.Fatalf("a refused download left %s behind", filepath.Base(path))
		}
	}
	status = http.StatusServiceUnavailable
	var later *RetryLaterError
	if err := Download(context.Background(), server.Client(), host, "0.2.2", b, dest+"4", platform.AccessCurrentUser); !errors.As(err, &later) || later.After != 2*time.Minute {
		t.Fatalf("a busy gateway must mean retry later with its Retry-After, got %v", err)
	}
}

func TestWaitHealthyDoesNotCountTimeWithoutAConnection(t *testing.T) {
	dir := t.TempDir()
	now := time.Date(2026, 9, 15, 12, 0, 0, 0, time.UTC)
	connected := false
	w := HealthWait{
		Dir: dir, Version: "0.2.2", Since: now, Timeout: time.Minute, MaxTimeout: 10 * time.Minute, Interval: 10 * time.Second,
		InstallerConnected: func() bool { return connected },
		Now:                func() time.Time { return now },
		Sleep:              func(d time.Duration) { now = now.Add(d) },
	}
	if err := WaitHealthy(w, nil); !errors.Is(err, ErrNotHealthy) {
		t.Fatalf("expected not healthy after the maximum wait, got %v", err)
	}
	if elapsed := now.Sub(w.Since); elapsed < 10*time.Minute {
		t.Fatalf("time without an installer connection counted against the new version: gave up after %s", elapsed)
	}

	now = time.Date(2026, 9, 15, 13, 0, 0, 0, time.UTC)
	w.Since = now
	connected = true
	if err := WriteHealth(dir, platform.AccessCurrentUser, Health{Version: "0.2.2", Connected: true, ConnectedAt: now.Add(-time.Hour)}); err != nil {
		t.Fatal(err)
	}
	if err := WaitHealthy(w, nil); !errors.Is(err, ErrNotHealthy) {
		t.Fatal("a connection from before the start of the new version must not count")
	}
	if err := WriteHealth(dir, platform.AccessCurrentUser, Health{Version: "0.2.2", Connected: true, ConnectedAt: now.Add(time.Second)}); err != nil {
		t.Fatal(err)
	}
	if err := WaitHealthy(w, nil); err != nil {
		t.Fatalf("a connected new version is healthy: %v", err)
	}
}

func TestSupervisorStartsAStoppedServiceButRespectsDisabled(t *testing.T) {
	now := time.Date(2026, 9, 15, 12, 0, 0, 0, time.UTC)
	controller := &fakeController{state: svcctl.StateStopped}
	s := &Supervisor{Controller: controller, Service: "fleeto-agent", Exe: "x", Logger: discard(), Now: func() time.Time { return now },
		Probe: func(context.Context, string) (string, error) { return "0.2.1", nil }}

	status := s.Check(context.Background())
	if controller.starts != 1 || status.GetVersion() != "0.2.1" {
		t.Fatalf("a stopped service must be started (starts %d, version %q)", controller.starts, status.GetVersion())
	}

	controller.state = svcctl.StateDisabled
	now = now.Add(time.Hour)
	if s.Check(context.Background()).GetState() != agentv1.ServiceState_SERVICE_STATE_DISABLED || controller.starts != 1 {
		t.Fatal("a disabled service must be reported and never started")
	}

	controller.state = svcctl.StateStopped
	s.Pause(true)
	now = now.Add(time.Hour)
	s.Check(context.Background())
	if controller.starts != 1 {
		t.Fatal("a paused supervisor must not start the service")
	}

	s.Pause(false)
	controller.startErr = errors.New("the service failed to start")
	now = now.Add(time.Hour)
	status = s.Check(context.Background())
	if status.GetDetail() == "" {
		t.Fatal("the start error must be reported")
	}
	s.Check(context.Background())
	if controller.starts != 2 {
		// The retry waits for the backoff, so the immediate second check does not start again.
		t.Fatalf("expected the retry to back off, starts %d", controller.starts)
	}
}

func TestUpdaterInstallsAVerifiedNewerReleaseAndSkipsARolledBackOne(t *testing.T) {
	self, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	binary, err := os.ReadFile(self)
	if err != nil {
		t.Fatal(err)
	}
	t.Setenv("FLEETO_PROBE_VERSION", "0.2.2")
	sum := sha256.Sum256(binary)
	file := release.ExpectedFile("agent", runtime.GOOS, runtime.GOARCH)
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if r.URL.Path != "/v1/releases/0.2.2/"+file {
			http.NotFound(w, r)
			return
		}
		_, _ = w.Write(binary)
	}))
	defer server.Close()

	pub, priv, _ := ed25519.GenerateKey(rand.Reader)
	manifest := []byte(fmt.Sprintf(`{"formatVersion":1,"version":"0.2.2","agentBinaries":[{"component":"agent","platform":%q,"architecture":%q,"file":%q,"sha256":%q,"size":%d}]}`,
		runtime.GOOS, runtime.GOARCH, file, hex.EncodeToString(sum[:]), len(binary)))
	signature := ed25519.Sign(priv, manifest)

	dir := t.TempDir()
	healthDir := filepath.Join(dir, "agent-state")
	_ = os.MkdirAll(healthDir, 0o700)
	exe := filepath.Join(dir, "installed.exe")
	writeFile(t, exe, "old binary")
	installed := "0.2.1"
	var reports []Status
	healthy := true
	controller := &fakeController{state: svcctl.StateRunning}
	controller.onStart = func() {
		if healthy {
			_ = WriteHealth(healthDir, platform.AccessCurrentUser, Health{Version: "0.2.2", Connected: true, ConnectedAt: time.Now().Add(time.Second)})
		}
	}
	u := NewUpdater(UpdaterOptions{
		Target: Target{Component: "agent", Service: "fleeto-agent", Exe: exe, HealthDir: healthDir}, Keys: []ed25519.PublicKey{pub},
		StateDir: dir, Access: platform.AccessCurrentUser, Controller: controller, Logger: discard(), MaxDelay: -1,
		Client: func() (*http.Client, string, error) {
			return server.Client(), strings.TrimPrefix(server.URL, "https://"), nil
		},
		Report:           func(s Status) { reports = append(reports, s) },
		Connected:        func() bool { return true },
		InstalledVersion: func(context.Context) (string, error) { return installed, nil },
		HealthTimeout:    2 * time.Second, HealthMaxTimeout: 3 * time.Second,
	})

	// Not allowed by the ring: nothing happens.
	u.Offer(manifest, signature, false)
	u.Evaluate(context.Background())
	if len(reports) != 0 {
		t.Fatalf("an update that the ring does not allow was started: %+v", reports)
	}

	u.Offer(manifest, signature, true)
	u.Evaluate(context.Background())
	if len(reports) == 0 || reports[len(reports)-1].State != agentv1.UpdateState_UPDATE_STATE_INSTALLED {
		t.Fatalf("expected an installed report, got %+v", reports)
	}
	if !strings.EqualFold(readFile(t, exe), string(binary)) {
		t.Fatal("the new binary is not installed")
	}

	// A signature from another key is ignored.
	_, otherPriv, _ := ed25519.GenerateKey(rand.Reader)
	reports = nil
	u.Offer(manifest, ed25519.Sign(otherPriv, manifest), true)
	u.Evaluate(context.Background())
	if len(reports) != 0 {
		t.Fatal("an offer with an untrusted signature was acted on")
	}

	// A version that does not come up is rolled back and not tried again.
	writeFile(t, exe, "old binary")
	_ = os.Remove(filepath.Join(healthDir, HealthFileName))
	healthy = false
	reports = nil
	u.Offer(manifest, signature, true)
	u.Evaluate(context.Background())
	if len(reports) == 0 || reports[len(reports)-1].State != agentv1.UpdateState_UPDATE_STATE_ROLLED_BACK || readFile(t, exe) != "old binary" {
		t.Fatalf("expected a rollback, got %+v", reports)
	}
	reports = nil
	u.Evaluate(context.Background())
	if len(reports) != 0 {
		t.Fatal("a rolled back version was tried again")
	}

	// Never a downgrade or reinstall of the same version.
	u2 := NewUpdater(UpdaterOptions{
		Target: Target{Component: "agent", Service: "fleeto-agent", Exe: exe, HealthDir: healthDir}, Keys: []ed25519.PublicKey{pub},
		StateDir: t.TempDir(), Access: platform.AccessCurrentUser, Controller: controller, Logger: discard(), MaxDelay: -1,
		Client: func() (*http.Client, string, error) {
			return server.Client(), strings.TrimPrefix(server.URL, "https://"), nil
		},
		Report:           func(s Status) { reports = append(reports, s) },
		InstalledVersion: func(context.Context) (string, error) { return "0.2.2", nil },
	})
	u2.Offer(manifest, signature, true)
	u2.Evaluate(context.Background())
	if len(reports) != 0 {
		t.Fatal("the installed version was reinstalled")
	}
}
