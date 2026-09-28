//go:build !windows && !linux

package storage

// lowerThreadPriority does nothing on other platforms.
func lowerThreadPriority() error { return nil }
