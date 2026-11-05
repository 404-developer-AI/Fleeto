package jobs

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/patch"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

// fakePatch replaces the patch package and the restart with notice for one test.
type fakePatch struct {
	mu       sync.Mutex
	result   patch.InstallResult
	block    chan struct{}
	calls    atomic.Int32
	ended    atomic.Int32
	after    atomic.Int32
	restarts []string
	fail     error
	opts     patch.InstallOptions
}

func useFakePatch(t *testing.T, engine string) *fakePatch {
	t.Helper()
	fake := &fakePatch{}
	oldInstall, oldEngine, oldRestart, oldMin := patchInstall, patchEngine, requestRestartAfter, minTimeout
	patchEngine = func() string { return engine }
	patchInstall = func(_ context.Context, job *agentv1.PatchInstallJob, opts patch.InstallOptions, tool, lines io.Writer, _ *slog.Logger) patch.InstallResult {
		fake.calls.Add(1)
		defer fake.ended.Add(1)
		fake.mu.Lock()
		fake.opts = opts
		fake.mu.Unlock()
		_, _ = io.WriteString(tool, "Reading package lists...\nSetting up libssl3 (3.0.2-0ubuntu1.12) ...")
		if fake.block != nil {
			<-fake.block
			// Output after the job ended is dropped.
			_, _ = io.WriteString(tool, "\nlate output of the package manager\n")
		}
		_, _ = io.WriteString(lines, "\n"+patch.Line(patch.StatusInstalled, "libssl3:amd64", "libssl3"))
		fake.mu.Lock()
		defer fake.mu.Unlock()
		return fake.result
	}
	requestRestartAfter = func(_ context.Context, delay time.Duration, message string) error {
		fake.mu.Lock()
		defer fake.mu.Unlock()
		fake.restarts = append(fake.restarts, delay.String()+"|"+message)
		return fake.fail
	}
	minTimeout = time.Second
	t.Cleanup(func() {
		patchInstall, patchEngine, requestRestartAfter, minTimeout = oldInstall, oldEngine, oldRestart, oldMin
	})
	return fake
}

func (f *fixture) patchManager(dir string, fake *fakePatch, scanning bool, drivers ...bool) *Manager {
	m, err := NewManager(Options{
		Dir: dir, Access: platform.AccessCurrentUser, Logger: logging.Discard(),
		Trust: func() signedconfig.Trust { return f.trust }, Managed: func() bool { return true }, FlushInterval: 50 * time.Millisecond,
		PatchScanning: func() bool { return scanning }, AfterPatchInstall: func() { fake.after.Add(1) },
		PatchIncludeDrivers: func() bool { return len(drivers) > 0 && drivers[0] },
	})
	if err != nil {
		f.t.Fatal(err)
	}
	f.t.Cleanup(m.Close)
	return m
}

func (f *fixture) patchPayload(id string, timeoutSeconds uint32, restart agentv1.PatchRestart) *agentv1.JobPayload {
	return &agentv1.JobPayload{
		JobId: id, InstanceId: testInstance, EndpointId: testEndpoint, Type: agentv1.JobType_JOB_TYPE_PATCH_INSTALL,
		ValidUntil: timestamppb.New(time.Now().Add(time.Hour)), InitiatedBy: "Tess", TimeoutSeconds: timeoutSeconds, MaxOutputBytes: 64 * 1024,
		PatchInstall: &agentv1.PatchInstallJob{
			Scope:   agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED,
			Updates: []*agentv1.PatchInstallUpdate{{Id: "libssl3:amd64", Title: "libssl3", Version: "3.0.2-0ubuntu1.12"}},
			Restart: restart, RestartNoticeSeconds: 300,
		},
	}
}

func outputOf(messages []Message, id string) string {
	var b strings.Builder
	for _, message := range messages {
		if o := message.Msg.GetJobOutput(); o != nil && o.GetJobId() == id {
			b.Write(o.GetData())
		}
	}
	return b.String()
}

func waitFor(t *testing.T, what string, ok func() bool) {
	t.Helper()
	deadline := time.Now().Add(10 * time.Second)
	for !ok() {
		if time.Now().After(deadline) {
			t.Fatalf("timed out waiting for %s", what)
		}
		time.Sleep(10 * time.Millisecond)
	}
}

