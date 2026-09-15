package jobs

import (
	"bytes"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/sha256"
	"encoding/hex"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

const (
	testInstance = "6f1d3c1e-5a4b-4f7e-9a31-2b8f0c1d2e3f"
	testEndpoint = "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	testKeyID    = "a1b2c3d4e5f60718"
)

type fixture struct {
	t     *testing.T
	pub   ed25519.PublicKey
	priv  ed25519.PrivateKey
	trust signedconfig.Trust
}

func newFixture(t *testing.T) *fixture {
	pub, priv, _ := ed25519.GenerateKey(rand.Reader)
	return &fixture{t: t, pub: pub, priv: priv, trust: signedconfig.Trust{SigningKey: pub, KeyID: testKeyID, InstanceID: testInstance, EndpointID: testEndpoint}}
}

// nativeScript returns a script for this OS that prints to both streams and exits with 3, or sleeps.
func nativeScript(sleep bool) (agentv1.ScriptLanguage, string) {
	if runtime.GOOS == "windows" {
		if sleep {
			return agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH, "@echo off\r\nping -n 30 127.0.0.1 >nul\r\n"
		}
		return agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH, "@echo off\r\necho hello from job\r\necho oops 1>&2\r\nexit /b 3\r\n"
	}
	if sleep {
		return agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL, "sleep 30\n"
	}
	return agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL, "echo hello from job\necho oops >&2\nexit 3\n"
}

func (f *fixture) payload(id string, language agentv1.ScriptLanguage, body string) *agentv1.JobPayload {
	sum := sha256.Sum256([]byte(body))
	return &agentv1.JobPayload{
		JobId: id, InstanceId: testInstance, EndpointId: testEndpoint, Type: agentv1.JobType_JOB_TYPE_SCRIPT,
		ValidUntil: timestamppb.New(time.Now().Add(time.Hour)), InitiatedBy: "Tess", TimeoutSeconds: 600, MaxOutputBytes: MaxOutputBytes,
		Script: &agentv1.ScriptJob{Language: language, Name: "test", Version: 1, Body: body, Sha256: hex.EncodeToString(sum[:])},
	}
}

func (f *fixture) sign(payload *agentv1.JobPayload) *agentv1.SignedJob {
	data, err := proto.Marshal(payload)
	if err != nil {
		f.t.Fatal(err)
	}
	message := append(append([]byte(Context), 0), data...)
	return &agentv1.SignedJob{Payload: data, Signature: ed25519.Sign(f.priv, message), KeyId: testKeyID}
}

func (f *fixture) manager(dir string) *Manager {
	m, err := NewManager(Options{
		Dir: dir, Access: platform.AccessCurrentUser, Logger: logging.Discard(),
		Trust: func() signedconfig.Trust { return f.trust }, Managed: func() bool { return true }, FlushInterval: 50 * time.Millisecond,
	})
	if err != nil {
		f.t.Fatal(err)
	}
	f.t.Cleanup(m.Close)
	return m
}

func completionOf(t *testing.T, m *Manager, id string, timeout time.Duration) (*agentv1.JobCompletion, []Message) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		messages := m.Pending(func(string) bool { return false })
		for _, message := range messages {
			if c := message.Msg.GetJobCompletion(); c != nil && c.GetJobId() == id {
				return c, messages
			}
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("job %s did not complete", id)
	return nil, nil
}

func TestVerifyRefusesEverythingThatIsNotThisEndpointsValidSignedJob(t *testing.T) {
	f := newFixture(t)
	language, body := nativeScript(false)
	now := time.Now()
	valid := f.payload("11111111-1111-1111-1111-111111111111", language, body)
	if _, err := Verify(f.sign(valid), f.trust, true, now); err != nil {
		t.Fatalf("a valid job must verify: %v", err)
	}

	cases := map[string]func() (*agentv1.SignedJob, bool){
		"agent-only": func() (*agentv1.SignedJob, bool) { return f.sign(valid), false },
		"other endpoint": func() (*agentv1.SignedJob, bool) {
			p := proto.Clone(valid).(*agentv1.JobPayload)
			p.EndpointId = "22222222-2222-2222-2222-222222222222"
			return f.sign(p), true
		},
		"expired": func() (*agentv1.SignedJob, bool) {
			p := proto.Clone(valid).(*agentv1.JobPayload)
			p.ValidUntil = timestamppb.New(now.Add(-6 * time.Minute))
			return f.sign(p), true
		},
		"too long": func() (*agentv1.SignedJob, bool) {
			p := proto.Clone(valid).(*agentv1.JobPayload)
			p.ValidUntil = timestamppb.New(now.Add(9 * 24 * time.Hour))
			return f.sign(p), true
		},
		"changed body": func() (*agentv1.SignedJob, bool) {
			p := proto.Clone(valid).(*agentv1.JobPayload)
			p.Script.Body += "\nrm -rf /"
			return f.sign(p), true
		},
		"other language": func() (*agentv1.SignedJob, bool) {
			other := agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BASH
			if runtime.GOOS != "windows" {
				other = agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL
			}
			return f.sign(f.payload(valid.JobId, other, body)), true
		},
		"tampered payload": func() (*agentv1.SignedJob, bool) {
			sj := f.sign(valid)
			sj.Payload = bytes.Replace(sj.Payload, []byte("hello"), []byte("HELLO"), 1)
			return sj, true
		},
		"other key": func() (*agentv1.SignedJob, bool) {
			_, other, _ := ed25519.GenerateKey(rand.Reader)
			data, _ := proto.Marshal(valid)
			return &agentv1.SignedJob{Payload: data, Signature: ed25519.Sign(other, append(append([]byte(Context), 0), data...)), KeyId: testKeyID}, true
		},
	}
	for name, build := range cases {
		sj, managed := build()
		if _, err := Verify(sj, f.trust, managed, now); err == nil {
			t.Errorf("%s: the job must be refused", name)
		}
	}
}

func TestAJobRunsOnceAndItsOutputIsReportedUntilAcknowledged(t *testing.T) {
	f := newFixture(t)
	dir := filepath.Join(t.TempDir(), "jobs")
	m := f.manager(dir)
	id := "33333333-3333-3333-3333-333333333333"
	language, body := nativeScript(false)
	sj := f.sign(f.payload(id, language, body))

	m.Accept(sj)
	completion, messages := completionOf(t, m, id, 30*time.Second)
	m.Accept(sj) // delivered again after a reconnect: ignored

	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_EXITED || completion.GetExitCode() != 3 {
		t.Fatalf("unexpected completion: %v", completion)
	}
	var stdout, stderr []byte
	started := false
	for _, message := range messages {
		if message.Msg.GetJobStarted() != nil {
			started = true
		}
		if out := message.Msg.GetJobOutput(); out != nil {
			if out.GetStream() == agentv1.JobStream_JOB_STREAM_STDOUT {
				stdout = append(stdout, out.GetData()...)
			} else {
				stderr = append(stderr, out.GetData()...)
			}
		}
	}
	if !started || !strings.Contains(string(stdout), "hello from job") || !strings.Contains(string(stderr), "oops") {
		t.Fatalf("missing started or output: started=%v stdout=%q stderr=%q", started, stdout, stderr)
	}
	sum := sha256.Sum256(stdout)
	if completion.GetStdout().GetSha256() != hex.EncodeToString(sum[:]) || completion.GetStdout().GetBytes() != uint64(len(stdout)) {
		t.Fatal("the stdout summary must match the sent chunks")
	}

	for _, message := range messages {
		switch {
		case message.Msg.GetJobStarted() != nil:
			m.Ack(&agentv1.JobAck{JobId: id, Kind: agentv1.JobAckKind_JOB_ACK_KIND_STARTED})
		case message.Msg.GetJobOutput() != nil:
			out := message.Msg.GetJobOutput()
			m.Ack(&agentv1.JobAck{JobId: id, Kind: agentv1.JobAckKind_JOB_ACK_KIND_OUTPUT, Stream: out.GetStream(), Sequence: out.GetSequence()})
		}
	}
	if _, err := os.Stat(filepath.Join(dir, id)); err != nil {
		t.Fatal("the job must stay on disk until its completion is acknowledged")
	}
	m.Ack(&agentv1.JobAck{JobId: id, Kind: agentv1.JobAckKind_JOB_ACK_KIND_COMPLETION})
	if _, err := os.Stat(filepath.Join(dir, id)); !os.IsNotExist(err) {
		t.Fatal("a fully acknowledged job must be removed")
	}
	m.Accept(sj)
	if _, err := os.Stat(filepath.Join(dir, id)); !os.IsNotExist(err) {
		t.Fatal("a job id must never run twice, also after its directory is gone")
	}
}

func TestATimedOutScriptIsEndedAndTruncatedOutputIsMarked(t *testing.T) {
	previous := minTimeout
	minTimeout = time.Second
	t.Cleanup(func() { minTimeout = previous })
	f := newFixture(t)
	m := f.manager(filepath.Join(t.TempDir(), "jobs"))

	language, body := nativeScript(true)
	slow := f.payload("44444444-4444-4444-4444-444444444444", language, body)
	slow.TimeoutSeconds = 1
	started := time.Now()
	m.Accept(f.sign(slow))
	completion, _ := completionOf(t, m, slow.JobId, 25*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_TIMED_OUT || time.Since(started) > 20*time.Second {
		t.Fatalf("the script must be ended at its timeout: %v after %s", completion.GetResult(), time.Since(started))
	}

	language, body = nativeScript(false)
	small := f.payload("55555555-5555-5555-5555-555555555555", language, body)
	small.MaxOutputBytes = 4
	m.Accept(f.sign(small))
	completion, _ = completionOf(t, m, small.JobId, 30*time.Second)
	if !completion.GetOutputTruncated() || completion.GetStdout().GetBytes()+completion.GetStderr().GetBytes() > 4 {
		t.Fatalf("output beyond the limit must be dropped and marked: %v", completion)
	}
}

func TestAJobThatWasRunningWhenTheAgentStoppedIsReportedAsInterruptedAndNotRunAgain(t *testing.T) {
	f := newFixture(t)
	dir := filepath.Join(t.TempDir(), "jobs")
	id := "66666666-6666-6666-6666-666666666666"
	language, body := nativeScript(false)
	data, _ := proto.Marshal(f.sign(f.payload(id, language, body)))
	if err := os.MkdirAll(filepath.Join(dir, id), 0o700); err != nil {
		t.Fatal(err)
	}
	_ = os.WriteFile(filepath.Join(dir, id, jobFile), data, 0o600)
	_ = os.WriteFile(filepath.Join(dir, id, startedFile), []byte(time.Now().UTC().Format(time.RFC3339Nano)), 0o600)

	m := f.manager(dir)
	completion, messages := completionOf(t, m, id, 5*time.Second)
	if completion.GetResult() != agentv1.JobResult_JOB_RESULT_INTERRUPTED {
		t.Fatalf("expected interrupted, got %v", completion.GetResult())
	}
	for _, message := range messages {
		if message.Msg.GetJobOutput() != nil {
			t.Fatal("an interrupted job must not run again")
		}
	}
}
