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
	"math"
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

	// Not allowed by the ring: only the wait is reported.
	u.Offer(manifest, signature, false)
	u.Evaluate(context.Background())
	if len(reports) != 1 || reports[0].State != agentv1.UpdateState_UPDATE_STATE_WAITING {
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
	if len(reports) != 1 || reports[0].WaitReason != agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_ROLLED_BACK {
		t.Fatalf("a rolled back version was tried again: %+v", reports)
	}
	reports = nil

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

func TestUpdaterLogsOnceWhyAnOfferedReleaseWaits(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(rand.Reader)
	file := release.ExpectedFile("agent", runtime.GOOS, runtime.GOARCH)
	manifest := []byte(fmt.Sprintf(`{"formatVersion":1,"version":"0.2.2","agentBinaries":[{"component":"agent","platform":%q,"architecture":%q,"file":%q,"sha256":%q,"size":1}]}`,
		runtime.GOOS, runtime.GOARCH, file, strings.Repeat("0", 64)))
	signature := ed25519.Sign(priv, manifest)

	var logs strings.Builder
	now := time.Date(2026, 9, 16, 12, 0, 0, 0, time.UTC)
	ready := true
	installed := "0.2.1"
	u := NewUpdater(UpdaterOptions{
		Target: Target{Component: "agent", Service: "fleeto-agent"}, Keys: []ed25519.PublicKey{pub}, StateDir: t.TempDir(),
		Access: platform.AccessCurrentUser, Controller: &fakeController{state: svcctl.StateRunning},
		Logger: slog.New(slog.NewTextHandler(&logs, nil)), MaxDelay: time.Hour, Now: func() time.Time { return now },
		Client:           func() (*http.Client, string, error) { return nil, "", errors.New("no download in this test") },
		InstalledVersion: func(context.Context) (string, error) { return installed, nil },
		Ready:            func(*release.Manifest) bool { return ready },
	})
	count := func(text string) int { return strings.Count(logs.String(), text) }
	var reports []Status
	u.opts.Report = func(s Status) { reports = append(reports, s) }
	// expect checks that the evaluations reported exactly one wait since the previous check, with this reason and duration.
	seen := 0
	expect := func(reason agentv1.UpdateWaitReason, seconds uint32) {
		t.Helper()
		if len(reports) != seen+1 {
			t.Fatalf("expected one new report, got %+v", reports[seen:])
		}
		msg := reports[seen].Message()
		seen = len(reports)
		if msg.GetState() != agentv1.UpdateState_UPDATE_STATE_WAITING || msg.GetWaitReason() != reason || msg.GetWaitSeconds() != seconds ||
			msg.GetVersion() != "0.2.2" || msg.GetComponent() != agentv1.Component_COMPONENT_AGENT {
			t.Fatalf("expected a %v wait of %d seconds, got %+v", reason, seconds, msg)
		}
	}

	u.Offer(manifest, signature, false)
	u.Evaluate(context.Background())
	u.Evaluate(context.Background())
	if count("waiting for the update ring") != 1 {
		t.Fatalf("expected the ring wait logged once:\n%s", logs.String())
	}
	expect(agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_UPDATE_RING, 0)

	u.memory.RetryAt("agent", now.Add(time.Hour))
	u.Offer(manifest, signature, true)
	u.Evaluate(context.Background())
	u.Evaluate(context.Background())
	if count("waiting for the next attempt") != 1 || count("nextAttempt=2026-09-16T13:00:00Z") != 1 {
		t.Fatalf("expected the retry wait logged once with its time:\n%s", logs.String())
	}
	expect(agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_NEXT_ATTEMPT, 3600)
	// A new failure moves the next attempt: logged and reported again.
	u.memory.RetryAt("agent", now.Add(2*time.Hour))
	u.Evaluate(context.Background())
	if count("nextAttempt=2026-09-16T14:00:00Z") != 1 {
		t.Fatalf("expected the moved retry wait logged:\n%s", logs.String())
	}
	expect(agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_NEXT_ATTEMPT, 7200)

	u.memory.Clear("agent")
	ready = false
	u.Evaluate(context.Background())
	if count("waiting until this service runs the release itself") != 1 {
		t.Fatalf("expected the readiness wait logged:\n%s", logs.String())
	}
	expect(agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_INSTALLER_UPDATE, 0)

	u.memory.RecordRollback("agent", "0.2.2")
	u.Evaluate(context.Background())
	u.Evaluate(context.Background())
	if count("rolled back before") != 1 {
		t.Fatalf("expected the rollback logged once:\n%s", logs.String())
	}
	expect(agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_ROLLED_BACK, 0)

	// An installed release is no wait; the random delay is reported with its end but logged as before.
	before := count("not installing it yet")
	installed = "0.2.2"
	u.Evaluate(context.Background())
	if len(reports) != seen {
		t.Fatalf("an installed release was reported as waiting: %+v", reports[seen:])
	}
	installed = "0.2.0"
	u.memory = OpenMemory(t.TempDir(), platform.AccessCurrentUser)
	ready = true
	u.notBefore["0.2.2"] = now.Add(90 * time.Second)
	u.Evaluate(context.Background())
	if count("not installing it yet") != before || len(reports) != seen {
		t.Fatalf("expected no wait logged or reported for a delay planned before:\n%s\n%+v", logs.String(), reports[seen:])
	}
	delete(u.notBefore, "0.2.2")
	u.Evaluate(context.Background())
	u.Evaluate(context.Background())
	if count("not installing it yet") != before || count("installing after a random delay") != 1 {
		t.Fatalf("expected the random delay logged once:\n%s", logs.String())
	}
	if len(reports) != seen+1 {
		t.Fatalf("expected one random delay report, got %+v", reports[seen:])
	}
	if msg := reports[seen].Message(); msg.GetWaitReason() != agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_RANDOM_DELAY ||
		msg.GetWaitSeconds() > 3600 || msg.GetWaitSeconds() != uint32(math.Ceil(u.notBefore["0.2.2"].Sub(now).Seconds())) {
		t.Fatalf("expected a random delay report ending at the planned time, got %+v", msg)
	}
}

func TestStatusMessageRoundsTheWaitUp(t *testing.T) {
	msg := Status{Component: release.ComponentWatchdog, Version: "0.2.2", State: agentv1.UpdateState_UPDATE_STATE_WAITING,
		WaitReason: agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_RANDOM_DELAY, WaitFor: 1500 * time.Millisecond}.Message()
	if msg.GetComponent() != agentv1.Component_COMPONENT_WATCHDOG || msg.GetWaitSeconds() != 2 {
		t.Fatalf("unexpected message %+v", msg)
	}
}

func TestDownloadMarksOnlyFailuresThatPassOnTheirOwnAsTransient(t *testing.T) {
	content := "binary content"
	sum := sha256.Sum256([]byte(content))
	status := http.StatusOK
	dropped := false
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		switch {
		case status != http.StatusOK:
			w.WriteHeader(status)
		case dropped:
			// Promise the whole binary, send half and drop the connection.
			w.Header().Set("Content-Length", fmt.Sprint(len(content)))
			_, _ = io.WriteString(w, content[:5])
			w.(http.Flusher).Flush()
			conn, _, _ := w.(http.Hijacker).Hijack()
			_ = conn.Close()
		default:
			_, _ = io.WriteString(w, "binary contenX")
		}
	}))
	host := strings.TrimPrefix(server.URL, "https://")
	b := release.Binary{Component: "agent", Platform: "windows", Architecture: "amd64", File: "windows-amd64/fleeto-agent.exe",
		SHA256: hex.EncodeToString(sum[:]), Size: int64(len(content))}
	dir := t.TempDir()
	download := func() error {
		return Download(context.Background(), server.Client(), host, "0.2.2", b, filepath.Join(dir, "staged.exe"), platform.AccessCurrentUser)
	}

	for _, code := range []int{http.StatusInternalServerError, http.StatusBadGateway, http.StatusGatewayTimeout} {
		status = code
		if err := download(); !IsTransient(err) {
			t.Fatalf("HTTP %d must be transient, got %v", code, err)
		}
	}
	for _, code := range []int{http.StatusNotFound, http.StatusForbidden} {
		status = code
		if err := download(); err == nil || IsTransient(err) {
			t.Fatalf("HTTP %d must wait an hour, got %v", code, err)
		}
	}
	status = http.StatusOK
	if err := download(); err == nil || IsTransient(err) {
		t.Fatalf("a download that does not match the manifest must wait an hour, got %v", err)
	}
	dropped = true
	if err := download(); !IsTransient(err) {
		t.Fatalf("a dropped connection must be transient, got %v", err)
	}
	server.Close()
	if err := download(); !IsTransient(err) {
		t.Fatalf("a gateway that does not answer must be transient, got %v", err)
	}
}

