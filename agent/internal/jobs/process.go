package jobs

import (
	"errors"
	"io"
	"os/exec"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// ErrNoUserSignedIn is returned when a job asks to run as the signed-in user and nobody is signed in. The job fails with
// this reason instead of waiting: the technician sees it and can start it again later.
var ErrNoUserSignedIn = errors.New("no user is signed in on this endpoint")

// scriptProcess is one running script. Running as the agent's own account is an os/exec command; running in the session of
// the signed-in user needs process creation os/exec cannot express on Windows, so both hide behind this.
type scriptProcess interface {
	Start() error
	// Wait returns when the script ended and its output was read, or when waitDelay passed after the script ended.
	Wait() error
	// ExitCode is valid after Wait; -1 when the script never ended on its own.
	ExitCode() int
	Pid() int
}

// commandOptions is everything a platform needs to start one script.
type commandOptions struct {
	language   agentv1.ScriptLanguage
	scriptPath string
	// dir is the working directory for a script that runs as the agent's own account; a script that runs as the
	// signed-in user gets that user's home directory instead.
	dir    string
	env    []string
	stdout io.Writer
	stderr io.Writer
	// wait is how long the output may stay open after the script itself ended.
	wait time.Duration
	// session is the signed-in user the script runs as; nil runs it as SYSTEM or root. Its type is platform-specific.
	session *signedInUser
}

// execProcess is the os/exec side of scriptProcess.
type execProcess struct{ cmd *exec.Cmd }

func (p execProcess) Start() error { return p.cmd.Start() }

func (p execProcess) Wait() error { return p.cmd.Wait() }

func (p execProcess) ExitCode() int {
	if p.cmd.ProcessState == nil {
		return -1
	}
	return p.cmd.ProcessState.ExitCode()
}

func (p execProcess) Pid() int {
	if p.cmd.Process == nil {
		return 0
	}
	return p.cmd.Process.Pid
}
