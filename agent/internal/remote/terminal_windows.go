//go:build windows

package remote

import (
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"syscall"
	"time"
	"unicode/utf8"
	"unsafe"

	"golang.org/x/sys/windows"
)

var (
	kernel32                = windows.NewLazySystemDLL("kernel32.dll")
	procCreatePseudoConsole = kernel32.NewProc("CreatePseudoConsole")
)

// Shells lists the shells this endpoint offers, the default first.
func Shells() []string { return []string{"powershell", "cmd"} }

// PTYAvailable reports whether terminals run in a pseudo console: Windows 10 1809 and Server 2019 or newer. Older versions (Server 2016)
// get a terminal without one, with line input.
func PTYAvailable() bool { return procCreatePseudoConsole.Find() == nil }

// OpenTerminal starts a shell as the account of the service (SYSTEM).
func OpenTerminal(shell string, cols, rows int) (Terminal, error) {
	path, args, err := shellCommand(shell)
	if err != nil {
		return nil, err
	}
	if PTYAvailable() {
		return openConPTY(path, args, cols, rows)
	}
	return openPipes(path, args, shell)
}

func shellCommand(shell string) (string, string, error) {
	root := os.Getenv("SystemRoot")
	if root == "" {
		root = `C:\Windows`
	}
	switch strings.ToLower(shell) {
	case "powershell", "":
		path := filepath.Join(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
		return path, fmt.Sprintf(`"%s" -NoLogo -ExecutionPolicy Bypass`, path), nil
	case "cmd":
		path := filepath.Join(root, "System32", "cmd.exe")
		return path, fmt.Sprintf(`"%s"`, path), nil
	default:
		return "", "", ErrUnknownShell
	}
}

// jobObject kills the shell and every process it started when the terminal closes.
func jobObject() (windows.Handle, error) {
	job, err := windows.CreateJobObject(nil, nil)
	if err != nil {
		return 0, fmt.Errorf("create a job object: %w", err)
	}
	info := windows.JOBOBJECT_EXTENDED_LIMIT_INFORMATION{
		BasicLimitInformation: windows.JOBOBJECT_BASIC_LIMIT_INFORMATION{LimitFlags: windows.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE},
	}
	if _, err := windows.SetInformationJobObject(job, windows.JobObjectExtendedLimitInformation, uintptr(unsafe.Pointer(&info)),
		uint32(unsafe.Sizeof(info))); err != nil {
		_ = windows.CloseHandle(job)
		return 0, fmt.Errorf("configure the job object: %w", err)
	}
	return job, nil
}

// conPTY is a shell in a Windows pseudo console.
type conPTY struct {
	console windows.Handle
	process windows.Handle
	job     windows.Handle
	input   *os.File
	output  *os.File
	once    sync.Once
	mu      sync.Mutex
	closed  bool
	exited  chan struct{}
	code    int
	err     error
}

func openConPTY(path, cmdLine string, cols, rows int) (Terminal, error) {
	var inRead, inWrite, outRead, outWrite windows.Handle
	if err := windows.CreatePipe(&inRead, &inWrite, nil, 0); err != nil {
		return nil, fmt.Errorf("create the terminal input: %w", err)
	}
	if err := windows.CreatePipe(&outRead, &outWrite, nil, 0); err != nil {
		closeHandles(inRead, inWrite)
		return nil, fmt.Errorf("create the terminal output: %w", err)
	}
	var console windows.Handle
	size := windows.Coord{X: int16(cols), Y: int16(rows)}
	if err := windows.CreatePseudoConsole(size, inRead, outWrite, 0, &console); err != nil {
		closeHandles(inRead, inWrite, outRead, outWrite)
		return nil, fmt.Errorf("create the pseudo console: %w", err)
	}
	// The pseudo console holds its own copies.
	closeHandles(inRead, outWrite)

	job, err := jobObject()
	if err != nil {
		windows.ClosePseudoConsole(console)
		closeHandles(inWrite, outRead)
		return nil, err
	}
	attributes, err := windows.NewProcThreadAttributeList(1)
	if err != nil {
		windows.ClosePseudoConsole(console)
		closeHandles(inWrite, outRead, job)
		return nil, err
	}
	defer attributes.Delete()
	// The attribute value is the handle itself, not a pointer to it.
	if err := attributes.Update(windows.PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE, *(*unsafe.Pointer)(unsafe.Pointer(&console)), unsafe.Sizeof(console)); err != nil {
		windows.ClosePseudoConsole(console)
		closeHandles(inWrite, outRead, job)
		return nil, fmt.Errorf("attach the pseudo console: %w", err)
	}
	info := &windows.StartupInfoEx{ProcThreadAttributeList: attributes.List()}
	info.Cb = uint32(unsafe.Sizeof(*info))
	info.Flags = windows.STARTF_USESTDHANDLES
	pathPtr, err := windows.UTF16PtrFromString(path)
	if err != nil {
		windows.ClosePseudoConsole(console)
		closeHandles(inWrite, outRead, job)
		return nil, err
	}
	cmdPtr, err := windows.UTF16PtrFromString(cmdLine)
	if err != nil {
		windows.ClosePseudoConsole(console)
		closeHandles(inWrite, outRead, job)
		return nil, err
	}
	dir, _ := windows.UTF16PtrFromString(os.Getenv("SystemRoot") + `\System32`)
	var process windows.ProcessInformation
	flags := uint32(windows.EXTENDED_STARTUPINFO_PRESENT | windows.CREATE_UNICODE_ENVIRONMENT | windows.CREATE_SUSPENDED)
	if err := windows.CreateProcess(pathPtr, cmdPtr, nil, nil, false, flags, nil, dir, &info.StartupInfo, &process); err != nil {
		windows.ClosePseudoConsole(console)
		closeHandles(inWrite, outRead, job)
		return nil, fmt.Errorf("start the shell: %w", err)
	}
	// In the job before it runs, so nothing it starts escapes the job.
	_ = windows.AssignProcessToJobObject(job, process.Process)
	_, _ = windows.ResumeThread(process.Thread)
	_ = windows.CloseHandle(process.Thread)
	t := &conPTY{
		console: console,
		process: process.Process,
		job:     job,
		input:   os.NewFile(uintptr(inWrite), "terminal-input"),
		output:  os.NewFile(uintptr(outRead), "terminal-output"),
		exited:  make(chan struct{}),
	}
	// The output pipe only ends when the pseudo console closes, so close it as soon as the shell exits (typed exit). The process handle
	// stays open until Close, so Wait and the watcher never use a closed handle.
	go func() {
		_, _ = windows.WaitForSingleObject(t.process, windows.INFINITE)
		var code uint32
		if err := windows.GetExitCodeProcess(t.process, &code); err != nil {
			t.err = err
		}
		t.code = int(int32(code))
		close(t.exited)
		t.closeConsole()
	}()
	return t, nil
}

func (t *conPTY) closeConsole() {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.closed {
		return
	}
	t.closed = true
	// ClosePseudoConsole flushes the remaining output first; the reader keeps draining meanwhile.
	windows.ClosePseudoConsole(t.console)
}

func (t *conPTY) Read(p []byte) (int, error)  { return t.output.Read(p) }
func (t *conPTY) Write(p []byte) (int, error) { return t.input.Write(p) }
func (t *conPTY) PTY() bool                   { return true }

func (t *conPTY) Resize(cols, rows int) error {
	t.mu.Lock()
	defer t.mu.Unlock()
	if t.closed {
		return nil
	}
	return windows.ResizePseudoConsole(t.console, windows.Coord{X: int16(cols), Y: int16(rows)})
}

func (t *conPTY) Wait() (int, error) {
	<-t.exited
	return t.code, t.err
}

func (t *conPTY) Close() error {
	t.once.Do(func() {
		_ = windows.TerminateJobObject(t.job, 1)
		_ = t.input.Close()
		go func() {
			// Once the terminated shell is gone the watcher has closed the console; then the handles can go.
			select {
			case <-t.exited:
			case <-time.After(30 * time.Second):
				t.closeConsole()
			}
			_ = t.output.Close()
			closeHandles(t.job)
			select {
			case <-t.exited:
				closeHandles(t.process)
			default:
				// Still running after termination: keep the handle rather than race the watcher.
			}
		}()
	})
	return nil
}

// pipeTerminal is a shell with redirected standard streams, for Windows versions without a pseudo console. It has no terminal
// emulation: the browser edits the line and sends it with Enter, and output arrives in the console code page, converted to UTF-8.
type pipeTerminal struct {
	proc   *os.Process
	job    windows.Handle
	stdin  io.WriteCloser
	output *os.File
	once   sync.Once
	done   chan struct{}
	code   int
	err    error
	carry  []byte
}

func openPipes(path, cmdLine, shell string) (Terminal, error) {
	if strings.EqualFold(shell, "powershell") || shell == "" {
		// PowerShell reads commands from standard input line by line only with -Command -.
		cmdLine += " -NoProfile -Command -"
	} else {
		cmdLine += " /q"
	}
	job, err := jobObject()
	if err != nil {
		return nil, err
	}
	outRead, outWrite, err := os.Pipe()
	if err != nil {
		closeHandles(job)
		return nil, err
	}
	inRead, inWrite, err := os.Pipe()
	if err != nil {
		closeHandles(job)
		_ = outRead.Close()
		_ = outWrite.Close()
		return nil, err
	}
	attr := &os.ProcAttr{
		Dir:   filepath.Join(os.Getenv("SystemRoot"), "System32"),
		Env:   os.Environ(),
		Files: []*os.File{inRead, outWrite, outWrite},
		Sys:   &syscall.SysProcAttr{CmdLine: cmdLine, HideWindow: true, CreationFlags: windows.CREATE_NO_WINDOW},
	}
	proc, err := os.StartProcess(path, nil, attr)
	_ = inRead.Close()
	_ = outWrite.Close()
	if err != nil {
		closeHandles(job)
		_ = outRead.Close()
		_ = inWrite.Close()
		return nil, fmt.Errorf("start the shell: %w", err)
	}
	if handle, err := windows.OpenProcess(windows.PROCESS_SET_QUOTA|windows.PROCESS_TERMINATE, false, uint32(proc.Pid)); err == nil {
		_ = windows.AssignProcessToJobObject(job, handle)
		closeHandles(handle)
	}
	t := &pipeTerminal{proc: proc, job: job, stdin: inWrite, output: outRead, done: make(chan struct{})}
	go func() {
		state, err := proc.Wait()
		if err == nil {
			t.code = state.ExitCode()
		}
		t.err = err
		close(t.done)
	}()
	return t, nil
}

// Read returns output converted from the OEM code page to UTF-8.
func (t *pipeTerminal) Read(p []byte) (int, error) {
	if len(t.carry) > 0 {
		n := copy(p, t.carry)
		t.carry = t.carry[n:]
		return n, nil
	}
	raw := make([]byte, max(len(p)/3, 1))
	n, err := t.output.Read(raw)
	if n == 0 {
		return 0, err
	}
	text := oemToUTF8(raw[:n])
	copied := copy(p, text)
	t.carry = append(t.carry[:0], text[copied:]...)
	return copied, nil
}

// Write converts the browser's line endings: the browser sends a carriage return for Enter.
func (t *pipeTerminal) Write(p []byte) (int, error) {
	converted := strings.ReplaceAll(string(p), "\r", "\r\n")
	if _, err := t.stdin.Write(utf8ToOEM(converted)); err != nil {
		return 0, err
	}
	return len(p), nil
}

func (t *pipeTerminal) Resize(int, int) error { return nil }
func (t *pipeTerminal) PTY() bool             { return false }

func (t *pipeTerminal) Wait() (int, error) {
	<-t.done
	return t.code, t.err
}

func (t *pipeTerminal) Close() error {
	t.once.Do(func() {
		_ = windows.TerminateJobObject(t.job, 1)
		_ = t.stdin.Close()
		closeHandles(t.job)
		go func() {
			<-t.done
			_ = t.output.Close()
		}()
	})
	return nil
}

func oemToUTF8(raw []byte) []byte {
	if utf8.Valid(raw) && isASCII(raw) {
		return raw
	}
	count, err := windows.MultiByteToWideChar(cpOEM, 0, &raw[0], int32(len(raw)), nil, 0)
	if err != nil || count == 0 {
		return raw
	}
	wide := make([]uint16, count)
	if _, err := windows.MultiByteToWideChar(cpOEM, 0, &raw[0], int32(len(raw)), &wide[0], count); err != nil {
		return raw
	}
	return []byte(windows.UTF16ToString(wide))
}

var procWideCharToMultiByte = kernel32.NewProc("WideCharToMultiByte")

func utf8ToOEM(text string) []byte {
	if isASCII([]byte(text)) {
		return []byte(text)
	}
	wide, err := windows.UTF16FromString(text)
	if err != nil || len(wide) <= 1 {
		return []byte(text)
	}
	wide = wide[:len(wide)-1]
	count, _, _ := procWideCharToMultiByte.Call(uintptr(cpOEM), 0, uintptr(unsafe.Pointer(&wide[0])), uintptr(len(wide)), 0, 0, 0, 0)
	if count == 0 {
		return []byte(text)
	}
	out := make([]byte, count)
	written, _, _ := procWideCharToMultiByte.Call(uintptr(cpOEM), 0, uintptr(unsafe.Pointer(&wide[0])), uintptr(len(wide)),
		uintptr(unsafe.Pointer(&out[0])), count, 0, 0)
	if written == 0 {
		return []byte(text)
	}
	return out[:written]
}

func isASCII(b []byte) bool {
	for _, c := range b {
		if c >= 0x80 {
			return false
		}
	}
	return true
}

func closeHandles(handles ...windows.Handle) {
	for _, h := range handles {
		if h != 0 && h != windows.InvalidHandle {
			_ = windows.CloseHandle(h)
		}
	}
}

// cpOEM is CP_OEMCP: the console code page of the system.
const cpOEM = 1
