package jobs

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"hash"
	"os"
	"os/exec"
	"path/filepath"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// waitDelay is how long the agent waits for output after the script's process ended or was killed; child processes that keep
// the output open are not waited for longer.
const waitDelay = 10 * time.Second

// run executes one verified job and stores its completion. It never returns an error: every outcome is a completion.
func (m *Manager) run(id string, payload *agentv1.JobPayload) {
	dir := m.dir(id)
	script := payload.GetScript()
	scriptPath, err := writeScript(dir, script, m.opts.Access)
	if err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: "could not write the script: " + err.Error()})
		return
	}
	started := m.opts.Now().UTC()
	if err := platform.WriteFileAtomic(filepath.Join(dir, startedFile), []byte(started.Format(time.RFC3339Nano)), m.opts.Access); err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: "could not record the start: " + err.Error()})
		return
	}
	m.signal()

	timeout := Timeout(payload)
	ctx, cancel := context.WithTimeout(m.ctx, timeout)
	defer cancel()

	budget := &outputBudget{remaining: OutputLimit(payload)}
	stdout := &streamWriter{m: m, id: id, stream: agentv1.JobStream_JOB_STREAM_STDOUT, budget: budget, hash: sha256.New()}
	stderr := &streamWriter{m: m, id: id, stream: agentv1.JobStream_JOB_STREAM_STDERR, budget: budget, hash: sha256.New()}

	cmd, tree, err := command(ctx, script.GetLanguage(), scriptPath, dir)
	if err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: err.Error()})
		return
	}
	defer tree.close()
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	cmd.Env = append(os.Environ(), "FLEETO_JOB_ID="+id)
	cmd.WaitDelay = waitDelay
	cmd.Cancel = tree.kill

	m.opts.Logger.Info("job started", "jobId", id, "script", script.GetName(), "language", LanguageName(script.GetLanguage()), "timeout", timeout.String())
	if err := cmd.Start(); err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START,
			Error: fmt.Sprintf("could not start %s: %v", LanguageName(script.GetLanguage()), err)})
		return
	}
	tree.attach(cmd)

	flushDone := make(chan struct{})
	go func() {
		ticker := time.NewTicker(m.opts.FlushInterval)
		defer ticker.Stop()
		for {
			select {
			case <-ticker.C:
				stdout.flushPartial()
				stderr.flushPartial()
			case <-flushDone:
				return
			}
		}
	}()
	waitErr := cmd.Wait()
	close(flushDone)
	stdout.flushPartial()
	stderr.flushPartial()

	completion := &agentv1.JobCompletion{
		Stdout:          stdout.summary(),
		Stderr:          stderr.summary(),
		OutputTruncated: budget.truncated(),
	}
	var exitErr *exec.ExitError
	switch {
	case errors.Is(m.ctx.Err(), context.Canceled):
		completion.Result = agentv1.JobResult_JOB_RESULT_INTERRUPTED
		completion.Error = "The agent stopped while the job ran; the script was ended. The job is not run again."
	case errors.Is(ctx.Err(), context.DeadlineExceeded):
		completion.Result = agentv1.JobResult_JOB_RESULT_TIMED_OUT
		completion.Error = fmt.Sprintf("The script did not finish within %s and was ended.", timeout)
	case waitErr == nil:
		completion.Result = agentv1.JobResult_JOB_RESULT_EXITED
		completion.ExitCode = 0
	case errors.As(waitErr, &exitErr):
		completion.Result = agentv1.JobResult_JOB_RESULT_EXITED
		completion.ExitCode = int32(exitErr.ExitCode())
	case errors.Is(waitErr, exec.ErrWaitDelay):
		// The script ended; a child process kept the output open and was left alone after waitDelay.
		completion.Result = agentv1.JobResult_JOB_RESULT_EXITED
		completion.ExitCode = int32(cmd.ProcessState.ExitCode())
	default:
		completion.Result = agentv1.JobResult_JOB_RESULT_FAILED_TO_START
		completion.Error = waitErr.Error()
	}
	if err := firstError(stdout.err, stderr.err); err != nil && completion.Error == "" {
		completion.Error = "Some output could not be stored on the endpoint: " + err.Error()
	}
	m.opts.Logger.Info("job ended", "jobId", id, "result", completion.GetResult().String(), "exitCode", completion.GetExitCode(),
		"outputBytes", completion.GetStdout().GetBytes()+completion.GetStderr().GetBytes(), "truncated", completion.GetOutputTruncated())
	m.complete(id, completion)
}

func firstError(errs ...error) error {
	for _, err := range errs {
		if err != nil {
			return err
		}
	}
	return nil
}

// writeScript stores the script in the job's protected directory. PowerShell gets a UTF-8 byte order mark, so Windows
// PowerShell reads non-ASCII characters correctly.
func writeScript(dir string, script *agentv1.ScriptJob, access platform.Access) (string, error) {
	var name string
	body := []byte(script.GetBody())
	switch script.GetLanguage() {
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL:
		name = "script.ps1"
		body = append([]byte{0xEF, 0xBB, 0xBF}, body...)
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH:
		name = "script.cmd"
	default:
		name = "script.sh"
	}
	path := filepath.Join(dir, name)
	if err := platform.WriteFileAtomic(path, body, access); err != nil {
		return "", err
	}
	return path, nil
}

// outputBudget is the output both streams of a job may still send.
type outputBudget struct {
	mu        sync.Mutex
	remaining int64
	cut       bool
}

func (b *outputBudget) take(n int) int {
	b.mu.Lock()
	defer b.mu.Unlock()
	allowed := int(min(int64(n), b.remaining))
	b.remaining -= int64(allowed)
	if allowed < n {
		b.cut = true
	}
	return allowed
}

func (b *outputBudget) truncated() bool {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.cut
}

// streamWriter turns one output stream into numbered chunk files of at most ChunkBytes.
type streamWriter struct {
	m      *Manager
	id     string
	stream agentv1.JobStream
	budget *outputBudget

	mu       sync.Mutex
	buf      []byte
	sequence uint64
	bytes    uint64
	hash     hash.Hash
	err      error
}

// Write never fails towards the process: output beyond the budget is dropped and the job is marked truncated.
func (w *streamWriter) Write(p []byte) (int, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	allowed := w.budget.take(len(p))
	w.buf = append(w.buf, p[:allowed]...)
	for len(w.buf) >= ChunkBytes {
		w.flushLocked(w.buf[:ChunkBytes])
		w.buf = append([]byte(nil), w.buf[ChunkBytes:]...)
	}
	return len(p), nil
}

func (w *streamWriter) flushPartial() {
	w.mu.Lock()
	defer w.mu.Unlock()
	if len(w.buf) > 0 {
		w.flushLocked(w.buf)
		w.buf = nil
	}
}

func (w *streamWriter) flushLocked(data []byte) {
	path := filepath.Join(w.m.dir(w.id), chunkName(w.stream, w.sequence))
	if err := platform.WriteFileAtomic(path, data, w.m.opts.Access); err != nil {
		if w.err == nil {
			w.err = err
		}
		return
	}
	w.hash.Write(data)
	w.bytes += uint64(len(data))
	w.sequence++
	w.m.signal()
}

func (w *streamWriter) summary() *agentv1.JobStreamSummary {
	w.mu.Lock()
	defer w.mu.Unlock()
	return &agentv1.JobStreamSummary{Chunks: w.sequence, Bytes: w.bytes, Sha256: hex.EncodeToString(w.hash.Sum(nil))}
}
