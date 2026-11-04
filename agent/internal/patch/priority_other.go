//go:build !linux

package patch

// lowerThreadPriority does nothing on other platforms: there is no scan there yet (Windows follows in a later step).
func lowerThreadPriority() error { return nil }
