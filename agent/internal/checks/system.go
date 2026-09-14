package checks

import (
	"context"
	"fmt"
	"strconv"
	"strings"
	"time"

	"github.com/shirou/gopsutil/v4/cpu"
	"github.com/shirou/gopsutil/v4/host"
	"github.com/shirou/gopsutil/v4/mem"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// DefaultCPUSampleSeconds is the CPU averaging window when the check does not set one.
const DefaultCPUSampleSeconds = 60

// DriveUsage is the space of one fixed drive or mount.
type DriveUsage struct {
	Name       string
	Filesystem string
	Total      uint64
	Free       uint64
}

// SystemCollector measures the local system.
type SystemCollector struct{}

// Collect implements Collector.
func (SystemCollector) Collect(ctx context.Context, spec *agentv1.CheckSpec) []Measurement {
	switch spec.GetType() {
	case agentv1.CheckType_CHECK_TYPE_CPU_USAGE:
		return []Measurement{cpuUsage(ctx, spec)}
	case agentv1.CheckType_CHECK_TYPE_MEMORY_USAGE:
		return []Measurement{memoryUsage(ctx)}
	case agentv1.CheckType_CHECK_TYPE_DISK_FREE:
		return diskFree(spec.GetParameters()["drive"])
	case agentv1.CheckType_CHECK_TYPE_SERVICE_RUNNING:
		return []Measurement{serviceRunning(spec.GetParameters()["service"])}
	case agentv1.CheckType_CHECK_TYPE_UPTIME:
		return []Measurement{uptime(ctx)}
	default:
		return []Measurement{{Error: fmt.Sprintf("check type %s is not supported by this agent version; update the agent", spec.GetType())}}
	}
}

// CPUSampleWindow returns the averaging window: sample_seconds, default 60, at most the interval, at least 1 s.
func CPUSampleWindow(spec *agentv1.CheckSpec) time.Duration {
	seconds := DefaultCPUSampleSeconds
	if v, ok := spec.GetParameters()["sample_seconds"]; ok {
		if n, err := strconv.Atoi(strings.TrimSpace(v)); err == nil && n > 0 {
			seconds = n
		}
	}
	if interval := int(spec.GetIntervalSeconds()); interval > 0 && seconds > interval {
		seconds = interval
	}
	if seconds < 1 {
		seconds = 1
	}
	return time.Duration(seconds) * time.Second
}

func cpuUsage(ctx context.Context, spec *agentv1.CheckSpec) Measurement {
	window := CPUSampleWindow(spec)
	values, err := cpu.PercentWithContext(ctx, window, false)
	if err != nil || len(values) == 0 {
		return Measurement{Error: fmt.Sprintf("CPU usage could not be read: %v", errOrEmpty(err))}
	}
	return Measurement{Value: round(values[0], 1), Detail: fmt.Sprintf("%.1f %% average over %d s", values[0], int(window/time.Second))}
}

func memoryUsage(ctx context.Context) Measurement {
	vm, err := mem.VirtualMemoryWithContext(ctx)
	if err != nil {
		return Measurement{Error: fmt.Sprintf("memory usage could not be read: %v", err)}
	}
	used := vm.Total - vm.Available
	percent := 0.0
	if vm.Total > 0 {
		percent = float64(used) / float64(vm.Total) * 100
	}
	return Measurement{Value: round(percent, 1), Detail: fmt.Sprintf("%s in use of %s", FormatBytes(used), FormatBytes(vm.Total))}
}

func uptime(ctx context.Context) Measurement {
	boot, err := host.BootTimeWithContext(ctx)
	if err != nil {
		return Measurement{Error: fmt.Sprintf("boot time could not be read: %v", err)}
	}
	since := time.Unix(int64(boot), 0).UTC()
	days := time.Since(since).Hours() / 24
	if days < 0 {
		days = 0
	}
	return Measurement{Value: round(days, 2), Detail: "up since " + since.Format(time.RFC3339)}
}

func diskFree(drive string) []Measurement {
	drive = strings.TrimSpace(drive)
	if drive == "" {
		return []Measurement{{Error: "the disk check has no drive parameter; set drive to a drive such as C: or * for every fixed drive"}}
	}
	drives, err := FixedDrives()
	if err != nil {
		return []Measurement{{Target: drive, Error: fmt.Sprintf("drives could not be listed: %v", err)}}
	}
	if drive == "*" {
		if len(drives) == 0 {
			return []Measurement{{Target: "*", Error: "no fixed drives found"}}
		}
		out := make([]Measurement, 0, len(drives))
		for _, d := range drives {
			out = append(out, driveMeasurement(d))
		}
		return out
	}
	want := NormalizeDrive(drive)
	for _, d := range drives {
		if strings.EqualFold(d.Name, want) {
			return []Measurement{driveMeasurement(d)}
		}
	}
	return []Measurement{{Target: want, Error: fmt.Sprintf("Drive %s does not exist or is not a fixed drive", want)}}
}

func driveMeasurement(d DriveUsage) Measurement {
	if d.Total == 0 {
		return Measurement{Target: d.Name, Error: fmt.Sprintf("Drive %s reports no size", d.Name)}
	}
	percent := float64(d.Free) / float64(d.Total) * 100
	return Measurement{
		Target: d.Name,
		Value:  round(percent, 1),
		Detail: fmt.Sprintf("%s free of %s", FormatBytes(d.Free), FormatBytes(d.Total)),
	}
}

// FormatBytes formats a size in binary units as Windows Explorer does: "12.3 GB", "237 GB", "512 MB".
func FormatBytes(b uint64) string {
	units := []string{"B", "KB", "MB", "GB", "TB", "PB"}
	value := float64(b)
	i := 0
	for value >= 1024 && i < len(units)-1 {
		value /= 1024
		i++
	}
	if i == 0 {
		return fmt.Sprintf("%d B", b)
	}
	if value >= 100 {
		return fmt.Sprintf("%.0f %s", value, units[i])
	}
	return fmt.Sprintf("%.1f %s", value, units[i])
}

func round(v float64, decimals int) float64 {
	p := 1.0
	for range decimals {
		p *= 10
	}
	if v < 0 {
		return float64(int64(v*p-0.5)) / p
	}
	return float64(int64(v*p+0.5)) / p
}

func errOrEmpty(err error) any {
	if err == nil {
		return "no data"
	}
	return err
}