func TestVerifyAcceptsAPatchInstallationOnlyWhenPatchManagementIsOn(t *testing.T) {
	useFakePatch(t, patch.EngineApt)
	f := newFixture(t)
	now := time.Now()
	valid := f.patchPayload("41111111-1111-1111-1111-111111111111", 3600, agentv1.PatchRestart_PATCH_RESTART_NEVER)
	on := Applied{Managed: true, PatchScanning: true}
	if _, err := VerifyApplied(f.sign(valid), f.trust, on, now); err != nil {
		t.Fatalf("a valid patch installation must verify: %v", err)
	}

	refusals := map[string]struct {
		change  func(p *agentv1.JobPayload)
		applied Applied
		reason  string
	}{
		"agent-only":             {func(*agentv1.JobPayload) {}, Applied{PatchScanning: true}, "agent-only"},
		"no patch scan interval": {func(*agentv1.JobPayload) {}, Applied{Managed: true}, "Patch management by Fleeto is not on for this endpoint"},
		"without its body":       {func(p *agentv1.JobPayload) { p.PatchInstall = nil }, on, "malformed"},
		"with a script":          {func(p *agentv1.JobPayload) { p.Script = &agentv1.ScriptJob{Body: "reboot"} }, on, "malformed"},
		"with a restart body":    {func(p *agentv1.JobPayload) { p.Restart = &agentv1.RestartJob{} }, on, "malformed"},
		"a bad id":               {func(p *agentv1.JobPayload) { p.PatchInstall.Updates[0].Id = "libssl3;reboot:amd64" }, on, "update id"},
		"a bad version":          {func(p *agentv1.JobPayload) { p.PatchInstall.Updates[0].Version = "$(reboot)" }, on, "version"},
		"a notice that is too short": {func(p *agentv1.JobPayload) {
			p.PatchInstall.Restart = agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED
			p.PatchInstall.RestartNoticeSeconds = 10
		}, on, "restart notice"},
		"too many updates": {func(p *agentv1.JobPayload) {
			for range 100 {
				p.PatchInstall.Updates = append(p.PatchInstall.Updates, p.PatchInstall.Updates[0])
			}
		}, on, "1 to 100"},
	}
	for name, c := range refusals {
		payload := proto.Clone(valid).(*agentv1.JobPayload)
		c.change(payload)
		_, err := VerifyApplied(f.sign(payload), f.trust, c.applied, now)
		if err == nil || !strings.Contains(err.Error(), c.reason) {
			t.Errorf("%s must be refused with %q, got %v", name, c.reason, err)
		}
	}
	if _, err := Verify(f.sign(valid), f.trust, true, now); err == nil {
		t.Error("Verify without the applied configuration refuses a patch installation")
	}
	script := f.payload("41111111-1111-1111-1111-111111111112", agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL, "true")
	script.PatchInstall = valid.PatchInstall
	if _, err := VerifyApplied(f.sign(script), f.trust, on, now); err == nil {
		t.Error("a script job with a patch installation body must be refused")
	}

	patchEngine = func() string { return "" }
	if _, err := VerifyApplied(f.sign(valid), f.trust, on, now); err == nil {
		t.Error("without a package manager a patch installation must be refused")
	}
	patchEngine = func() string { return patch.EngineWindowsUpdate }
	if _, err := VerifyApplied(f.sign(valid), f.trust, on, now); err == nil {
		t.Error("a Linux package id must be refused on Windows")
	}
}

