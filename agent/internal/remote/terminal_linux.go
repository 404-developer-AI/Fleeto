//go:build linux

package remote

import (
	"errors"
	"fmt"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"

	"golang.org/x/sys/unix"
)

// Shells lists the shells this endpoint offers: the root shell, bash when it is installed.
func Shells() []string {
	if _, err := os.Stat("/bin/bash"); err == nil {
		return []string{"bash"}
	}
	return []string{"sh"}
}

// PTYAvailable reports whether terminals run in a pseudo terminal; always on Linux.
func PTYAvailable() bool { return true }

// ptyTerminal is a login shell as root in a pseudo terminal, in its own session so closing it ends every process it started.
type ptyTerminal struct {
	master *os.File
	cmd    *exec.Cmd
	once   sync.Once
	done   chan struct{}
	code   int
	err    error
}

// OpenTerminal starts the root shell in a pseudo terminal.
func OpenTerminal(shell string, cols, rows int) (Terminal, error) {
	path := "/bin/sh"
	switch strings.ToLower(shell) {
	case "bash":
		path = "/bin/bash"
	case "sh", "":
	default:
		return nil, ErrUnknownShell
	}
	if _, err := os.Stat(path); err != nil {
		return nil, fmt.Errorf("%s is not installed on this endpoint", path)
	}

	master, err := os.OpenFile("/dev/ptmx", os.O_RDWR|unix.O_NOCTTY|unix.O_CLOEXEC, 0)
	if err != nil {
		return nil, fmt.Errorf("open a pseudo terminal: %w", err)
	}
	fd := int(master.Fd())
	if err := unix.IoctlSetPointerInt(fd, unix.TIOCSPTLCK, 0); err != nil {
		_ = master.Close()
		return nil, fmt.Errorf("unlock the pseudo terminal: %w", err)
	}
	number, err := unix.IoctlGetInt(fd, unix.TIOCGPTN)
	if err != nil {
		_ = master.Close()
		return nil, fmt.Errorf("name the pseudo terminal: %w", err)
	}
	slave, err := os.OpenFile("/dev/pts/"+strconv.Itoa(number), os.O_RDWR|unix.O_NOCTTY, 0)
	if err != nil {
		_ = master.Close()
		return nil, fmt.Errorf("open the pseudo terminal: %w", err)
	}
	_ = unix.IoctlSetWinsize(fd, unix.TIOCSWINSZ, &unix.Winsize{Col: uint16(cols), Row: uint16(rows)})

	cmd := exec.Command(path, "-l")
	cmd.Dir = "/root"
	if unix.Access(cmd.Dir, unix.X_OK) != nil {
		cmd.Dir = "/"
	}
	cmd.Env = []string{
		"TERM=xterm-256color",
		"HOME=" + cmd.Dir,
		"USER=root",
		"LOGNAME=root",
		"SHELL=" + path,
		"LANG=" + firstNonEmpty(os.Getenv("LANG"), "C.UTF-8"),
		"PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin",
	}
	cmd.Stdin, cmd.Stdout, cmd.Stderr = slave, slave, slave
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true, Setctty: true, Ctty: 0}
	if err := cmd.Start(); err != nil {
		_ = slave.Close()
		_ = master.Close()
		return nil, fmt.Errorf("start the shell: %w", err)
	}
	_ = slave.Close()

	t := &ptyTerminal{master: master, cmd: cmd, done: make(chan struct{})}
	go func() {
		err := cmd.Wait()
		var exit *exec.ExitError
		switch {
		case err == nil:
		case errors.As(err, &exit):
			t.code = exit.ExitCode()
		default:
			t.err = err
		}
		close(t.done)
	}()
	return t, nil
}

func (t *ptyTerminal) Read(p []byte) (int, error) {
	n, err := t.master.Read(p)
	// The master returns EIO once the shell and every process holding the terminal are gone: that is the end of the output.
	if err != nil && errors.Is(err, syscall.EIO) {
		return n, errors.New("the terminal ended")
	}
	return n, err
}

func (t *ptyTerminal) Write(p []byte) (int, error) { return t.master.Write(p) }
func (t *ptyTerminal) PTY() bool                   { return true }

func (t *ptyTerminal) Resize(cols, rows int) error {
	return unix.IoctlSetWinsize(int(t.master.Fd()), unix.TIOCSWINSZ, &unix.Winsize{Col: uint16(cols), Row: uint16(rows)})
}

func (t *ptyTerminal) Wait() (int, error) {
	<-t.done
	return t.code, t.err
}

// Close hangs up the session of the shell, then kills whatever is left of it.
func (t *ptyTerminal) Close() error {
	t.once.Do(func() {
		if pid := t.cmd.Process.Pid; pid > 0 {
			_ = syscall.Kill(-pid, syscall.SIGHUP)
			go func() {
				select {
				case <-t.done:
				case <-time.After(3 * time.Second):
					_ = syscall.Kill(-pid, syscall.SIGKILL)
					<-t.done
				}
				_ = t.master.Close()
			}()
		}
	})
	return nil
}

func firstNonEmpty(values ...string) string {
	for _, v := range values {
		if v != "" {
			return v
		}
	}
	return ""
}
