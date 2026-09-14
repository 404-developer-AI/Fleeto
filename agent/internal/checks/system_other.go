//go:build !windows

package checks

import (
	"strings"

	"github.com/shirou/gopsutil/v4/disk"
)

// NormalizeDrive returns the mount point as given.
func NormalizeDrive(drive string) string {
	return strings.TrimSpace(drive)
}

// FixedDrives lists physical partitions with their space.
func FixedDrives() ([]DriveUsage, error) {
	parts, err := disk.Partitions(false)
	if err != nil {
		return nil, err
	}
	var drives []DriveUsage
	seen := map[string]bool{}
	for _, p := range parts {
		if seen[p.Mountpoint] {
			continue
		}
		seen[p.Mountpoint] = true
		usage, err := disk.Usage(p.Mountpoint)
		if err != nil {
			continue
		}
		drives = append(drives, DriveUsage{Name: p.Mountpoint, Filesystem: p.Fstype, Total: usage.Total, Free: usage.Free})
	}
	return drives, nil
}

func serviceRunning(name string) Measurement {
	return Measurement{Target: strings.TrimSpace(name), Error: "Service checks are not supported on this platform yet"}
}
