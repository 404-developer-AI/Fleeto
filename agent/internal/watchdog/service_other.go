//go:build !windows && !linux

package watchdog

import "github.com/404-developer-AI/Fleeto/agent/internal/svcctl"

// IsService reports whether the process was started by the service manager.
func IsService() bool { return false }

// Serve is the service entry point.
func Serve() error { return svcctl.ErrUnsupported }
