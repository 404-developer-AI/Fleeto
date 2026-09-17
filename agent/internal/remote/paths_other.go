//go:build !windows

package remote

// driveRoots is the single filesystem root on Unix-like systems.
func driveRoots() []string { return []string{"/"} }

// defaultPath is where the explorer opens.
func defaultPath() string { return "/" }
