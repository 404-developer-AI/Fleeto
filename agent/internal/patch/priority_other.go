//go:build !linux

package patch

// lowerThreadPriority does nothing on other platforms. On Windows the search runs in the Windows Update service (wuauserv), not in
// the agent, so the priority of the agent thread would not reach it.
func lowerThreadPriority() error { return nil }
