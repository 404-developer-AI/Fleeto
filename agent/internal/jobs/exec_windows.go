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

// interpreter is the program that runs one script and the command line it is started with.
type interpreter struct {
	path    string
	cmdLine string
}

func interpreterFor(language agentv1.ScriptLanguage, scriptPath string) (interpreter, error) {
	systemRoot := os.Getenv("SystemRoot")
	if systemRoot == "" {
		systemRoot = `C:\Windows`
	}
	switch language {
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL:
		path := filepath.Join(systemRoot, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
		return interpreter{path: path,
			cmdLine: fmt.Sprintf(`"%s" -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass -File "%s"`, path, scriptPath)}, nil
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH:
		path := filepath.Join(systemRoot, "System32", "cmd.exe")
		// cmd.exe parses its own command line: /s strips the outer quotes, so a path with spaces stays one argument.
		return interpreter{path: path, cmdLine: fmt.Sprintf(`"%s" /d /s /c ""%s""`, path, scriptPath)}, nil
	default:
		return interpreter{}, fmt.Errorf("%s scripts do not run on Windows", LanguageName(language))
	}
}

func command(ctx context.Context, opts commandOptions) (scriptProcess, *processTree, error) {
	run, err := interpreterFor(opts.language, opts.scriptPath)
	if err != nil {
		return nil, nil, err
	}

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
	tree := &processTree{job: job}

	if opts.session != nil {
		proc, err := newUserProcess(ctx, run, opts, tree)
		if err != nil {
			tree.close()
			return nil, nil, err
		}
		return proc, tree, nil
	}

	cmd := exec.CommandContext(ctx, run.path)
	cmd.SysProcAttr = &syscall.SysProcAttr{CmdLine: run.cmdLine, HideWindow: true, CreationFlags: windows.CREATE_NO_WINDOW}
	cmd.Dir = opts.dir
	cmd.Stdout = opts.stdout
	cmd.Stderr = opts.stderr
	cmd.Env = append(os.Environ(), opts.env...)
	cmd.WaitDelay = opts.wait
	cmd.Cancel = tree.kill
	return execProcess{cmd: cmd}, tree, nil
}

// attach adds the started process to the job object; processes it starts from then on belong to the job too.
func (t *processTree) attach(pid int) {
	if pid == 0 {
		return
	}
	process, err := windows.OpenProcess(windows.PROCESS_SET_QUOTA|windows.PROCESS_TERMINATE, false, uint32(pid))
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
