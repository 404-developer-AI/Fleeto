//go:build windows

package checks

import (
	"errors"
	"fmt"
	"strings"

	"golang.org/x/sys/windows"
)

// NormalizeDrive turns "c", "c:" or "C:\" into "C:".
func NormalizeDrive(drive string) string {
	d := strings.TrimRight(strings.TrimSpace(drive), `\/`)
	if len(d) == 1 {
		d += ":"
	}
	return strings.ToUpper(d)
}

// FixedDrives lists the fixed drives with their space.
func FixedDrives() ([]DriveUsage, error) {
	buf := make([]uint16, 512)
	n, err := windows.GetLogicalDriveStrings(uint32(len(buf)), &buf[0])
	if err != nil {
		return nil, err
	}
	var drives []DriveUsage
	for _, root := range splitMultiSZ(buf[:n]) {
		rootPtr, err := windows.UTF16PtrFromString(root)
		if err != nil {
			continue
		}
		if windows.GetDriveType(rootPtr) != windows.DRIVE_FIXED {
			continue
		}
		var free, total, totalFree uint64
		if err := windows.GetDiskFreeSpaceEx(rootPtr, &free, &total, &totalFree); err != nil {
			// A locked BitLocker volume, for example; report it rather than hiding it.
			drives = append(drives, DriveUsage{Name: NormalizeDrive(root)})
			continue
		}
		fs := make([]uint16, windows.MAX_PATH+1)
		_ = windows.GetVolumeInformation(rootPtr, nil, 0, nil, nil, nil, &fs[0], uint32(len(fs)))
		drives = append(drives, DriveUsage{Name: NormalizeDrive(root), Filesystem: windows.UTF16ToString(fs), Total: total, Free: totalFree})
	}
	return drives, nil
}

func splitMultiSZ(buf []uint16) []string {
	var out []string
	start := 0
	for i, c := range buf {
		if c == 0 {
			if i > start {
				out = append(out, windows.UTF16ToString(buf[start:i]))
			}
			start = i + 1
		}
	}
	return out
}

// serviceRunning queries the service with the least rights needed, so it also works in the non-elevated
// development mode.
func serviceRunning(name string) Measurement {
	name = strings.TrimSpace(name)
	if name == "" {
		return Measurement{Error: "the service check has no service parameter; set service to the service name"}
	}
	running, state, err := QueryService(name)
	if err != nil {
		if errors.Is(err, windows.ERROR_SERVICE_DOES_NOT_EXIST) {
			return Measurement{Target: name, Error: fmt.Sprintf("Service %s does not exist", name)}
		}
		return Measurement{Target: name, Error: fmt.Sprintf("Service %s could not be queried: %v", name, err)}
	}
	value := 0.0
	if running {
		value = 1
	}
	return Measurement{Target: name, Value: value, Detail: state}
}

// QueryService returns whether a service runs and its state name.
func QueryService(name string) (bool, string, error) {
	scm, err := windows.OpenSCManager(nil, nil, windows.SC_MANAGER_CONNECT)
	if err != nil {
		return false, "", err
	}
	defer windows.CloseServiceHandle(scm)
	namePtr, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return false, "", err
	}
	h, err := windows.OpenService(scm, namePtr, windows.SERVICE_QUERY_STATUS)
	if err != nil {
		return false, "", err
	}
	defer windows.CloseServiceHandle(h)
	var status windows.SERVICE_STATUS
	if err := windows.QueryServiceStatus(h, &status); err != nil {
		return false, "", err
	}
	return status.CurrentState == windows.SERVICE_RUNNING, ServiceStateName(status.CurrentState), nil
}

// ServiceStateName names a service state.
func ServiceStateName(state uint32) string {
	switch state {
	case windows.SERVICE_STOPPED:
		return "Stopped"
	case windows.SERVICE_START_PENDING:
		return "Starting"
	case windows.SERVICE_STOP_PENDING:
		return "Stopping"
	case windows.SERVICE_RUNNING:
		return "Running"
	case windows.SERVICE_CONTINUE_PENDING:
		return "Continuing"
	case windows.SERVICE_PAUSE_PENDING:
		return "Pausing"
	case windows.SERVICE_PAUSED:
		return "Paused"
	default:
		return fmt.Sprintf("Unknown state %d", state)
	}
}
