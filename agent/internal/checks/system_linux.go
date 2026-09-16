//go:build linux

package checks

import (
	"fmt"
	"strings"

	"github.com/shirou/gopsutil/v4/disk"

	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
)

// virtualFilesystems never hold data of the endpoint: they are read-only images, container layers or kernel filesystems, and a
// squashfs image is always 100 % full, so reporting it would alert on every snap.
var virtualFilesystems = map[string]bool{
	"squashfs": true, "iso9660": true, "overlay": true, "tmpfs": true, "devtmpfs": true, "ramfs": true,
	"autofs": true, "fuse.snapfuse": true, "fuse.portal": true, "fuse.gvfsd-fuse": true,
	"nfs": true, "nfs4": true, "cifs": true, "smb3": true, "fuse.sshfs": true,
}

// virtualMounts are the mount trees of container and package runtimes: their filesystems are counted under their real mount.
var virtualMounts = []string{"/snap/", "/var/lib/docker/", "/var/lib/containers/", "/var/lib/kubelet/", "/run/"}

// NormalizeDrive returns the mount point without a trailing slash, except for the root.
func NormalizeDrive(drive string) string {
	d := strings.TrimSpace(drive)
	if len(d) > 1 {
		d = strings.TrimRight(d, "/")
		if d == "" {
			return "/"
		}
	}
	return d
}

// FixedDrives lists the local filesystems that hold data, one per device: the same filesystem mounted twice (a bind mount) is
// reported once, under its shortest mount point.
func FixedDrives() ([]DriveUsage, error) {
	parts, err := disk.Partitions(false)
	if err != nil {
		return nil, err
	}
	byDevice := map[string]DriveUsage{}
	var order []string
	for _, p := range parts {
		if !keepPartition(p.Fstype, p.Mountpoint) {
			continue
		}
		usage, err := disk.Usage(p.Mountpoint)
		if err != nil || usage.Total == 0 {
			continue
		}
		drive := DriveUsage{Name: NormalizeDrive(p.Mountpoint), Filesystem: p.Fstype, Total: usage.Total, Free: usage.Free}
		current, seen := byDevice[p.Device]
		if !seen {
			byDevice[p.Device] = drive
			order = append(order, p.Device)
			continue
		}
		if len(drive.Name) < len(current.Name) {
			byDevice[p.Device] = drive
		}
	}
	drives := make([]DriveUsage, 0, len(order))
	for _, device := range order {
		drives = append(drives, byDevice[device])
	}
	return drives, nil
}

// keepPartition reports whether a filesystem holds data of this endpoint, so that its free space is worth reporting.
func keepPartition(fstype, mount string) bool {
	if virtualFilesystems[fstype] || strings.HasPrefix(fstype, "fuse.") {
		return false
	}
	for _, prefix := range virtualMounts {
		if strings.HasPrefix(mount, prefix) {
			return false
		}
	}
	return true
}

// serviceRunning reports whether a systemd service runs. The unit may be given with or without its .service suffix.
func serviceRunning(name string) Measurement {
	name = strings.TrimSpace(name)
	if name == "" {
		return Measurement{Error: "the service check has no service parameter; set service to the unit name, for example nginx"}
	}
	unit := strings.TrimSuffix(name, ".service")
	state, err := svcctl.New().Query(unit)
	if err != nil {
		return Measurement{Target: name, Error: fmt.Sprintf("Service %s could not be queried: %v", name, err)}
	}
	switch state {
	case svcctl.StateNotInstalled:
		return Measurement{Target: name, Error: fmt.Sprintf("Service %s does not exist", name)}
	case svcctl.StateRunning:
		return Measurement{Target: name, Value: 1, Detail: "Running"}
	case svcctl.StateStarting:
		return Measurement{Target: name, Value: 1, Detail: "Starting"}
	case svcctl.StateDisabled:
		return Measurement{Target: name, Value: 0, Detail: "Stopped and disabled"}
	default:
		return Measurement{Target: name, Value: 0, Detail: strings.ToUpper(state.String()[:1]) + state.String()[1:]}
	}
}
