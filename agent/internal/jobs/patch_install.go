package jobs

import (
	"crypto/sha256"
	"encoding"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/patch"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// A patch installation job (0.9.0 step 3, PATCHING.md section 5) installs updates with the tools of the operating system
// (internal/patch). The jobs package bounds it with the signed timeout and never stops the package manager: at the timeout the job
// is answered and its slot freed, while the installation runs on behind the guard of the patch package, which refuses another
// installation until it ended; a scan follows every installation. A restart the updates need is the restart of restart.go with
// the notice of the job, and the job completes after the endpoint started again.

const (
	errPatchTimedOut = "The package manager was still working; the next scan shows what was installed."
	errPatchRunning  = "An earlier installation is still running; the next scan shows what it installed. Start this one again after it ended."
	errPatchStopped  = "The agent stopped while updates were being installed; the package manager was left to finish. The job is not run " +
		"again; the next scan shows what was installed."
	errPatchInterrupted = "The agent stopped while updates were being installed. The job is not run again; the next scan shows what " +
		"was installed."
	errPatchInternal = "The installation failed inside the agent; the agent log has the details."

	// MaxNoticeSeconds is the longest notice before a restart for updates.
	MaxNoticeSeconds = patch.MaxRestartNotice
	// maxNoticeLen bounds the restart notice shown to a signed-in user.
	maxNoticeLen = 500
	// patchRestartGrace is how long after the notice the agent waits for the restart before it cancels it.
	patchRestartGrace = 30 * time.Minute
	// lineReserve is the part of the output limit kept for the lines of the agent, so the output of the package manager cannot cut
	// the lines the server reads.
	lineReserve = 1 << 20
)

// The patch package and the operating system are reached through these variables, so a test installs nothing.
var (
	patchInstall        = patch.Install
	beginPatchInstall   = patch.BeginInstall
	patchEngine         = patch.InstallEngine
	requestRestartAfter = osRequestRestartAfter
)

// patchRestart is what a patch job that restarts the endpoint keeps next to its restart record, to complete after the start.
type patchRestart struct {
	ExitCode  int32       `json:"exitCode"`
	Error     string      `json:"error,omitempty"`
	Truncated bool        `json:"truncated,omitempty"`
	Stdout    streamState `json:"stdout"`
}

// streamState is a stream writer at a moment: its next chunk, its bytes and the state of its hash.
type streamState struct {
	Sequence uint64 `json:"sequence"`
	Bytes    uint64 `json:"bytes"`
	Hash     []byte `json:"hash"`
}

func (w *streamWriter) state() (streamState, error) {
	w.mu.Lock()
	defer w.mu.Unlock()
	marshaler, ok := w.hash.(encoding.BinaryMarshaler)
	if !ok {
		return streamState{}, errors.New("the hash of the output cannot be stored")
	}
	data, err := marshaler.MarshalBinary()
	if err != nil {
		return streamState{}, err
	}
	return streamState{Sequence: w.sequence, Bytes: w.bytes, Hash: data}, nil
}

// resumeStream continues the stdout of a job after the agent started again, from a stored state.
func (m *Manager) resumeStream(id string, s streamState) (*streamWriter, error) {
	h := sha256.New()
	unmarshaler, ok := h.(encoding.BinaryUnmarshaler)
	if !ok {
		return nil, errors.New("the hash of the output cannot be restored")
	}
	if err := unmarshaler.UnmarshalBinary(s.Hash); err != nil {
		return nil, err
	}
	return &streamWriter{m: m, id: id, stream: agentv1.JobStream_JOB_STREAM_STDOUT, budget: &outputBudget{remaining: lineReserve},
		sequence: s.Sequence, bytes: s.Bytes, hash: h}, nil
}

// patchOutput is the stdout of a patch job: the package manager's own output within the limit minus a reserve, the lines of the
// agent within the reserve. Closed at the timeout, it drops what the installation still writes.
type patchOutput struct {
	mu       sync.Mutex
	w        *streamWriter
	closed   bool
	toolLeft int64
	toolCut  bool
}

type writerFunc func([]byte) (int, error)

func (f writerFunc) Write(p []byte) (int, error) { return f(p) }

func (o *patchOutput) tool() io.Writer {
	return writerFunc(func(p []byte) (int, error) {
		o.mu.Lock()
		defer o.mu.Unlock()
		if o.closed {
			return len(p), nil
		}
		allowed := int(min(int64(len(p)), max(o.toolLeft, 0)))
		o.toolLeft -= int64(allowed)
		if allowed < len(p) && !o.toolCut {
			o.toolCut = true
			_, _ = o.w.Write(p[:allowed])
			_, _ = o.w.Write([]byte("\n[The output of the package manager was cut here: it reached the output limit of the job.]\n"))
			return len(p), nil
		}
		_, _ = o.w.Write(p[:allowed])
		return len(p), nil
	})
}

func (o *patchOutput) lines() io.Writer {
	return writerFunc(func(p []byte) (int, error) {
		o.mu.Lock()
		defer o.mu.Unlock()
		if !o.closed {
			_, _ = o.w.Write(p)
		}
		return len(p), nil
	})
}

func (o *patchOutput) close() {
	o.mu.Lock()
	o.closed = true
	o.mu.Unlock()
}

func (o *patchOutput) truncated() bool {
	o.mu.Lock()
	defer o.mu.Unlock()
	return o.toolCut
}

// afterPatchInstall asks for a scan; it never fails the caller.
func (m *Manager) afterPatchInstall() {
	if m.opts.AfterPatchInstall == nil {
		return
	}
	_ = safego.Call(m.opts.Logger, "scan after a patch installation", func() error {
		m.opts.AfterPatchInstall()
		return nil
	})
}

// runPatchInstall runs one verified patch installation job and stores its completion.
func (m *Manager) runPatchInstall(id string, payload *agentv1.JobPayload) {
	release, ok := beginPatchInstall()
	if !ok {
		m.opts.Logger.Warn("refused a patch installation: an earlier one still runs", "jobId", id)
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_REFUSED, Error: errPatchRunning})
		return
	}
	dir := m.dir(id)
	started := m.opts.Now().UTC()
	if err := platform.WriteFileAtomic(filepath.Join(dir, startedFile), []byte(started.Format(time.RFC3339Nano)), m.opts.Access); err != nil {
		release()
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: "could not record the start: " + err.Error()})
		return
	}
	m.signal()

	limit := OutputLimit(payload)
	budget := &outputBudget{remaining: limit}
	stdout := &streamWriter{m: m, id: id, stream: agentv1.JobStream_JOB_STREAM_STDOUT, budget: budget, hash: sha256.New()}
	out := &patchOutput{w: stdout, toolLeft: limit - min(limit/2, lineReserve)}
	timeout := Timeout(payload)
	job := payload.GetPatchInstall()
	logger := m.opts.Logger
	logger.Info("patch installation started", "jobId", id, "scope", job.GetScope().String(), "updates", len(job.GetUpdates()),
		"restart", job.GetRestart().String(), "timeout", timeout.String(), "initiatedBy", payload.GetInitiatedBy())

	done := make(chan patch.InstallResult, 1)
	// Not part of the manager's wait group: the installation may outlive the job and the agent's Close, and is never stopped.
	go func() {
		result := patch.InstallResult{ExitCode: 1, Error: errPatchInternal}
		defer func() {
			release()
			logger.Info("patch installation ended", "jobId", id, "exitCode", result.ExitCode, "installed", result.Installed,
				"restartRequired", result.RestartRequired)
			m.afterPatchInstall()
			done <- result
		}()
		defer safego.Recover(logger, "patch installation "+id)
		result = patchInstall(m.ctx, job, out.tool(), out.lines(), logger)
	}()

	flushDone := make(chan struct{})
	go func() {
		ticker := time.NewTicker(m.opts.FlushInterval)
		defer ticker.Stop()
		for {
			select {
			case <-ticker.C:
				stdout.flushPartial()
			case <-flushDone:
				return
			}
		}
	}()
	timer := time.NewTimer(timeout)
	defer timer.Stop()
	var result patch.InstallResult
	select {
	case result = <-done:
		close(flushDone)
	case <-timer.C:
		m.endPatchEarly(id, out, stdout, budget, flushDone, agentv1.JobResult_JOB_RESULT_TIMED_OUT, errPatchTimedOut)
		logger.Warn("a patch installation reached its timeout; the package manager is left to finish", "jobId", id, "timeout", timeout.String())
		return
	case <-m.ctx.Done():
		m.endPatchEarly(id, out, stdout, budget, flushDone, agentv1.JobResult_JOB_RESULT_INTERRUPTED, errPatchStopped)
		return
	}
	stdout.flushPartial()
	m.finishPatchInstall(id, payload, result, stdout, func() bool { return budget.truncated() || out.truncated() })
}

