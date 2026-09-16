//go:build windows

package jobs

import (
	"context"
	"fmt"
	"io"
	"os"
	"os/exec"
	"sync"
	"time"
	"unicode/utf16"
	"unsafe"

	"golang.org/x/sys/windows"
)

// interactiveDesktop is the window station and desktop of a signed-in user. A service creating a process in a user's
// session must name it: its own desktop does not exist in that session, so leaving it empty fails.
const interactiveDesktop = `winsta0\default`

// userProcess runs a script in the session of the signed-in user. os/exec cannot do this: it cannot name a desktop, so
// the process is created with CreateProcessAsUser here, with pipes for the two output streams.
type userProcess struct {
	ctx  context.Context
	run  interpreter
	opts commandOptions
	tree *processTree

	handle  windows.Handle
	pid     int
	exit    int
	copying sync.WaitGroup
	closers []windows.Handle
}

func newUserProcess(ctx context.Context, run interpreter, opts commandOptions, tree *processTree) (*userProcess, error) {
	return &userProcess{ctx: ctx, run: run, opts: opts, tree: tree, exit: -1}, nil
}

func (p *userProcess) Pid() int { return p.pid }

func (p *userProcess) ExitCode() int { return p.exit }

func (p *userProcess) Start() error {
	token := p.opts.session.token
	environment, err := environmentBlock(token, p.opts.env)
	if err != nil {
		return fmt.Errorf("read the environment of the signed-in user: %w", err)
	}

	inherit := &windows.SecurityAttributes{InheritHandle: 1}
	inherit.Length = uint32(unsafe.Sizeof(*inherit))

	stdout, err := p.pipe(inherit, p.opts.stdout)
	if err != nil {
		return err
	}
	stderr, err := p.pipe(inherit, p.opts.stderr)
	if err != nil {
		p.closeHandles()
		return err
	}
	// A script that reads input gets end of file at once instead of waiting forever.
	stdin, err := windows.CreateFile(windows.StringToUTF16Ptr("NUL"), windows.GENERIC_READ,
		windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE, inherit, windows.OPEN_EXISTING, 0, 0)
	if err != nil {
		p.closeHandles()
		return fmt.Errorf("open NUL for the script's input: %w", err)
	}
	p.closers = append(p.closers, stdin)

	startup := windows.StartupInfo{
		Desktop:    windows.StringToUTF16Ptr(interactiveDesktop),
		Flags:      windows.STARTF_USESTDHANDLES | windows.STARTF_USESHOWWINDOW,
		ShowWindow: windows.SW_HIDE,
		StdInput:   stdin,
		StdOutput:  stdout,
		StdErr:     stderr,
	}
	startup.Cb = uint32(unsafe.Sizeof(startup))

	var directory *uint16
	if p.opts.dir != "" {
		directory = windows.StringToUTF16Ptr(p.opts.dir)
	}
	var information windows.ProcessInformation
	err = windows.CreateProcessAsUser(token, nil, windows.StringToUTF16Ptr(p.run.cmdLine), nil, nil, true,
		windows.CREATE_UNICODE_ENVIRONMENT|windows.CREATE_NO_WINDOW, environment, directory, &startup, &information)
	// The child owns its ends of the pipes now; keeping ours open would hide the end of the output.
	p.closeHandles()
	if err != nil {
		p.copying.Wait()
		return err
	}
	_ = windows.CloseHandle(information.Thread)
	p.handle = information.Process
	p.pid = int(information.ProcessId)

	go func() {
		<-p.ctx.Done()
		_ = p.tree.kill()
	}()
	return nil
}

// pipe makes one output pipe: the child writes into it, a goroutine copies what comes out to the job's stream.
func (p *userProcess) pipe(inherit *windows.SecurityAttributes, to io.Writer) (windows.Handle, error) {
	var read, write windows.Handle
	if err := windows.CreatePipe(&read, &write, inherit, 0); err != nil {
		return 0, fmt.Errorf("create an output pipe: %w", err)
	}
	// Only the write end belongs to the child.
	if err := windows.SetHandleInformation(read, windows.HANDLE_FLAG_INHERIT, 0); err != nil {
		_ = windows.CloseHandle(read)
		_ = windows.CloseHandle(write)
		return 0, fmt.Errorf("protect an output pipe: %w", err)
	}
	p.closers = append(p.closers, write)

	p.copying.Add(1)
	go func() {
		defer p.copying.Done()
		file := os.NewFile(uintptr(read), "fleeto-job-output")
		defer file.Close()
		_, _ = io.Copy(to, file)
	}()
	return write, nil
}

// closeHandles closes what only the parent still holds: the write ends of the pipes and the input handle.
func (p *userProcess) closeHandles() {
	for _, handle := range p.closers {
		_ = windows.CloseHandle(handle)
	}
	p.closers = nil
}

func (p *userProcess) Wait() error {
	defer func() {
		if p.handle != 0 {
			_ = windows.CloseHandle(p.handle)
			p.handle = 0
		}
	}()
	if _, err := windows.WaitForSingleObject(p.handle, windows.INFINITE); err != nil {
		return err
	}
	var code uint32
	if err := windows.GetExitCodeProcess(p.handle, &code); err != nil {
		return err
	}
	p.exit = int(int32(code))

	// The script ended; give whatever it started a moment to close the output, then stop waiting for it.
	done := make(chan struct{})
	go func() {
		p.copying.Wait()
		close(done)
	}()
	select {
	case <-done:
		return nil
	case <-time.After(p.opts.wait):
		return exec.ErrWaitDelay
	}
}

// environmentBlock is the signed-in user's own environment with the job's variables added. CreateProcessAsUser wants one
// block of UTF-16 strings, each ending in a zero, the whole ending in another zero.
func environmentBlock(token windows.Token, extra []string) (*uint16, error) {
	var block *uint16
	if err := windows.CreateEnvironmentBlock(&block, token, false); err != nil {
		return nil, err
	}
	defer windows.DestroyEnvironmentBlock(block)

	var entries []string
	for pointer := block; ; {
		entry := windows.UTF16PtrToString(pointer)
		if entry == "" {
			break
		}
		entries = append(entries, entry)
		pointer = (*uint16)(unsafe.Add(unsafe.Pointer(pointer), (len(utf16.Encode([]rune(entry)))+1)*2))
	}
	entries = append(entries, extra...)

	var flat []uint16
	for _, entry := range entries {
		encoded, err := windows.UTF16FromString(entry)
		if err != nil {
			return nil, err
		}
		flat = append(flat, encoded...)
	}
	flat = append(flat, 0)
	return &flat[0], nil
}
