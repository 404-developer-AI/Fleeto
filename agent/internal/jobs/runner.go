package jobs

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"hash"
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
	// A job that runs as the signed-in user needs that user's session before anything else: without one it fails now
	// rather than half way, and the script is staged where that user can read it instead of in the protected job directory.
	var session *signedInUser
	if payload.GetRunAs() == agentv1.JobRunAs_JOB_RUN_AS_LOGGED_ON_USER {
		found, err := signedInSession()
		if err != nil {
			m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: runAsUserError(err)})
			return
		}
		defer found.close()
		session = found
	}

	scriptPath, workDir, release, err := stage(dir, script, m.opts.Access, session)
	if err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: "could not write the script: " + err.Error()})
		return
	}
	defer release()
	if session != nil {
		// Recorded next to the start, so the job history names the user even when the agent restarts before the start is sent.
		if err := platform.WriteFileAtomic(filepath.Join(dir, accountFile), []byte(session.accountName()), m.opts.Access); err != nil {
			m.opts.Logger.Warn("the account of the job could not be recorded", "jobId", id, "error", err)
		}
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

	proc, tree, err := command(ctx, commandOptions{
		language:   script.GetLanguage(),
		scriptPath: scriptPath,
		dir:        workDir,
		env:        []string{"FLEETO_JOB_ID=" + id},
		stdout:     stdout,
		stderr:     stderr,
		wait:       waitDelay,
		session:    session,
	})
	if err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: runAsUserError(err)})
		return
	}
	defer tree.close()

	m.opts.Logger.Info("job started", "jobId", id, "script", script.GetName(), "language", LanguageName(script.GetLanguage()),
		"timeout", timeout.String(), "asUser", session != nil)
	if err := proc.Start(); err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START,
			Error: fmt.Sprintf("could not start %s: %v", LanguageName(script.GetLanguage()), err)})
		return
	}
	tree.attach(proc.Pid())

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
	waitErr := proc.Wait()
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
	// The script ended on its own. ErrWaitDelay means a child process kept the output open and was left alone.
	case waitErr == nil, errors.As(waitErr, &exitErr), errors.Is(waitErr, exec.ErrWaitDelay):
		completion.Result = agentv1.JobResult_JOB_RESULT_EXITED
		completion.ExitCode = int32(proc.ExitCode())
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

// runAsUserError turns the one error a technician must recognise into the sentence shown on the job, and passes
// everything else through.
func runAsUserError(err error) string {
	if errors.Is(err, ErrNoUserSignedIn) {
		return "No user is signed in on this endpoint, so the script could not run as the signed-in user. Start the job again when someone is signed in."
	}
	return err.Error()
}

// stage puts the script where the account that runs it can read it: the protected job directory for a script that runs
// as SYSTEM or root, a directory only the signed-in user may read otherwise. release removes what it created.
func stage(dir string, script *agentv1.ScriptJob, access platform.Access, session *signedInUser) (scriptPath, workDir string, release func(), err error) {
	if session == nil {
		path, err := writeScript(dir, script, access)
		return path, dir, func() {}, err
	}
	return session.stage(script)
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
	name, body := scriptFile(script)
	path := filepath.Join(dir, name)
	if err := platform.WriteFileAtomic(path, body, access); err != nil {
		return "", err
	}
	return path, nil
}

// scriptFile is the file name and the exact bytes of one script.
func scriptFile(script *agentv1.ScriptJob) (string, []byte) {
	body := []byte(script.GetBody())
	switch script.GetLanguage() {
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL:
		return "script.ps1", append([]byte{0xEF, 0xBB, 0xBF}, body...)
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH:
		return "script.cmd", body
	default:
		return "script.sh", body
	}
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
