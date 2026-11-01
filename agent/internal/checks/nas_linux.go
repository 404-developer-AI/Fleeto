//go:build linux

package checks

import (
	"context"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"sort"
	"strings"
	"time"
)

// dsmVersionFile exists on Synology DSM only (inventory.DSMVersionFile).
const dsmVersionFile = "/etc.defaults/VERSION"

// smartTimeout bounds one smartctl run; a disk that hangs must not hold the check.
const smartTimeout = 30 * time.Second

func raidState(array string) []Measurement {
	content, err := os.ReadFile("/proc/mdstat")
	if errors.Is(err, os.ErrNotExist) {
		return []Measurement{{Target: "*", Error: "this endpoint has no software RAID (no /proc/mdstat)"}}
	}
	if err != nil {
		return []Measurement{{Target: "*", Error: "/proc/mdstat could not be read: " + err.Error()}}
	}
	_, statErr := os.Stat(dsmVersionFile)
	return RaidMeasurements(string(content), array, statErr == nil)
}

// smartDisks lists the disks below /sys/block that smartctl can read: SATA and SAS (sd*, sata* on newer DSM) and NVMe, never a USB
// disk, a removable one or a virtual device.
func smartDisks() []string {
	entries, err := os.ReadDir("/sys/block")
	if err != nil {
		return nil
	}
	var disks []string
	for _, e := range entries {
		name := e.Name()
		if !DiskName.MatchString(name) {
			continue
		}
		if target, err := filepath.EvalSymlinks(filepath.Join("/sys/block", name)); err == nil &&
			(strings.Contains(target, "/usb") || strings.Contains(target, "/virtual/")) {
			continue
		}
		if removable, err := os.ReadFile(filepath.Join("/sys/block", name, "removable")); err == nil && strings.TrimSpace(string(removable)) == "1" {
			continue
		}
		disks = append(disks, name)
	}
	sort.Strings(disks)
	return disks
}

// diskReports runs smartctl for the disks the parameter names. A disk in standby is left out: reading it would spin it up, so its last
// result stays until it is awake at a run.
func diskReports(ctx context.Context, disk string) (map[string]SmartReport, []Measurement) {
	disk = strings.TrimSpace(disk)
	if disk == "" {
		disk = "*"
	}
	if disk != "*" && !DiskName.MatchString(disk) {
		return nil, []Measurement{{Error: "the disk parameter must be * or a disk name such as sda, sata1 or nvme0n1"}}
	}
	tool := findSmartctl()
	if tool == "" {
		return nil, []Measurement{{Target: disk, Error: "smartctl was not found on this endpoint; install smartmontools to read the disks"}}
	}
	disks := []string{disk}
	if disk == "*" {
		if disks = smartDisks(); len(disks) == 0 {
			return nil, []Measurement{{Target: "*", Error: "this endpoint has no disk smartctl can read"}}
		}
	} else if _, err := os.Stat(filepath.Join("/sys/block", disk)); err != nil {
		return nil, []Measurement{{Target: disk, Error: "disk " + disk + " does not exist on this endpoint"}}
	}
	reports := map[string]SmartReport{}
	for _, d := range disks {
		runCtx, cancel := context.WithTimeout(ctx, smartTimeout)
		// #nosec G204 -- smartctl with fixed options and a disk name that matched DiskName.
		output, _ := exec.CommandContext(runCtx, tool, "-n", "standby", "-i", "-H", "-A", "/dev/"+d).Output()
		cancel()
		// smartctl sets bits of its exit code for what it found on the disk; the output says what it is, so the code is not judged.
		reports[d] = ParseSmart(string(output))
	}
	return reports, nil
}

func diskHealth(ctx context.Context, disk string) []Measurement {
	reports, problem := diskReports(ctx, disk)
	if problem != nil {
		return problem
	}
	var out []Measurement
	for _, d := range sortedKeys(reports) {
		if r := reports[d]; !r.Standby {
			out = append(out, HealthMeasurement(d, r))
		}
	}
	return out
}

func diskTemperature(ctx context.Context, disk string) []Measurement {
	reports, problem := diskReports(ctx, disk)
	if problem != nil {
		return problem
	}
	var out []Measurement
	for _, d := range sortedKeys(reports) {
		if r := reports[d]; !r.Standby {
			out = append(out, TemperatureMeasurement(d, r))
		}
	}
	return out
}

// smartctlPaths are where smartctl lives when the PATH of the service does not hold it, as on Synology DSM.
var smartctlPaths = []string{"/usr/sbin/smartctl", "/usr/bin/smartctl", "/usr/syno/bin/smartctl", "/usr/local/sbin/smartctl"}

func findSmartctl() string {
	if tool, err := exec.LookPath("smartctl"); err == nil {
		return tool
	}
	for _, path := range smartctlPaths {
		if info, err := os.Stat(path); err == nil && info.Mode().IsRegular() && info.Mode()&0o111 != 0 {
			return path
		}
	}
	return ""
}

func sortedKeys(m map[string]SmartReport) []string {
	keys := make([]string, 0, len(m))
	for k := range m {
		keys = append(keys, k)
	}
	sort.Strings(keys)
	return keys
}
