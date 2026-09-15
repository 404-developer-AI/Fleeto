//go:build !windows

package jobs

import (
	"context"
	"fmt"
	"os/exec"
	"syscall"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// processTree runs the script in its own process group, so a timeout or stop ends every process the script started.
type processTree struct {
	cmd *exec.Cmd
}

func command(ctx context.Context, language agentv1.ScriptLanguage, scriptPath, dir string) (*exec.Cmd, *processTree, error) {
	var interpreter string
	switch language {
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL:
		interpreter = "/bin/sh"
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BASH:
		interpreter = "/bin/bash"
	default:
		return nil, nil, fmt.Errorf("%s scripts do not run on this operating system", LanguageName(language))
	}
	cmd := exec.CommandContext(ctx, interpreter, scriptPath)
	cmd.Dir = dir
	cmd.SysProcAttr = &syscall.SysProcAttr{Setpgid: true}
	return cmd, &processTree{cmd: cmd}, nil
}

func (t *processTree) attach(*exec.Cmd) {}

// kill ends the whole process group.
func (t *processTree) kill() error {
	if t.cmd.Process == nil {
		return nil
	}
	return syscall.Kill(-t.cmd.Process.Pid, syscall.SIGKILL)
}

func (t *processTree) close() {}
