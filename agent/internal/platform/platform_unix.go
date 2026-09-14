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
		return "/Library/Application Support/Fleetify/Agent"
	}
	return "/var/lib/fleetify-agent"
}

// ProgramDir is where the agent binary is installed.
func ProgramDir() string {
	if runtime.GOOS == "darwin" {
		return "/Library/Fleetify/Agent"
	}
	return "/opt/fleetify-agent"
}

// BinaryName is the file name of the agent executable.
const BinaryName = "fleetify-agent"

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