func TestUpdaterRetriesATransientFailureSoonWithBackoff(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(rand.Reader)
	file := release.ExpectedFile("agent", runtime.GOOS, runtime.GOARCH)
	manifest := []byte(fmt.Sprintf(`{"formatVersion":1,"version":"0.2.2","agentBinaries":[{"component":"agent","platform":%q,"architecture":%q,"file":%q,"sha256":%q,"size":1}]}`,
		runtime.GOOS, runtime.GOARCH, file, strings.Repeat("0", 64)))
	status := http.StatusBadGateway
	server := httptest.NewTLSServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) { w.WriteHeader(status) }))
	defer server.Close()

	now := time.Date(2026, 9, 16, 12, 0, 0, 0, time.UTC)
	var reports []Status
	u := NewUpdater(UpdaterOptions{
		Target: Target{Component: "agent", Service: "fleeto-agent"}, Keys: []ed25519.PublicKey{pub}, StateDir: t.TempDir(),
		Access: platform.AccessCurrentUser, Controller: &fakeController{state: svcctl.StateRunning}, Logger: discard(), MaxDelay: -1,
		Now: func() time.Time { return now },
		Client: func() (*http.Client, string, error) {
			return server.Client(), strings.TrimPrefix(server.URL, "https://"), nil
		},
		Report:           func(s Status) { reports = append(reports, s) },
		InstalledVersion: func(context.Context) (string, error) { return "0.2.1", nil },
		// Long enough that the wake-ups of the failures never fire during the test.
		TransientRetry: 10 * time.Minute, RetryAfter: time.Hour,
	})
	u.Offer(manifest, ed25519.Sign(priv, manifest), true)

	// Each transient failure in a row waits between half and all of 10, 20, 40 minutes, then at most the hour.
	for i, ceiling := range []time.Duration{10 * time.Minute, 20 * time.Minute, 40 * time.Minute, time.Hour, time.Hour} {
		u.Evaluate(context.Background())
		if len(reports) == 0 || reports[len(reports)-1].State != agentv1.UpdateState_UPDATE_STATE_FAILED {
			t.Fatalf("attempt %d: expected a failed report, got %+v", i, reports)
		}
		wait := u.memory.NextAttempt("agent").Sub(now)
		if wait < ceiling/2 || wait > ceiling {
			t.Fatalf("attempt %d: expected a wait up to %s, got %s", i, ceiling, wait)
		}
		now = u.memory.NextAttempt("agent")
	}

	// A refusal waits the hour and starts the backoff again.
	status = http.StatusNotFound
	u.Evaluate(context.Background())
	if wait := u.memory.NextAttempt("agent").Sub(now); wait != time.Hour {
		t.Fatalf("a refusal must wait an hour, got %s", wait)
	}
	now = u.memory.NextAttempt("agent")
	status = http.StatusBadGateway
	u.Evaluate(context.Background())
	if wait := u.memory.NextAttempt("agent").Sub(now); wait < 5*time.Minute || wait > 10*time.Minute {
		t.Fatalf("the backoff must start again after a refusal, got %s", wait)
	}
}
