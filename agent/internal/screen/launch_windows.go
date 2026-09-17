//go:build windows

package screen

import (
	"bufio"
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// HelperCommand is the argument of fleeto-agent that runs the helper.
const HelperCommand = "remote-helper"

// helperDesktop is where the helper starts; it attaches itself to the input desktop right away.
const helperDesktop = `winsta0\default`

// SessionIDEnv tells the helper which Windows session it serves (for its Info frames and log lines only).
const SessionIDEnv = "FLEETO_REMOTE_SESSION"

// WindowsLauncher starts "fleeto-agent remote-helper" as SYSTEM in a Windows session: the agent service's own token, duplicated and moved
// to that session (needs SeTcbPrivilege, which SYSTEM has). The helper talks over anonymous pipes that only it inherits, so no other
// process can connect to it, and it lives in a job object that ends it when the agent does. Its log lines go to the agent's log.
func WindowsLauncher(logger *slog.Logger) Launcher {
	return func(ctx context.Context, sessionID uint32) (Helper, error) {
		if err := sessionExists(sessionID); err != nil {
			return nil, err
		}
		exe, err := os.Executable()
		if err != nil {
			return nil, fmt.Errorf("find the agent binary: %w", err)
		}
		token, err := sessionToken(sessionID)
		if err != nil {
			return nil, err
		}
		defer token.Close()

		inherit := &windows.SecurityAttributes{InheritHandle: 1}
		inherit.Length = uint32(unsafe.Sizeof(*inherit))
		var closeLater []windows.Handle
		closeAll := func() {
			for _, h := range closeLater {
				_ = windows.CloseHandle(h)
			}
		}
		stdinRead, stdinWrite, err := pipe(inherit, true)
		if err != nil {
			return nil, err
		}
		stdoutRead, stdoutWrite, err := pipe(inherit, false)
		if err != nil {
			_ = windows.CloseHandle(stdinRead)
			_ = windows.CloseHandle(stdinWrite)
			return nil, err
		}
		stderrRead, stderrWrite, err := pipe(inherit, false)
		if err != nil {
			for _, h := range []windows.Handle{stdinRead, stdinWrite, stdoutRead, stdoutWrite} {
				_ = windows.CloseHandle(h)
			}
			return nil, err
		}
		closeLater = append(closeLater, stdinRead, stdoutWrite, stderrWrite)

		// Only the three pipe ends are inherited, even when other code of the agent holds inheritable handles at this moment (a job's
		// output pipes).
		handles := []windows.Handle{stdinRead, stdoutWrite, stderrWrite}
		attributes, err := windows.NewProcThreadAttributeList(1)
		if err == nil {
			err = attributes.Update(windows.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, unsafe.Pointer(&handles[0]), uintptr(len(handles))*unsafe.Sizeof(handles[0]))
		}
		if err != nil {
			if attributes != nil {
				attributes.Delete()
			}
			closeAll()
			for _, h := range []windows.Handle{stdinWrite, stdoutRead, stderrRead} {
				_ = windows.CloseHandle(h)
			}
			return nil, fmt.Errorf("limit the helper's handles: %w", err)
		}
		startup := windows.StartupInfoEx{
			StartupInfo: windows.StartupInfo{
				Desktop:    windows.StringToUTF16Ptr(helperDesktop),
				Flags:      windows.STARTF_USESTDHANDLES | windows.STARTF_USESHOWWINDOW,
				ShowWindow: windows.SW_HIDE,
				StdInput:   stdinRead,
				StdOutput:  stdoutWrite,
				StdErr:     stderrWrite,
			},
			ProcThreadAttributeList: attributes.List(),
		}
		startup.Cb = uint32(unsafe.Sizeof(startup))
		commandLine := windows.ComposeCommandLine([]string{exe, HelperCommand})
		environment := environmentBlock([]string{
			SessionIDEnv + "=" + itoa(sessionID),
			"SystemRoot=" + os.Getenv("SystemRoot"),
			"SystemDrive=" + os.Getenv("SystemDrive"),
			"windir=" + os.Getenv("windir"),
			"TEMP=" + os.Getenv("TEMP"),
			"TMP=" + os.Getenv("TMP"),
		})
		var info windows.ProcessInformation
		err = windows.CreateProcessAsUser(token, windows.StringToUTF16Ptr(exe), windows.StringToUTF16Ptr(commandLine), nil, nil, true,
			windows.CREATE_NO_WINDOW|windows.CREATE_SUSPENDED|windows.CREATE_UNICODE_ENVIRONMENT|windows.EXTENDED_STARTUPINFO_PRESENT,
			&environment[0], nil, &startup.StartupInfo, &info)
		attributes.Delete()
		closeAll()
		if err != nil {
			_ = windows.CloseHandle(stdinWrite)
			_ = windows.CloseHandle(stdoutRead)
			_ = windows.CloseHandle(stderrRead)
			return nil, fmt.Errorf("start the helper in session %d: %w", sessionID, err)
		}
		job, err := killOnCloseJob()
		if err == nil {
			err = windows.AssignProcessToJobObject(job, info.Process)
		}
		if err != nil {
			_ = windows.TerminateProcess(info.Process, 1)
			_ = windows.CloseHandle(info.Thread)
			_ = windows.CloseHandle(info.Process)
			if job != 0 {
				_ = windows.CloseHandle(job)
			}
			_ = windows.CloseHandle(stdinWrite)
			_ = windows.CloseHandle(stdoutRead)
			_ = windows.CloseHandle(stderrRead)
			return nil, fmt.Errorf("contain the helper: %w", err)
		}
		if _, err := windows.ResumeThread(info.Thread); err != nil {
			_ = windows.TerminateJobObject(job, 1)
		}
		_ = windows.CloseHandle(info.Thread)

		h := &windowsHelper{
			in:      os.NewFile(uintptr(stdinWrite), "fleeto-helper-in"),
			out:     os.NewFile(uintptr(stdoutRead), "fleeto-helper-out"),
			process: info.Process,
			job:     job,
		}
		logs := os.NewFile(uintptr(stderrRead), "fleeto-helper-log")
		go func() {
			defer logs.Close()
			scanner := bufio.NewScanner(logs)
			for scanner.Scan() {
				logger.Info("remote control helper: "+scanner.Text(), "session", sessionID)
			}
		}()
		return h, nil
	}
}

type windowsHelper struct {
	in      *os.File
	out     *os.File
	process windows.Handle
	job     windows.Handle
	once    sync.Once
}

func (h *windowsHelper) In() io.Writer  { return h.in }
func (h *windowsHelper) Out() io.Reader { return h.out }

// Close closes the helper's input, which makes it release every key and button and exit, and ends it when it does not within 2 seconds.
func (h *windowsHelper) Close() error {
	h.once.Do(func() {
		_ = h.in.Close()
		if event, err := windows.WaitForSingleObject(h.process, uint32((2 * time.Second).Milliseconds())); err != nil || event != windows.WAIT_OBJECT_0 {
			_ = windows.TerminateJobObject(h.job, 1)
		}
		_ = h.out.Close()
		_ = windows.CloseHandle(h.process)
		_ = windows.CloseHandle(h.job)
	})
	return nil
}

// sessionToken is the service's own token (SYSTEM), as a primary token in the given Windows session.
func sessionToken(sessionID uint32) (windows.Token, error) {
	var own windows.Token
	if err := windows.OpenProcessToken(windows.CurrentProcess(),
		windows.TOKEN_DUPLICATE|windows.TOKEN_QUERY|windows.TOKEN_ASSIGN_PRIMARY|windows.TOKEN_ADJUST_DEFAULT|windows.TOKEN_ADJUST_SESSIONID, &own); err != nil {
		return 0, fmt.Errorf("open the service token: %w", err)
	}
	defer own.Close()
	var token windows.Token
	if err := windows.DuplicateTokenEx(own, windows.MAXIMUM_ALLOWED, nil, windows.SecurityImpersonation, windows.TokenPrimary, &token); err != nil {
		return 0, fmt.Errorf("duplicate the service token: %w", err)
	}
	id := sessionID
	if err := windows.SetTokenInformation(token, windows.TokenSessionId, (*byte)(unsafe.Pointer(&id)), uint32(unsafe.Sizeof(id))); err != nil {
		token.Close()
		return 0, fmt.Errorf("move the token to session %d (the agent must run as SYSTEM): %w", sessionID, err)
	}
	return token, nil
}

// pipe creates an anonymous pipe. childReads says which end the child inherits; the other end is not inheritable.
func pipe(inherit *windows.SecurityAttributes, childReads bool) (read, write windows.Handle, err error) {
	if err := windows.CreatePipe(&read, &write, inherit, 0); err != nil {
		return 0, 0, fmt.Errorf("create a helper pipe: %w", err)
	}
	parentEnd := read
	if childReads {
		parentEnd = write
	}
	if err := windows.SetHandleInformation(parentEnd, windows.HANDLE_FLAG_INHERIT, 0); err != nil {
		_ = windows.CloseHandle(read)
		_ = windows.CloseHandle(write)
		return 0, 0, fmt.Errorf("protect a helper pipe: %w", err)
	}
	return read, write, nil
}

// killOnCloseJob is a job object that ends its processes when its last handle closes, so a helper never outlives the agent.
func killOnCloseJob() (windows.Handle, error) {
	job, err := windows.CreateJobObject(nil, nil)
	if err != nil {
		return 0, err
	}
	limits := windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION{}
	limits.BasicLimitInformation.LimitFlags = windows.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
	if _, err := windows.SetInformationJobObject(job, windows.JobObjectExtendedLimitInformation, uintptr(unsafe.Pointer(&limits)),
		uint32(unsafe.Sizeof(limits))); err != nil {
		_ = windows.CloseHandle(job)
		return 0, err
	}
	return job, nil
}

// sessionExists fails with a clear message when a Windows session is gone (signed out since the list was reported).
func sessionExists(sessionID uint32) error {
	var sessions *windows.WTS_SESSION_INFO
	var count uint32
	if err := windows.WTSEnumerateSessions(0, 0, 1, &sessions, &count); err != nil {
		return nil // cannot tell; the start itself will say what is wrong
	}
	defer windows.WTSFreeMemory(uintptr(unsafe.Pointer(sessions)))
	for _, s := range unsafe.Slice(sessions, count) {
		if s.SessionID == sessionID {
			return nil
		}
	}
	return fmt.Errorf("Windows session %d does not exist any more (the user signed out)", sessionID)
}

// ConsoleSession is the Windows session attached to the console.
func ConsoleSession() uint32 { return windows.WTSGetActiveConsoleSessionId() }

// SessionExists reports whether a Windows session is still there (the user did not sign out).
func SessionExists(session uint32) bool { return sessionExists(session) == nil }

const sasPolicyKey = `SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System`

// SecureAttention sends Ctrl+Alt+Del to the console the way the sign-in screen expects it (SendSAS). Windows accepts that from a service
// only when SoftwareSASGeneration allows services (1 or 3); the agent sets it when it is missing, and a group policy that sets it
// otherwise wins.
func SecureAttention() error {
	key, err := registry.OpenKey(registry.LOCAL_MACHINE, sasPolicyKey, registry.QUERY_VALUE)
	if err == nil {
		value, _, readErr := key.GetIntegerValue("SoftwareSASGeneration")
		_ = key.Close()
		if readErr == nil && value != 1 && value != 3 {
			return errors.New("Ctrl+Alt+Del is turned off on this endpoint: the policy \"Disable or enable software Secure Attention Sequence\" " +
				"(SoftwareSASGeneration) does not allow services. Change that group policy to Services to use it.")
		}
	}
	if err := procSendSAS.Find(); err != nil {
		return errors.New("Ctrl+Alt+Del cannot be sent: this Windows version has no sas.dll.")
	}
	procSendSAS.Call(0)
	return nil
}

// EnableSoftwareSAS sets SoftwareSASGeneration to 1 (services may send Ctrl+Alt+Del) when the value is missing, so the Ctrl+Alt+Del
// button of remote control works. An existing value, set by an administrator or a group policy, is left alone. Called when the agent
// service starts, which covers install and update.
func EnableSoftwareSAS() (changed bool, err error) {
	key, _, err := registry.CreateKey(registry.LOCAL_MACHINE, sasPolicyKey, registry.QUERY_VALUE|registry.SET_VALUE)
	if err != nil {
		return false, err
	}
	defer key.Close()
	if _, _, err := key.GetIntegerValue("SoftwareSASGeneration"); err == nil {
		return false, nil
	} else if !errors.Is(err, registry.ErrNotExist) {
		return false, err
	}
	return true, key.SetDWordValue("SoftwareSASGeneration", 1)
}

// Supported reports whether this platform serves remote control.
func Supported() bool { return true }

// environmentBlock builds a UTF-16 environment block for CreateProcessAsUser: each "NAME=value" ends in a NUL, and the whole block ends
// in a second NUL. It never passes a string with an embedded NUL to the UTF-16 conversion (that panics); a variable that cannot be
// converted (an unexpected NUL in a value) is skipped.
func environmentBlock(vars []string) []uint16 {
	var block []uint16
	for _, v := range vars {
		encoded, err := windows.UTF16FromString(v)
		if err != nil {
			continue
		}
		block = append(block, encoded...) // encoded already ends in a NUL
	}
	return append(block, 0)
}