// endPatchEarly answers a job whose installation still runs: its output ends here, the installation goes on.
func (m *Manager) endPatchEarly(id string, out *patchOutput, stdout *streamWriter, budget *outputBudget, flushDone chan struct{},
	result agentv1.JobResult, message string) {
	out.close()
	close(flushDone)
	stdout.flushPartial()
	completion := &agentv1.JobCompletion{Result: result, Error: message, Stdout: stdout.summary(),
		OutputTruncated: budget.truncated() || out.truncated()}
	m.complete(id, completion)
}

// finishPatchInstall writes the restart line and completes the job, or restarts the endpoint first.
func (m *Manager) finishPatchInstall(id string, payload *agentv1.JobPayload, result patch.InstallResult, stdout *streamWriter, truncated func() bool) {
	job := payload.GetPatchInstall()
	switch {
	case !result.RestartRequired:
		_, _ = stdout.Write([]byte(patch.RestartLine(patch.RestartNotRequired)))
	case job.GetRestart() != agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED || result.Installed == 0:
		// Never, or nothing was installed by this job: the restart is left to a technician or a later round.
		_, _ = stdout.Write([]byte(patch.RestartLine(patch.RestartRequired)))
	default:
		restarting, reason := m.restartAfterPatch(id, payload, result, stdout, truncated)
		if restarting {
			return
		}
		if result.Error == "" {
			result.Error = reason
		}
	}
	stdout.flushPartial()
	completion := &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_EXITED, ExitCode: result.ExitCode, Error: result.Error,
		Stdout: stdout.summary(), OutputTruncated: truncated()}
	if stdout.err != nil && completion.Error == "" {
		completion.Error = "Some output could not be stored on the endpoint: " + stdout.err.Error()
	}
	m.complete(id, completion)
}