func TestARefusedPatchJobRunsNothing(t *testing.T) {
	fake := useFakePatch(t, patch.EngineApt)
	f := newFixture(t)
	id := "42222222-2222-2222-2222-222222222221"
	m := f.patchManager(t.TempDir(), fake, false)
	m.Accept(f.sign(f.patchPayload(id, 3600, agentv1.PatchRestart_PATCH_RESTART_NEVER)))
	completion, _ := completionOf(t, m, id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_REFUSED || !strings.Contains(completion.GetError(), "Patch management by Fleeto is not on") {
		t.Fatalf("got %v: %s", completion.GetResult(), completion.GetError())
	}
	if fake.calls.Load() != 0 {
		t.Fatal("a refused job must not install anything")
	}
}

func TestAPatchJobReportsItsLinesAndTheRestartState(t *testing.T) {
	cases := []struct {
		name     string
		result   patch.InstallResult
		restart  agentv1.PatchRestart
		wantLine string
	}{
		{"nothing to restart", patch.InstallResult{Installed: 1}, agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED, "fleeto-patch: restart not-required\n"},
		{"never restart", patch.InstallResult{Installed: 1, RestartRequired: true}, agentv1.PatchRestart_PATCH_RESTART_NEVER, "fleeto-patch: restart required\n"},
		{"nothing installed by this job", patch.InstallResult{RestartRequired: true}, agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED, "fleeto-patch: restart required\n"},
		{"a failure", patch.InstallResult{ExitCode: 1, Error: "apt-get ended with exit code 100."}, agentv1.PatchRestart_PATCH_RESTART_NEVER, "fleeto-patch: restart not-required\n"},
	}
	for i, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			useFakeRestart(t, time.Now().Add(-time.Hour))
			fake := useFakePatch(t, patch.EngineApt)
			fake.result = c.result
			f := newFixture(t)
			id := "43333333-3333-3333-3333-33333333333" + string(rune('0'+i))
			m := f.patchManager(t.TempDir(), fake, true)
			m.Accept(f.sign(f.patchPayload(id, 3600, c.restart)))
			completion, messages := completionOf(t, m, id, 5*time.Second)
			if completion.GetResult() != agentv1.JobResult_JOB_RESULT_EXITED || completion.GetExitCode() != c.result.ExitCode ||
				completion.GetError() != c.result.Error {
				t.Fatalf("got %v %d: %s", completion.GetResult(), completion.GetExitCode(), completion.GetError())
			}
			out := outputOf(messages, id)
			if !strings.HasSuffix(out, "fleeto-patch: installed libssl3:amd64 libssl3\n"+c.wantLine) || !strings.HasPrefix(out, "Reading package lists...") {
				t.Fatalf("output %q", out)
			}
			sum := sha256.Sum256([]byte(out))
			if completion.GetStdout().GetSha256() != hex.EncodeToString(sum[:]) || completion.GetStdout().GetBytes() != uint64(len(out)) {
				t.Fatal("the summary must cover the whole output")
			}
			if len(fake.restarts) != 0 {
				t.Fatal("no restart was asked for")
			}
			waitFor(t, "the scan after the installation", func() bool { return fake.after.Load() == 1 })
		})
	}
}

