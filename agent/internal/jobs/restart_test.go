package jobs

import (
	"context"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync/atomic"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// fakeRestart replaces the operating system for one test: the boot time it reports, and whether a restart was asked or cancelled.
type fakeRestart struct {
	boot      atomic.Int64
	requested atomic.Int32
	cancelled atomic.Int32
	fail      error
}

func useFakeRestart(t *testing.T, boot time.Time) *fakeRestart {
	t.Helper()
	if !RestartSupported() {
		t.Skip("Fleeto restarts only Windows and Linux endpoints")
	}
	fake := &fakeRestart{}
	fake.boot.Store(boot.Unix())
	oldBoot, oldRequest, oldCancel, oldMin := readBootTime, requestRestart, cancelRestart, minTimeout
	readBootTime = func() (time.Time, error) { return time.Unix(fake.boot.Load(), 0), nil }
	requestRestart = func(context.Context) error {
		fake.requested.Add(1)
		return fake.fail
	}
	cancelRestart = func() error {
		fake.cancelled.Add(1)
		return nil
	}
	minTimeout = time.Second
	t.Cleanup(func() {
		readBootTime, requestRestart, cancelRestart, minTimeout = oldBoot, oldRequest, oldCancel, oldMin
	})
	return fake
}

func (f *fixture) restartPayload(id string, timeoutSeconds uint32) *agentv1.JobPayload {
	return &agentv1.JobPayload{
		JobId: id, InstanceId: testInstance, EndpointId: testEndpoint, Type: agentv1.JobType_JOB_TYPE_RESTART,
		ValidUntil: timestamppb.New(time.Now().Add(time.Hour)), InitiatedBy: "Fleeto (task Patch round, schedule)", TimeoutSeconds: timeoutSeconds,
		MaxOutputBytes: 64 * 1024, Restart: &agentv1.RestartJob{},
	}
}

func TestVerifyAcceptsARestartOnlyWithoutAScript(t *testing.T) {
	if !RestartSupported() {
		t.Skip("Fleeto restarts only Windows and Linux endpoints")
	}
	f := newFixture(t)
	now := time.Now()
	valid := f.restartPayload("31111111-1111-1111-1111-111111111111", 1800)
	if _, err := Verify(f.sign(valid), f.trust, true, now); err != nil {
		t.Fatalf("a restart must verify: %v", err)
	}

	language, body := nativeScript(false)
	withScript := proto.Clone(valid).(*agentv1.JobPayload)
	withScript.Script = f.payload(valid.JobId, language, body).Script
	noBody := proto.Clone(valid).(*agentv1.JobPayload)
	noBody.Restart = nil
	scriptWithRestart := f.payload("31111111-1111-1111-1111-111111111112", language, body)
	scriptWithRestart.Restart = &agentv1.RestartJob{}
	unknown := proto.Clone(valid).(*agentv1.JobPayload)
	unknown.Type = agentv1.JobType(3)

	for name, payload := range map[string]*agentv1.JobPayload{
		"restart with a script": withScript, "restart without its body": noBody, "script with a restart": scriptWithRestart, "unknown type": unknown,
	} {
		if _, err := Verify(f.sign(payload), f.trust, true, now); err == nil {
			t.Errorf("%s must be refused", name)
		}
	}
	if _, err := Verify(f.sign(valid), f.trust, false, now); err == nil {
		t.Error("an agent-only endpoint must refuse a restart")
	}
}

func TestARestartCompletesOnlyAfterTheEndpointStartedAgainWithANewerBootTime(t *testing.T) {
	boot := time.Now().Add(-time.Hour).Truncate(time.Second)
	fake := useFakeRestart(t, boot)
	f := newFixture(t)
	dir := t.TempDir()
	id := "32222222-2222-2222-2222-222222222222"

	m := f.manager(dir)
	m.Accept(f.sign(f.restartPayload(id, 600)))
	deadline := time.Now().Add(5 * time.Second)
	for fake.requested.Load() == 0 && time.Now().Before(deadline) {
		time.Sleep(20 * time.Millisecond)
	}
	if fake.requested.Load() != 1 {
		t.Fatal("the agent must ask the operating system to restart")
	}
	started := false
	for _, message := range m.Pending(func(string) bool { return false }) {
		if message.Msg.GetJobCompletion() != nil {
			t.Fatal("a restart must not complete before the endpoint started again")
		}
		started = started || message.Msg.GetJobStarted() != nil
	}
	if !started {
		t.Fatal("the start of the restart must be reported before the endpoint goes down")
	}

	// The operating system stops the agent for the restart: the job stays open.
	m.Close()
	if exists(filepath.Join(dir, id, completionFile)) {
		t.Fatal("stopping the agent for the restart must not complete the job")
	}

	// The endpoint is back with a newer boot time.
	fake.boot.Store(boot.Add(2 * time.Hour).Unix())
	completion, messages := completionOf(t, f.manager(dir), id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_EXITED || completion.GetExitCode() != 0 {
		t.Fatalf("a restart that happened must succeed, got %v %d: %s", completion.GetResult(), completion.GetExitCode(), completion.GetError())
	}
	var output strings.Builder
	for _, message := range messages {
		output.Write(message.Msg.GetJobOutput().GetData())
	}
	if !strings.Contains(output.String(), "The endpoint restarted at") || completion.GetStdout().GetChunks() != 1 {
		t.Fatalf("the restart must report when the endpoint started, got %q", output.String())
	}
}

func TestARestartThatDoesNotHappenTimesOutAndIsCancelled(t *testing.T) {
	fake := useFakeRestart(t, time.Now().Add(-time.Hour))
	f := newFixture(t)
	id := "33333333-3333-3333-3333-333333333333"

	m := f.manager(t.TempDir())
	m.Accept(f.sign(f.restartPayload(id, 1)))
	completion, _ := completionOf(t, m, id, 10*time.Second)

	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_TIMED_OUT || !strings.Contains(completion.GetError(), "did not restart") {
		t.Fatalf("a restart that does not happen must time out, got %v: %s", completion.GetResult(), completion.GetError())
	}
	if fake.cancelled.Load() != 1 {
		t.Fatal("the planned restart must be cancelled when the wait ends")
	}
}

func TestAnAgentThatStartsAgainWithoutARestartKeepsWaitingUntilTheDeadline(t *testing.T) {
	boot := time.Now().Add(-time.Hour).Truncate(time.Second)
	useFakeRestart(t, boot)
	f := newFixture(t)
	dir := t.TempDir()
	id := "34444444-4444-4444-4444-444444444444"

	m := f.manager(dir)
	m.Accept(f.sign(f.restartPayload(id, 600)))
	deadline := time.Now().Add(5 * time.Second)
	for !exists(filepath.Join(dir, id, restartFile)) && time.Now().Before(deadline) {
		time.Sleep(20 * time.Millisecond)
	}
	m.Close()

	// Same boot time, the wait not over: the restarted agent waits on; with the wait over it times out.
	again := f.manager(dir)
	time.Sleep(200 * time.Millisecond)
	if exists(filepath.Join(dir, id, completionFile)) {
		t.Fatal("an agent that started again before the restart must keep waiting")
	}
	again.Close()

	record := restartRecord{BootTime: boot.UTC(), RequestedAt: time.Now().Add(-time.Hour), Deadline: time.Now().Add(-time.Minute)}
	writeRecord(t, filepath.Join(dir, id, restartFile), record)
	completion, _ := completionOf(t, f.manager(dir), id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_TIMED_OUT {
		t.Fatalf("a restart whose wait ended without a restart must time out, got %v", completion.GetResult())
	}
}

func TestARestartTheOperatingSystemRefusesFailsToStart(t *testing.T) {
	fake := useFakeRestart(t, time.Now().Add(-time.Hour))
	fake.fail = errors.New("access denied")
	f := newFixture(t)
	dir := t.TempDir()
	id := "35555555-5555-5555-5555-555555555555"

	completion, _ := completionOf(t, func() *Manager { m := f.manager(dir); m.Accept(f.sign(f.restartPayload(id, 600))); return m }(), id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_FAILED_TO_START || !strings.Contains(completion.GetError(), "access denied") {
		t.Fatalf("a refused restart must fail to start with the reason, got %v: %s", completion.GetResult(), completion.GetError())
	}
	if exists(filepath.Join(dir, id, restartFile)) {
		t.Fatal("a restart that never started must not be waited for after the next start")
	}
}

func writeRecord(t *testing.T, path string, record restartRecord) {
	t.Helper()
	data, err := json.Marshal(record)
	if err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, data, 0o600); err != nil {
		t.Fatal(err)
	}
}
