//go:build linux

package screen

import (
	"errors"
	"os"
	"os/user"
	"runtime"
	"strconv"
	"syscall"
	"unsafe"

	"golang.org/x/sys/unix"
)

// Reading a file with the rights of a user (security review of 0.3.0 step 7). The agent runs as root; a path that comes from a user (a file
// they copied, their XAUTHORITY) is opened on a thread of its own that takes that user's uid, gid and groups first, so root never reads
// for them what they may not read themselves, and never opens a device or a named pipe they point at. The thread is thrown away after the
// open: the credentials change only that thread (raw system calls, not the process-wide ones), and Go ends a thread whose goroutine exits
// while locked to it.

// openAs opens a regular file for reading as the account uid/gid with its supplementary groups.
func openAs(path string, uid, gid uint32) (*os.File, error) {
	groups := []uint32{gid}
	if account, err := user.LookupId(strconv.FormatUint(uint64(uid), 10)); err == nil {
		if ids, err := account.GroupIds(); err == nil {
			for _, id := range ids {
				if n, err := strconv.ParseUint(id, 10, 32); err == nil && uint32(n) != gid {
					groups = append(groups, uint32(n))
				}
			}
		}
	}
	type result struct {
		file *os.File
		err  error
	}
	done := make(chan result, 1)
	go func() {
		// Never unlocked: the thread ends with this goroutine and takes the user's credentials with it.
		runtime.LockOSThread()
		if err := becomeOnThisThread(uid, gid, groups); err != nil {
			done <- result{err: err}
			return
		}
		fd, err := unix.Open(path, unix.O_RDONLY|unix.O_NONBLOCK|unix.O_NOCTTY|unix.O_CLOEXEC, 0)
		if err != nil {
			done <- result{err: &os.PathError{Op: "open", Path: path, Err: err}}
			return
		}
		var st unix.Stat_t
		if err := unix.Fstat(fd, &st); err != nil || st.Mode&unix.S_IFMT != unix.S_IFREG {
			_ = unix.Close(fd)
			done <- result{err: &os.PathError{Op: "open", Path: path, Err: syscall.EINVAL}}
			return
		}
		if err := unix.SetNonblock(fd, false); err != nil {
			_ = unix.Close(fd)
			done <- result{err: err}
			return
		}
		done <- result{file: os.NewFile(uintptr(fd), path)}
	}()
	r := <-done
	return r.file, r.err
}

// becomeOnThisThread switches the calling thread (only) to the account. Groups first, then the group, then the user.
func becomeOnThisThread(uid, gid uint32, groups []uint32) error {
	if uid == 0 {
		return errors.New("a file of a user is never opened as root")
	}
	list := make([]uint32, len(groups))
	copy(list, groups)
	if len(list) == 0 {
		list = []uint32{gid}
	}
	if _, _, errno := unix.RawSyscall(unix.SYS_SETGROUPS, uintptr(len(list)), uintptr(unsafe.Pointer(&list[0])), 0); errno != 0 {
		return errno
	}
	if _, _, errno := unix.RawSyscall(unix.SYS_SETRESGID, uintptr(gid), uintptr(gid), uintptr(gid)); errno != 0 {
		return errno
	}
	if _, _, errno := unix.RawSyscall(unix.SYS_SETRESUID, uintptr(uid), uintptr(uid), uintptr(uid)); errno != 0 {
		return errno
	}
	return nil
}
