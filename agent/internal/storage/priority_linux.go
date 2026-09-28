//go:build linux

package storage

import "golang.org/x/sys/unix"

const (
	ioprioWhoProcess = 1
	ioprioClassIdle  = 3
	ioprioClassShift = 13
)

// lowerThreadPriority gives the calling thread the idle I/O class (it only gets the disk when nothing else wants it) and the
// lowest CPU priority. On Linux both apply to one thread when given its thread id. The caller locked its goroutine to the
// thread and never unlocks it, so the thread ends with the goroutine and never runs other work.
func lowerThreadPriority() error {
	tid := unix.Gettid()
	if _, _, errno := unix.Syscall(unix.SYS_IOPRIO_SET, ioprioWhoProcess, uintptr(tid), ioprioClassIdle<<ioprioClassShift); errno != 0 {
		return errno
	}
	return unix.Setpriority(unix.PRIO_PROCESS, tid, 19)
}
