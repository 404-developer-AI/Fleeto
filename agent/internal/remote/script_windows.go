//go:build windows

package remote

import (
	"fmt"
	"os"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"

	"github.com/404-developer-AI/Fleeto/agent/internal/jobs"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

// OpenScript runs library scripts staged under dir (0.6.0). As SYSTEM a script gets a pseudo console like the terminal (redirected
// streams on Server 2016); as the signed-in user it runs in that user's session with redirected streams, the way a job as the user
// does, so the browser sends input line by line.
func OpenScript(dir string, access platform.Access) ScriptOpener {
	return func(request ScriptRequest, cols, rows int) (Terminal, string, error) {
		script, err := request.Job()
		if err != nil {
			return nil, "", err
		}
		staged, err := jobs.StageInteractive(script, dir, access, request.RunAs == RunAsUser, request.User)
		if err != nil {
			return nil, "", err
		}
		path, cmdLine, err := staged.CommandLine()
		if err != nil {
			staged.Release()
			return nil, "", err
		}
		var terminal Terminal
		switch {
		case staged.Token() != 0:
			terminal, err = openUserPipes(staged, cmdLine)
		case PTYAvailable():
			terminal, err = openConPTYIn(path, cmdLine, staged.Dir, cols, rows)
		default:
			terminal, err = openPipesIn(path, cmdLine, staged.Dir)
		}
		if err != nil {
			staged.Release()
			return nil, "", err
		}
		return withRelease(terminal, staged.Release), staged.Account, nil
	}
}

// openUserPipes starts the staged script with the signed-in user's token in their session, with its input and output redirected to
// pipes and in a job object, so closing the terminal ends everything it started.
func openUserPipes(staged *jobs.Interactive, cmdLine string) (Terminal, error) {
	environment, err := staged.Environment()
	if err != nil {
		return nil, fmt.Errorf("read the environment of the signed-in user: %w", err)
	}
	job, err := jobObject()
	if err != nil {
		return nil, err
	}

	// No other process may be created while the child's ends are inheritable, or it would inherit them too and hold the output open.
	syscall.ForkLock.Lock()
	inherit := &windows.SecurityAttributes{InheritHandle: 1}
	inherit.Length = uint32(unsafe.Sizeof(*inherit))
	var inRead, inWrite, outRead, outWrite windows.Handle
	if err := windows.CreatePipe(&inRead, &inWrite, inherit, 0); err != nil {
		syscall.ForkLock.Unlock()
		closeHandles(job)
		return nil, fmt.Errorf("create the script input: %w", err)
	}
	if err := windows.CreatePipe(&outRead, &outWrite, inherit, 0); err != nil {
		syscall.ForkLock.Unlock()
		closeHandles(job, inRead, inWrite)
		return nil, fmt.Errorf("create the script output: %w", err)
	}
	// Only the child's ends are inherited.
	_ = windows.SetHandleInformation(inWrite, windows.HANDLE_FLAG_INHERIT, 0)
	_ = windows.SetHandleInformation(outRead, windows.HANDLE_FLAG_INHERIT, 0)

	startup := windows.StartupInfo{
		Desktop:    windows.StringToUTF16Ptr(jobs.InteractiveDesktop),
		Flags:      windows.STARTF_USESTDHANDLES | windows.STARTF_USESHOWWINDOW,
		ShowWindow: windows.SW_HIDE,
		StdInput:   inRead,
		StdOutput:  outWrite,
		StdErr:     outWrite,
	}
	startup.Cb = uint32(unsafe.Sizeof(startup))
	var information windows.ProcessInformation
	flags := uint32(windows.CREATE_UNICODE_ENVIRONMENT | windows.CREATE_NO_WINDOW | windows.CREATE_SUSPENDED)
	err = windows.CreateProcessAsUser(staged.Token(), nil, windows.StringToUTF16Ptr(cmdLine), nil, nil, true, flags, environment,
		windows.StringToUTF16Ptr(staged.Dir), &startup, &information)
	// The child owns its ends now; keeping ours open would hide the end of the output.
	closeHandles(inRead, outWrite)
	syscall.ForkLock.Unlock()
	if err != nil {
		closeHandles(job, inWrite, outRead)
		return nil, fmt.Errorf("start the script as %s: %w", staged.Account, err)
	}
	// In the job before it runs, so nothing it starts escapes the job.
	_ = windows.AssignProcessToJobObject(job, information.Process)
	_, _ = windows.ResumeThread(information.Thread)
	closeHandles(information.Thread)

	t := &pipeTerminal{job: job, stdin: os.NewFile(uintptr(inWrite), "script-input"), output: os.NewFile(uintptr(outRead), "script-output"),
		done: make(chan struct{})}
	go func() {
		defer closeHandles(information.Process)
		if _, err := windows.WaitForSingleObject(information.Process, windows.INFINITE); err != nil {
			t.err = err
		} else {
			var code uint32
			if err := windows.GetExitCodeProcess(information.Process, &code); err != nil {
				t.err = err
			}
			t.code = int(int32(code))
		}
		close(t.done)
	}()
	return t, nil
}
