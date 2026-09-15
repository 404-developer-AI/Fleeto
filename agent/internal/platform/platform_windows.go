//go:build windows

package platform

import (
	"fmt"
	"os"
	"path/filepath"

	"golang.org/x/sys/windows"
)

// DefaultStateDir is C:\ProgramData\Fleetify\Agent.
func DefaultStateDir() string {
	base := os.Getenv("ProgramData")
	if base == "" {
		base = `C:\ProgramData`
	}
	return filepath.Join(base, "Fleetify", "Agent")
}

// ProgramDir is C:\Program Files\Fleetify\Agent.
func ProgramDir() string {
	base := os.Getenv("ProgramFiles")
	if base == "" {
		base = `C:\Program Files`
	}
	return filepath.Join(base, "Fleetify", "Agent")
}

// BinaryName is the file name of the agent executable.
const BinaryName = "fleetify-agent.exe"

// WatchdogBinaryName is the file name of the watchdog executable, installed next to the agent (0.2.1).
const WatchdogBinaryName = "fleetify-watchdog.exe"

// WatchdogStateDir is C:\ProgramData\Fleetify\Watchdog.
func WatchdogStateDir() string {
	return filepath.Join(filepath.Dir(DefaultStateDir()), "Watchdog")
}

// DataDir is C:\ProgramData\Fleetify, the parent of the agent and watchdog state directories.
func DataDir() string {
	return filepath.Dir(DefaultStateDir())
}

// IsElevated reports whether the process runs with an elevated (administrator) token.
func IsElevated() bool {
	return windows.GetCurrentProcessToken().IsElevated()
}

// ElevationHint tells the technician how to get the required rights.
const ElevationHint = "open PowerShell or Command Prompt with 'Run as administrator' and run the command again"

func protect(path string, access Access, inherit bool) error {
	flags := ""
	if inherit {
		flags = "OICI"
	}
	var sddl string
	switch access {
	case AccessSystem:
		// Protected DACL: SYSTEM and the built-in Administrators group only, no inherited entries.
		sddl = fmt.Sprintf("D:P(A;%[1]s;FA;;;SY)(A;%[1]s;FA;;;BA)", flags)
	case AccessCurrentUser:
		user, err := currentUserSID()
		if err != nil {
			return err
		}
		sddl = fmt.Sprintf("D:P(A;%s;FA;;;%s)", flags, user)
	default:
		return fmt.Errorf("unknown access mode %d", access)
	}
	sd, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		return fmt.Errorf("build security descriptor: %w", err)
	}
	dacl, _, err := sd.DACL()
	if err != nil {
		return fmt.Errorf("read DACL: %w", err)
	}
	err = windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, dacl, nil)
	if err != nil {
		return fmt.Errorf("restrict access to %s: %w", path, err)
	}
	return nil
}

func currentUserSID() (string, error) {
	user, err := windows.GetCurrentProcessToken().GetTokenUser()
	if err != nil {
		return "", fmt.Errorf("read current user: %w", err)
	}
	return user.User.Sid.String(), nil
}
