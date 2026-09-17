//go:build windows

package remote

import (
	"errors"
	"os"

	"golang.org/x/sys/windows"
)

// driveRoots lists the fixed and removable drives, e.g. "C:\\", so the file explorer can start at "This PC".
func driveRoots() []string {
	mask, err := windows.GetLogicalDrives()
	if err != nil {
		return []string{`C:\`}
	}
	var roots []string
	for i := 0; i < 26; i++ {
		if mask&(1<<uint(i)) != 0 {
			roots = append(roots, string(rune('A'+i))+`:\`)
		}
	}
	if len(roots) == 0 {
		roots = []string{`C:\`}
	}
	return roots
}

// defaultPath is where the explorer opens: the system drive.
func defaultPath() string {
	if drive := os.Getenv("SystemDrive"); drive != "" {
		return drive + `\`
	}
	return `C:\`
}

// inUse reports whether an operation failed because another program has the file open.
func inUse(err error) bool {
	return errors.Is(err, windows.ERROR_SHARING_VIOLATION) || errors.Is(err, windows.ERROR_LOCK_VIOLATION)
}
