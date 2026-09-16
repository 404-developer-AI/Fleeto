//go:build !windows

package platform

import (
	"fmt"
	"os"
	"runtime"
)

// DefaultStateDir is the state directory of the agent service.
func DefaultStateDir() string {
	if runtime.GOOS == "darwin" {
		return "/Library/Application Support/Fleeto/Agent"
	}
	return "/var/lib/fleeto/agent"
}

// ProgramDir is where the agent and watchdog binaries are installed. It is deliberately not under /opt/fleeto: that path holds the
// instances of a Fleeto server, which can run on an endpoint that also has an agent.
func ProgramDir() string {
	if runtime.GOOS == "darwin" {
		return "/Library/Fleeto/Agent"
	}
	return "/opt/fleeto-agent"
}

// BinaryName is the file name of the agent executable.
const BinaryName = "fleeto-agent"

// WatchdogBinaryName is the file name of the watchdog executable (0.2.1).
const WatchdogBinaryName = "fleeto-watchdog"

// WatchdogStateDir is the state directory of the watchdog service.
func WatchdogStateDir() string {
	if runtime.GOOS == "darwin" {
		return "/Library/Application Support/Fleeto/Watchdog"
	}
	return "/var/lib/fleeto/watchdog"
}

// DataDir holds the markers shared by agent and watchdog.
func DataDir() string {
	if runtime.GOOS == "darwin" {
		return "/Library/Application Support/Fleeto"
	}
	return "/var/lib/fleeto"
}

// IsElevated reports whether the process runs as root.
func IsElevated() bool {
	return os.Geteuid() == 0
}

// ElevationHint tells the technician how to get the required rights.
const ElevationHint = "run the command again with sudo"

func protect(path string, access Access, dir bool) error {
	mode := os.FileMode(0o600)
	if dir {
		mode = 0o700
	}
	if err := os.Chmod(path, mode); err != nil {
		return fmt.Errorf("restrict access to %s: %w", path, err)
	}
	if access == AccessSystem && os.Geteuid() == 0 {
		if err := os.Chown(path, 0, 0); err != nil {
			return fmt.Errorf("set owner of %s: %w", path, err)
		}
	}
	return nil
}
