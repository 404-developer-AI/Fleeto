//go:build linux

package screen

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net"
	"os"
	"strconv"
	"syscall"
	"time"

	"github.com/jezek/xgb"
	"golang.org/x/sys/unix"
)

// The start of every Linux child of remote control (helper, clipboard, consent): read FrameX11, drop from root to the account it names,
// then connect to the X display with the cookie. Nothing talks to the X server before the privileges are gone.

func init() {
	// xgb logs to stderr by default; the children's stderr is the agent's log, which gets only what the children say themselves.
	xgb.Logger = log.New(io.Discard, "", 0)
}

// startX11Child reads the first frame, drops privileges and connects to the display.
func startX11Child(in io.Reader) (*xgb.Conn, X11Body, error) {
	frame, err := ReadFrame(in)
	if err != nil {
		return nil, X11Body{}, err
	}
	if frame[0] != FrameX11 {
		return nil, X11Body{}, errors.New("the first frame does not name the display")
	}
	var body X11Body
	if err := json.Unmarshal(frame[1:], &body); err != nil {
		return nil, X11Body{}, err
	}
	if err := dropPrivileges(body.UID, body.GID, body.Groups); err != nil {
		return nil, body, err
	}
	conn, err := dialDisplay(body.Display, body.Cookie, body.ServerPID, body.ServerUIDs)
	if err != nil {
		return nil, body, err
	}
	return conn, body, nil
}

// dropPrivileges switches the whole process (every thread) to the account. A child that does not run as root keeps its account only when
// it is the one asked for.
func dropPrivileges(uid, gid uint32, groups []uint32) error {
	if os.Geteuid() != 0 {
		if uint32(os.Geteuid()) == uid {
			return nil
		}
		return errors.New("the remote control process does not run as root and cannot switch to its account")
	}
	if uid == 0 {
		return errors.New("a remote control process must not keep running as root")
	}
	list := []int{int(gid)}
	for _, g := range groups {
		if g != gid {
			list = append(list, int(g))
		}
	}
	if err := syscall.Setgroups(list); err != nil {
		return fmt.Errorf("drop the groups: %w", err)
	}
	if err := syscall.Setgid(int(gid)); err != nil {
		return fmt.Errorf("switch the group: %w", err)
	}
	if err := syscall.Setuid(int(uid)); err != nil {
		return fmt.Errorf("switch the user: %w", err)
	}
	if os.Geteuid() == 0 || os.Getuid() == 0 {
		return errors.New("the process still runs as root")
	}
	return nil
}

// dialDisplay connects to a local display through its Unix socket (the file, then the abstract socket some servers use only), and talks to
// it only when the process at the other end is the X server the agent found, or one of root or the session's user: a user who put a
// server of their own on the socket gets neither the cookie nor the screen (security review of 0.3.0 step 7).
func dialDisplay(display, cookie string, serverPID int, serverUIDs []uint32) (*xgb.Conn, error) {
	number, ok := displayNumber(display)
	if !ok {
		return nil, errors.New("the display name is invalid")
	}
	path := "/tmp/.X11-unix/X" + strconv.Itoa(number)
	var socket net.Conn
	var err error
	for _, address := range []string{path, "@" + path} {
		socket, err = net.DialTimeout("unix", address, 5*time.Second)
		if err == nil {
			break
		}
	}
	if err != nil {
		return nil, fmt.Errorf("connect to X display %s: %w", display, err)
	}
	if err := checkServerPeer(socket, serverPID, serverUIDs); err != nil {
		_ = socket.Close()
		return nil, err
	}
	conn, err := xgb.NewConnNetWithCookieHex(socket, cookie)
	if err != nil {
		_ = socket.Close()
		return nil, fmt.Errorf("open X display %s: %w", display, err)
	}
	return conn, nil
}

// checkServerPeer compares the process at the other end of the display socket with the server the agent found.
func checkServerPeer(socket net.Conn, serverPID int, serverUIDs []uint32) error {
	unixConn, ok := socket.(*net.UnixConn)
	if !ok {
		return errors.New("the display is not a local socket")
	}
	raw, err := unixConn.SyscallConn()
	if err != nil {
		return err
	}
	var cred *unix.Ucred
	var credErr error
	if err := raw.Control(func(fd uintptr) {
		cred, credErr = unix.GetsockoptUcred(int(fd), unix.SOL_SOCKET, unix.SO_PEERCRED)
	}); err != nil {
		return err
	}
	if credErr != nil {
		return credErr
	}
	if serverPID != 0 && int(cred.Pid) != serverPID {
		return errors.New("another program than the X server answers on the display")
	}
	if len(serverUIDs) > 0 {
		for _, uid := range serverUIDs {
			if cred.Uid == uid {
				return nil
			}
		}
		return errors.New("the X server on the display belongs to another user")
	}
	return nil
}