func TestATimedOutInstallationIsNeverStoppedAndBlocksTheNextUntilItEnds(t *testing.T) {
	fake := useFakePatch(t, patch.EngineApt)
	fake.block = make(chan struct{})
	f := newFixture(t)
	dir := t.TempDir()
	m := f.patchManager(dir, fake, true)
	first, second, third := "44444444-4444-4444-4444-444444444441", "44444444-4444-4444-4444-444444444442", "44444444-4444-4444-4444-444444444443"

	m.Accept(f.sign(f.patchPayload(first, 1, agentv1.PatchRestart_PATCH_RESTART_NEVER)))
	completion, messages := completionOf(t, m, first, 10*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_TIMED_OUT || completion.GetError() != errPatchTimedOut {
		t.Fatalf("got %v: %s", completion.GetResult(), completion.GetError())
	}
	if fake.ended.Load() != 0 || !patch.InstallRunning() {
		t.Fatal("the installation must keep running after the timeout")
	}
	if out := outputOf(messages, first); !strings.Contains(out, "Setting up libssl3") {
		t.Fatalf("the output until the timeout is kept, got %q", out)
	}

	// The slot is free, but a new installation is refused while the earlier one runs.
	m.Accept(f.sign(f.patchPayload(second, 3600, agentv1.PatchRestart_PATCH_RESTART_NEVER)))
	completion, _ = completionOf(t, m, second, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_REFUSED || completion.GetError() != errPatchRunning {
		t.Fatalf("got %v: %s", completion.GetResult(), completion.GetError())
	}

	close(fake.block)
	waitFor(t, "the installation to end and a scan", func() bool { return fake.after.Load() == 1 && !patch.InstallRunning() })
	time.Sleep(100 * time.Millisecond)
	for _, message := range m.Pending(func(string) bool { return false }) {
		if o := message.Msg.GetJobOutput(); o != nil && strings.Contains(string(o.GetData()), "late output") {
			t.Fatal("output after the job was answered must be dropped")
		}
	}

	m.Accept(f.sign(f.patchPayload(third, 3600, agentv1.PatchRestart_PATCH_RESTART_NEVER)))
	completion, _ = completionOf(t, m, third, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_EXITED {
		t.Fatalf("after the earlier installation ended a new one runs, got %v: %s", completion.GetResult(), completion.GetError())
	}
}

func TestAPatchJobThatNeedsARestartCompletesAfterTheEndpointStartedAgain(t *testing.T) {
	boot := time.Now().Add(-time.Hour).Truncate(time.Second)
	restart := useFakeRestart(t, boot)
	fake := useFakePatch(t, patch.EngineApt)
	fake.result = patch.InstallResult{ExitCode: 1, Installed: 1, RestartRequired: true, Error: "dnf ended with exit code 1."}
	f := newFixture(t)
	dir := t.TempDir()
	id := "45555555-5555-5555-5555-555555555555"

	m := f.patchManager(dir, fake, true)
	payload := f.patchPayload(id, 3600, agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED)
	payload.PatchInstall.RestartMessage = "-h now\nSave your work"
	m.Accept(f.sign(payload))
	waitFor(t, "the restart", func() bool { fake.mu.Lock(); defer fake.mu.Unlock(); return len(fake.restarts) == 1 })
	if got := fake.restarts[0]; got != "5m0s|h now Save your work" {
		t.Fatalf("restart %q: the notice of the job, sanitized", got)
	}
	if restart.requested.Load() != 0 {
		t.Fatal("a patch restart uses the restart with a notice, not the one of a task")
	}
	record, err := os.ReadFile(filepath.Join(dir, id, restartFile))
	if err != nil || !strings.Contains(string(record), `"patch"`) {
		t.Fatalf("the restart is recorded next to the job: %s %v", record, err)
	}
	for _, message := range m.Pending(func(string) bool { return false }) {
		if message.Msg.GetJobCompletion() != nil {
			t.Fatal("the job must not complete before the endpoint started again")
		}
	}
	m.Close()

	restart.boot.Store(boot.Add(2 * time.Hour).Unix())
	completion, messages := completionOf(t, f.patchManager(dir, fake, true), id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_EXITED || completion.GetExitCode() != 1 || completion.GetError() != fake.result.Error {
		t.Fatalf("got %v %d: %s", completion.GetResult(), completion.GetExitCode(), completion.GetError())
	}
	out := outputOf(messages, id)
	if !strings.Contains(out, "fleeto-patch: installed libssl3:amd64 libssl3\n") || !strings.Contains(out, "fleeto-patch: restart restarting\n") ||
		!strings.HasSuffix(strings.TrimSpace(out), "UTC.") && !strings.Contains(out, "The endpoint restarted at") {
		t.Fatalf("output %q", out)
	}
	sum := sha256.Sum256([]byte(out))
	if completion.GetStdout().GetSha256() != hex.EncodeToString(sum[:]) || completion.GetStdout().GetBytes() != uint64(len(out)) ||
		completion.GetStdout().GetChunks() < 2 {
		t.Fatalf("the summary must cover the output from before and after the restart: %+v, %d bytes", completion.GetStdout(), len(out))
	}
	waitFor(t, "a scan after the restart", func() bool { return fake.after.Load() >= 2 })
}

func TestAPatchRestartThatTheSystemRefusesEndsWithRestartRequired(t *testing.T) {
	useFakeRestart(t, time.Now().Add(-time.Hour))
	fake := useFakePatch(t, patch.EngineApt)
	fake.result = patch.InstallResult{Installed: 1, RestartRequired: true}
	fake.fail = os.ErrPermission
	f := newFixture(t)
	dir := t.TempDir()
	id := "46666666-6666-6666-6666-666666666666"
	m := f.patchManager(dir, fake, true)
	m.Accept(f.sign(f.patchPayload(id, 3600, agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED)))
	completion, messages := completionOf(t, m, id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_EXITED || !strings.Contains(completion.GetError(), "could not be restarted") {
		t.Fatalf("got %v: %s", completion.GetResult(), completion.GetError())
	}
	if out := outputOf(messages, id); !strings.HasSuffix(out, "fleeto-patch: restart required\n") {
		t.Fatalf("the last restart line says what is true, got %q", out)
	}
	if exists(filepath.Join(dir, id, restartFile)) {
		t.Fatal("a restart that was refused is not waited for")
	}
}

func TestAPatchRestartThatDoesNotHappenTimesOut(t *testing.T) {
	boot := time.Now().Add(-time.Hour).Truncate(time.Second)
	restart := useFakeRestart(t, boot)
	fake := useFakePatch(t, patch.EngineApt)
	f := newFixture(t)
	dir := t.TempDir()
	id := "47777777-7777-7777-7777-777777777777"
	writePatchJob(t, f, dir, id)
	record := restartRecord{BootTime: boot.UTC(), RequestedAt: time.Now().Add(-2 * time.Hour), Deadline: time.Now().Add(-time.Minute),
		Patch: &patchRestart{ExitCode: 0, Stdout: emptyStreamState(t)}}
	writeRecord(t, filepath.Join(dir, id, restartFile), record)

	completion, messages := completionOf(t, f.patchManager(dir, fake, true), id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_TIMED_OUT || !strings.Contains(completion.GetError(), "still needs a restart") {
		t.Fatalf("got %v: %s", completion.GetResult(), completion.GetError())
	}
	if out := outputOf(messages, id); out != "fleeto-patch: restart required\n" {
		t.Fatalf("output %q", out)
	}
	if restart.cancelled.Load() != 0 || fake.after.Load() != 1 {
		t.Fatalf("cancelled %d, scans %d", restart.cancelled.Load(), fake.after.Load())
	}
}

func TestAPatchJobThatRanWhenTheAgentStoppedIsInterruptedAndScanned(t *testing.T) {
	fake := useFakePatch(t, patch.EngineApt)
	f := newFixture(t)
	dir := t.TempDir()
	id := "48888888-8888-8888-8888-888888888888"
	writePatchJob(t, f, dir, id)
	if err := os.WriteFile(filepath.Join(dir, id, startedFile), []byte(time.Now().UTC().Format(time.RFC3339Nano)), 0o600); err != nil {
		t.Fatal(err)
	}
	completion, _ := completionOf(t, f.patchManager(dir, fake, true), id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_INTERRUPTED || completion.GetError() != errPatchInterrupted {
		t.Fatalf("got %v: %s", completion.GetResult(), completion.GetError())
	}
	if fake.calls.Load() != 0 || fake.after.Load() != 1 {
		t.Fatalf("an interrupted installation is never run again (%d) and a scan follows (%d)", fake.calls.Load(), fake.after.Load())
	}
}

func TestTheRestartNotice(t *testing.T) {
	cases := map[string]string{
		patchRestartNotice("", 300):                         "Updates were installed and this computer restarts in 5 minutes to finish them. Save your work.",
		patchRestartNotice("", 60):                          "Updates were installed and this computer restarts in 1 minute to finish them. Save your work.",
		patchRestartNotice("", 61):                          "Updates were installed and this computer restarts in 2 minutes to finish them. Save your work.",
		patchRestartNotice(" \n", 60):                       "Updates were installed and this computer restarts in 1 minute to finish them. Save your work.",
		patchRestartNotice("--Save\x07 your\r\n work ", 60): "Save your work",
	}
	for got, want := range cases {
		if got != want {
			t.Errorf("got %q, want %q", got, want)
		}
	}
	long := patchRestartNotice(strings.Repeat("é", 400), 60)
	if len(long) > maxNoticeLen || !strings.HasPrefix(long, "é") || strings.ContainsRune(long, '�') {
		t.Fatalf("a long notice is cut on a whole character to %d bytes, got %d", maxNoticeLen, len(long))
	}
	if noticeMinutes(86400) != 1440 || noticeMinutes(90) != 2 {
		t.Fatal("minutes round up")
	}
}

// writePatchJob stores a signed patch job as Accept does, without running it.
func writePatchJob(t *testing.T, f *fixture, dir, id string) {
	t.Helper()
	data, err := proto.Marshal(f.sign(f.patchPayload(id, 3600, agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED)))
	if err != nil {
		t.Fatal(err)
	}
	if err := os.MkdirAll(filepath.Join(dir, id), 0o700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(dir, id, jobFile), data, 0o600); err != nil {
		t.Fatal(err)
	}
}

func emptyStreamState(t *testing.T) streamState {
	t.Helper()
	w := &streamWriter{hash: sha256.New()}
	s, err := w.state()
	if err != nil {
		t.Fatal(err)
	}
	return s
}

func TestAPatchJobPassesItsIDDirectoryAndTheDriversOfTheConfiguration(t *testing.T) {
	useFakeRestart(t, time.Now().Add(-time.Hour))
	fake := useFakePatch(t, patch.EngineWindowsUpdate)
	f := newFixture(t)
	id := "45555555-5555-5555-5555-555555555555"
	m := f.patchManager(t.TempDir(), fake, true, true)
	payload := f.patchPayload(id, 3600, agentv1.PatchRestart_PATCH_RESTART_NEVER)
	payload.PatchInstall.Scope = agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING
	payload.PatchInstall.Updates = nil
	m.Accept(f.sign(payload))
	completionOf(t, m, id, 5*time.Second)
	fake.mu.Lock()
	defer fake.mu.Unlock()
	if fake.opts.JobID != id || !fake.opts.Drivers || fake.opts.OutputDir != m.dir(id) {
		t.Fatalf("options %+v", fake.opts)
	}
}
