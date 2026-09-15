//go:build !windows

package watchdog

import "github.com/404-developer-AI/Fleeto/agent/internal/svcctl"

// IsService reports whether the process was started by the service manager. The systemd watchdog follows with the Linux agent.
func IsService() bool { return false }

// Serve is the service entry point.
func Serve() error { return svcctl.ErrUnsupported }
