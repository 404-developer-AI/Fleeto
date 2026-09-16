//go:build !windows

package jobs

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"syscall"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// processTree runs the script in its own process group, so a timeout or stop ends every process the script started.
type processTree struct {
	pid int
}

func command(ctx context.Context, opts commandOptions) (scriptProcess, *processTree, error) {
	var interpreter string
	switch opts.language {
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL:
		interpreter = "/bin/sh"
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BASH:
		interpreter = "/bin/bash"
	default:
		return nil, nil, fmt.Errorf("%s scripts do not run on this operating system", LanguageName(opts.language))
	}
	tree := &processTree{}
	cmd := exec.CommandContext(ctx, interpreter, opts.scriptPath)
	cmd.Dir = opts.dir
	cmd.Stdout = opts.stdout
	cmd.Stderr = opts.stderr
	cmd.WaitDelay = opts.wait
	cmd.Cancel = tree.kill
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	if user := opts.session; user != nil {
		// The script drops to the user's own account and session; root's environment never reaches it.
		cmd.SysProcAttr.Credential = &syscall.Credential{Uid: user.uid, Gid: user.gid}
		cmd.Env = user.environment(opts.env)
	} else {
		cmd.Env = append(os.Environ(), opts.env...)
	}
	return execProcess{cmd: cmd}, tree, nil
}

// attach remembers the process so the whole group can be ended.
func (t *processTree) attach(pid int) { t.pid = pid }

// kill ends the whole process group.
func (t *processTree) kill() error {
	if t.pid == 0 {
		return nil
	}
	return syscall.Kill(-t.pid, syscall.SIGKILL)
}

func (t *processTree) close() {}
