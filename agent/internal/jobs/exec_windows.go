//go:build windows

package jobs

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// processTree puts the script's process in a job object, so a timeout or stop ends every process the script started.
type processTree struct {
	job windows.Handle
}

func command(ctx context.Context, language agentv1.ScriptLanguage, scriptPath, dir string) (*exec.Cmd, *processTree, error) {
	systemRoot := os.Getenv("SystemRoot")
	if systemRoot == "" {
		systemRoot = `C:\Windows`
	}
	var cmd *exec.Cmd
	switch language {
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL:
		powershell := filepath.Join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
		cmd = exec.CommandContext(ctx, powershell, "-NoLogo", "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", scriptPath)
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH:
		cmdExe := filepath.Join(systemRoot, "System32", "cmd.exe")
		cmd = exec.CommandContext(ctx, cmdExe)
		// cmd.exe parses its own command line: /s strips the outer quotes, so a path with spaces stays one argument.
		cmd.SysProcAttr = &syscall.SysProcAttr{CmdLine: fmt.Sprintf(`"%s" /d /s /c ""%s""`, cmdExe, scriptPath)}
	default:
		return nil, nil, fmt.Errorf("%s scripts do not run on Windows", LanguageName(language))
	}
	if cmd.SysProcAttr == nil {
		cmd.SysProcAttr = &syscall.SysProcAttr{}
	}
	cmd.SysProcAttr.HideWindow = true
	cmd.SysProcAttr.CreationFlags |= windows.CREATE_NO_WINDOW
	cmd.Dir = dir

	job, err := windows.CreateJobObject(nil, nil)
	if err != nil {
		return nil, nil, fmt.Errorf("create a job object: %w", err)
	}
	info := windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION{
		BasicLimitInformation: windows.JOBOBJECT_BASIC_LIMIT_INFORMATION{LimitFlags: windows.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE},
	}
	if _, err := windows.SetInformationJobObject(job, windows.JobObjectExtendedLimitInformation, uintptr(unsafe.Pointer(&info)),
		uint32(unsafe.Sizeof(info))); err != nil {
		_ = windows.CloseHandle(job)
		return nil, nil, fmt.Errorf("configure the job object: %w", err)
	}
	return cmd, &processTree{job: job}, nil
}

// attach adds the started process to the job object; processes it starts from then on belong to the job too.
func (t *processTree) attach(cmd *exec.Cmd) {
	process, err := windows.OpenProcess(windows.PROCESS_SET_QUOTA|windows.PROCESS_TERMINATE, false, uint32(cmd.Process.Pid))
	if err != nil {
		return
	}
	defer windows.CloseHandle(process)
	_ = windows.AssignProcessToJobObject(t.job, process)
}

// kill ends every process in the job.
func (t *processTree) kill() error {
	return windows.TerminateJobObject(t.job, 1)
}

func (t *processTree) close() {
	if t != nil && t.job != 0 {
		_ = windows.CloseHandle(t.job)
		t.job = 0
	}
}
