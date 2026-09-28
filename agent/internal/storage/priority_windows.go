//go:build windows

package storage

import "golang.org/x/sys/windows"

const threadModeBackgroundBegin = 0x00010000

var procSetThreadPriority = windows.NewLazySystemDLL("kernel32.dll").NewProc("SetThreadPriority")

// lowerThreadPriority puts the calling OS thread in background mode, which lowers its CPU, I/O and memory priority. The caller
// locked its goroutine to the thread and never unlocks it, so the thread ends with the goroutine and never runs other work.
func lowerThreadPriority() error {
	thread, err := windows.GetCurrentThread()
	if err != nil {
		return err
	}
	r, _, err := procSetThreadPriority.Call(uintptr(thread), threadModeBackgroundBegin)
	if r == 0 {
		return err
	}
	return nil
}