// restartAfterPatch records the restart next to the job, asks the operating system to restart after the notice and waits for it.
// It returns false with the reason when the endpoint is not restarted; the job then completes with "restart required".
func (m *Manager) restartAfterPatch(id string, payload *agentv1.JobPayload, result patch.InstallResult, stdout *streamWriter,
	truncated func() bool) (bool, string) {
	job := payload.GetPatchInstall()
	notRestarted := func(reason string) (bool, string) {
		_, _ = stdout.Write([]byte(reason + "\n" + patch.RestartLine(patch.RestartRequired)))
		return false, reason
	}
	boot, err := readBootTime()
	if err != nil {
		return notRestarted("The boot time of the endpoint cannot be read, so a restart could not be confirmed and the endpoint is not restarted: " + err.Error())
	}
	notice := time.Duration(job.GetRestartNoticeSeconds()) * time.Second
	message := patchRestartNotice(job.GetRestartMessage(), job.GetRestartNoticeSeconds())
	_, _ = stdout.Write([]byte(fmt.Sprintf("The updates need a restart. The endpoint restarts in %s; signed-in users see: %s\n",
		noticeDuration(job.GetRestartNoticeSeconds()), message)))
	_, _ = stdout.Write([]byte(patch.RestartLine(patch.RestartRestarting)))
	stdout.flushPartial()
	state, err := stdout.state()
	if err != nil {
		return notRestarted("The restart could not be recorded, so the endpoint is not restarted: " + err.Error())
	}
	now := m.opts.Now().UTC()
	record := restartRecord{BootTime: boot.UTC(), RequestedAt: now, Deadline: now.Add(notice + patchRestartGrace),
		Patch: &patchRestart{ExitCode: result.ExitCode, Error: result.Error, Truncated: truncated(), Stdout: state}}
	data, _ := json.Marshal(record)
	path := filepath.Join(m.dir(id), restartFile)
	if err := platform.WriteFileAtomic(path, data, m.opts.Access); err != nil {
		return notRestarted("The restart could not be recorded, so the endpoint is not restarted: " + err.Error())
	}
	if err := requestRestartAfter(m.ctx, notice, message); err != nil {
		_ = os.Remove(path)
		return notRestarted("The endpoint could not be restarted: " + err.Error())
	}
	m.opts.Logger.Info("asked the operating system to restart the endpoint for updates", "jobId", id, "notice", notice.String(),
		"deadline", record.Deadline.Format(time.RFC3339))
	m.waitForRestart(id, record)
	return true, ""
}

