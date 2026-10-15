package jobs

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"time"

	"github.com/shirou/gopsutil/v4/host"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// A restart job (0.6.0) restarts the endpoint for a step of a task. The signed payload carries no input: the agent runs a fixed
// command that restarts the endpoint after a minute's notice, and completes the job only after it started again with a newer
// boot time. Without a restart within the signed timeout the planned restart is cancelled and the job times out.

const (
	restartFile = "restart.json"
	// bootTolerance absorbs the jitter of the boot time Windows reports.
	bootTolerance = 10 * time.Second
	// restartNotice is shown to a signed-in user.
	restartNotice = "Fleeto restarts this computer in one minute for a task."
)

// The operating system is reached through these variables, so a test never restarts the machine.
var (
	readBootTime   = func() (time.Time, error) { t, err := host.BootTime(); return time.Unix(int64(t), 0), err }
	requestRestart = osRequestRestart
	cancelRestart  = osCancelRestart
)

// restartRecord is what the agent knew when it asked for the restart.
type restartRecord struct {
	BootTime    time.Time `json:"bootTime"`
	RequestedAt time.Time `json:"requestedAt"`
	Deadline    time.Time `json:"deadline"`
}

// RestartSupported reports whether this operating system restarts for a task.
func RestartSupported() bool { return runtime.GOOS == "windows" || runtime.GOOS == "linux" }

// runRestart records the boot time and the deadline, reports the start and asks the operating system to restart. The job then
// waits: when the agent stops for the restart it leaves the job open, and recover completes it after the start.
func (m *Manager) runRestart(id string, payload *agentv1.JobPayload) {
	dir := m.dir(id)
	boot, err := readBootTime()
	if err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START,
			Error: "The boot time of the endpoint cannot be read, so a restart could not be confirmed: " + err.Error()})
		return
	}
	now := m.opts.Now().UTC()
	record := restartRecord{BootTime: boot.UTC(), RequestedAt: now, Deadline: now.Add(Timeout(payload))}
	data, _ := json.Marshal(record)
	if err := platform.WriteFileAtomic(filepath.Join(dir, restartFile), data, m.opts.Access); err != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: "could not record the restart: " + err.Error()})
		return
	}
	if err := platform.WriteFileAtomic(filepath.Join(dir, startedFile), []byte(now.Format(time.RFC3339Nano)), m.opts.Access); err != nil {
		_ = os.Remove(filepath.Join(dir, restartFile))
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: "could not record the start: " + err.Error()})
		return
	}
	m.signal()
	if err := requestRestart(m.ctx); err != nil {
		_ = os.Remove(filepath.Join(dir, restartFile))
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_FAILED_TO_START, Error: "The endpoint could not be restarted: " + err.Error()})
		return
	}
	m.opts.Logger.Info("asked the operating system to restart the endpoint for a task", "jobId", id, "initiatedBy", payload.GetInitiatedBy(),
		"deadline", record.Deadline.Format(time.RFC3339))
	m.waitForRestart(id, record)
}

// waitForRestart ends the job when the deadline passes without a restart. When the agent stops, the job stays open.
func (m *Manager) waitForRestart(id string, record restartRecord) {
	timer := time.NewTimer(max(record.Deadline.Sub(m.opts.Now()), 0))
	defer timer.Stop()
	select {
	case <-m.ctx.Done():
		// Most likely the restart itself: recover completes the job after the start.
		return
	case <-timer.C:
	}
	if err := cancelRestart(); err != nil {
		m.opts.Logger.Warn("could not cancel the planned restart", "jobId", id, "error", err)
	}
	m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_TIMED_OUT,
		Error: fmt.Sprintf("The endpoint did not restart within %s after Fleeto asked for it. The planned restart was cancelled.",
			record.Deadline.Sub(record.RequestedAt).Round(time.Minute))})
}

// recoverRestart answers a restart job after the agent started again: done when the endpoint has a newer boot time, still waiting
// before the deadline (the agent restarted on its own), timed out after it. False when the job is no restart.
func (m *Manager) recoverRestart(id string) bool {
	data, err := os.ReadFile(filepath.Join(m.dir(id), restartFile))
	if err != nil {
		return false
	}
	var record restartRecord
	if json.Unmarshal(data, &record) != nil {
		m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_INTERRUPTED,
			Error: "The record of the restart cannot be read, so whether the endpoint restarted is unknown."})
		return true
	}
	if boot, err := readBootTime(); err == nil && boot.After(record.BootTime.Add(bootTolerance)) {
		line := fmt.Sprintf("The endpoint restarted at %s.\n", boot.UTC().Format(time.RFC3339))
		summary, err := m.writeLine(id, line)
		completion := &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_EXITED, ExitCode: 0, Stdout: summary}
		if err != nil {
			completion.Error = "Some output could not be stored on the endpoint: " + err.Error()
		}
		m.opts.Logger.Info("the endpoint restarted for a task", "jobId", id, "bootTime", boot.UTC().Format(time.RFC3339))
		m.complete(id, completion)
		return true
	}
	if m.opts.Now().Before(record.Deadline) {
		m.opts.Logger.Info("the agent started again before the endpoint restarted; still waiting for the restart", "jobId", id)
		m.wg.Add(1)
		safego.Go(m.opts.Logger, "restart "+id, func() {
			defer m.wg.Done()
			m.waitForRestart(id, record)
		})
		return true
	}
	m.complete(id, &agentv1.JobCompletion{Result: agentv1.JobResult_JOB_RESULT_TIMED_OUT,
		Error: "The agent started again, but the endpoint did not restart before the wait of the step ended."})
	return true
}

// writeLine stores one line as the job's only output chunk and returns its summary.
func (m *Manager) writeLine(id, line string) (*agentv1.JobStreamSummary, error) {
	data := []byte(line)
	if err := platform.WriteFileAtomic(filepath.Join(m.dir(id), chunkName(agentv1.JobStream_JOB_STREAM_STDOUT, 0)), data, m.opts.Access); err != nil {
		empty := sha256.Sum256(nil)
		return &agentv1.JobStreamSummary{Sha256: hex.EncodeToString(empty[:])}, err
	}
	sum := sha256.Sum256(data)
	return &agentv1.JobStreamSummary{Chunks: 1, Bytes: uint64(len(data)), Sha256: hex.EncodeToString(sum[:])}, nil
}
