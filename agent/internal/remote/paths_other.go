//go:build !windows

package remote

// driveRoots is the single filesystem root on Unix-like systems.
func driveRoots() []string { return []string{"/"} }

// defaultPath is where the explorer opens.
func defaultPath() string { return "/" }

// inUse reports whether an operation failed because another program has the file open; Unix-like systems do not lock files that way.
func inUse(error) bool { return false }

// samePathName compares two paths the way the file system does: case matters on Linux.
func samePathName(a, b string) bool { return a == b }