// patchCompletion completes a patch job from its restart record, with line appended to its stdout.
func (m *Manager) patchCompletion(id string, p *patchRestart, result agentv1.JobResult, message, line string) *agentv1.JobCompletion {
	completion := &agentv1.JobCompletion{Result: result, Error: message, OutputTruncated: p.Truncated}
	if result == agentv1.JobResult_JOB_RESULT_EXITED {
		completion.ExitCode = p.ExitCode
		if completion.Error == "" {
			completion.Error = p.Error
		}
	}
	w, err := m.resumeStream(id, p.Stdout)
	if err != nil {
		// The earlier output is on its way or delivered; only its summary is lost.
		m.opts.Logger.Warn("the output of a patch job could not be continued after the restart", "jobId", id, "error", err)
		empty := sha256.Sum256(nil)
		completion.Stdout = &agentv1.JobStreamSummary{Chunks: p.Stdout.Sequence, Bytes: p.Stdout.Bytes, Sha256: hex.EncodeToString(empty[:])}
		if completion.Error == "" {
			completion.Error = "The summary of the output could not be restored after the restart: " + err.Error()
		}
		return completion
	}
	if line != "" {
		_, _ = w.Write([]byte(line))
		w.flushPartial()
	}
	completion.Stdout = w.summary()
	if w.err != nil && completion.Error == "" {
		completion.Error = "Some output could not be stored on the endpoint: " + w.err.Error()
	}
	return completion
}

// isPatchJob reads the type of a stored job, only to decide whether to scan after it; nothing runs on it.
func (m *Manager) isPatchJob(id string) bool {
	data, err := os.ReadFile(filepath.Join(m.dir(id), jobFile))
	if err != nil {
		return false
	}
	var sj agentv1.SignedJob
	if proto.Unmarshal(data, &sj) != nil {
		return false
	}
	var payload agentv1.JobPayload
	if (proto.UnmarshalOptions{DiscardUnknown: true}).Unmarshal(sj.GetPayload(), &payload) != nil {
		return false
	}
	return payload.GetType() == agentv1.JobType_JOB_TYPE_PATCH_INSTALL
}

// patchRestartNotice is the text a signed-in user sees: the message of the job, or the agent's own. Control characters become
// spaces, a leading dash goes (shutdown on Linux would read it as an option), and it is cut to maxNoticeLen bytes.
func patchRestartNotice(custom string, seconds uint32) string {
	msg := sanitizeNotice(custom)
	if msg == "" {
		msg = fmt.Sprintf("Updates were installed and this computer restarts in %s to finish them. Save your work.", noticeDuration(seconds))
	}
	return msg
}

func sanitizeNotice(s string) string {
	s = strings.ToValidUTF8(s, "")
	s = strings.Map(func(r rune) rune {
		if unicode.IsControl(r) {
			return ' '
		}
		return r
	}, s)
	s = strings.Join(strings.Fields(s), " ")
	s = strings.TrimLeft(s, "- ")
	if len(s) > maxNoticeLen {
		end := maxNoticeLen
		for end > 0 && !utf8.RuneStart(s[end]) {
			end--
		}
		s = strings.TrimSpace(s[:end])
	}
	return s
}

// noticeMinutes is the notice in whole minutes, rounded up, as shutdown on Linux takes it.
func noticeMinutes(seconds uint32) uint32 { return max((seconds+59)/60, 1) }

func noticeDuration(seconds uint32) string {
	if n := noticeMinutes(seconds); n != 1 {
		return fmt.Sprintf("%d minutes", n)
	}
	return "1 minute"
}
