//go:build linux

package patch

import (
	"bytes"
	"context"
	"errors"
	"log/slog"
	"os"
	"os/exec"
	"syscall"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Scan reads the missing updates with apt or dnf. It returns nil when the endpoint has neither.
//
// Priority: the Manager runs Scan on an OS thread locked to its goroutine whose CPU priority (nice 19) and I/O class (idle) it
// lowered. On Linux both are attributes of the thread, and a child forked from it inherits them; exec.Cmd forks from the calling
// thread, so apt-get, dnf and everything they start run at the lowest priority without nice or ionice.
func Scan(ctx context.Context, requestID string, _ ScanOptions, logger *slog.Logger) *agentv1.PatchScanReport {
	report := scanWith(ctx, linuxSystem{logger: logger})
	if report != nil {
		report.RequestId = requestID
	}
	return report
}

type linuxSystem struct {
	logger *slog.Logger
	// jobID and outputDir are those of an installation (install_linux.go); empty for a scan.
	jobID     string
	outputDir string
}

// cappedBuffer keeps at most maxOutputBytes of a command's output and drops the rest.
type cappedBuffer struct {
	bytes.Buffer
}

func (b *cappedBuffer) Write(p []byte) (int, error) {
	if room := maxOutputBytes - b.Len(); room > 0 {
		if len(p) > room {
			b.Buffer.Write(p[:room])
		} else {
			b.Buffer.Write(p)
		}
	}
	return len(p), nil
}

func (s linuxSystem) Run(ctx context.Context, env []string, name string, args ...string) cmdResult {
	path, err := exec.LookPath(name)
	if err != nil {
		return cmdResult{Code: -1, Err: err}
	}
	cmd := exec.CommandContext(ctx, path, args...) // #nosec G204 -- fixed commands; arguments are fixed or validated package names.
	// Later entries win, so the fixed locale overrides the agent's.
	cmd.Env = append(os.Environ(), env...)
	cmd.Stdin = nil
	var stdout, stderr cappedBuffer
	cmd.Stdout, cmd.Stderr = &stdout, &stderr
	// On a timeout apt and dnf get SIGTERM first, so they release their locks and clean up partial downloads; a scan never runs a
	// transaction, so stopping it is safe. SIGKILL follows after 30 seconds.
	cmd.Cancel = func() error { return cmd.Process.Signal(syscall.SIGTERM) }
	cmd.WaitDelay = 30 * time.Second
	err = cmd.Run()
	result := cmdResult{Stdout: stdout.String(), Stderr: stderr.String(), Code: cmd.ProcessState.ExitCode()}
	var exit *exec.ExitError
	switch {
	case ctx.Err() != nil:
		result.Err, result.Code = ctx.Err(), -1
	case err != nil && !errors.As(err, &exit):
		result.Err = err
	}
	if result.Err != nil {
		s.logger.Debug("a patch scan command did not finish", "command", name, "error", result.Err)
	}
	return result
}

func (linuxSystem) LookPath(name string) bool {
	_, err := exec.LookPath(name)
	return err == nil
}

func (linuxSystem) Exists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}

func (linuxSystem) ReadDirNames(path string) []string {
	entries, err := os.ReadDir(path)
	if err != nil {
		return nil
	}
	names := make([]string, 0, len(entries))
	for _, e := range entries {
		names = append(names, e.Name())
	}
	return names
}
