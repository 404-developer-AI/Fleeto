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

	"github.com/404-developer-AI/Fleeto/agent/internal/jobs"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
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
type SystemCollector struct {
	// ScriptDir is where script checks write their script while it runs; script checks cannot run without it.
	ScriptDir string
	// Access protects ScriptDir.
	Access platform.Access
}

// Collect implements Collector.
func (c SystemCollector) Collect(ctx context.Context, spec *agentv1.CheckSpec) []Measurement {
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
	case agentv1.CheckType_CHECK_TYPE_PING:
		return []Measurement{pingCheck(ctx, spec.GetParameters())}
	case agentv1.CheckType_CHECK_TYPE_TCP_PORT:
		return []Measurement{tcpCheck(ctx, spec.GetParameters())}
	case agentv1.CheckType_CHECK_TYPE_HTTP:
		return httpCheck(ctx, spec.GetParameters())
	case agentv1.CheckType_CHECK_TYPE_PROCESS_RUNNING:
		return []Measurement{processCheck(ctx, spec.GetParameters()["process"])}
	case agentv1.CheckType_CHECK_TYPE_PENDING_REBOOT:
		return []Measurement{pendingReboot(ctx)}
	case agentv1.CheckType_CHECK_TYPE_FILE:
		return []Measurement{fileCheck(ctx, spec.GetParameters())}
	case agentv1.CheckType_CHECK_TYPE_CERTIFICATE_EXPIRY:
		return certificateCheck(spec.GetParameters(), time.Now())
	case agentv1.CheckType_CHECK_TYPE_EVENT_LOG:
		return []Measurement{eventLogCheck(ctx, spec.GetParameters())}
	case agentv1.CheckType_CHECK_TYPE_SECURITY_CENTER:
		return []Measurement{securityCenterCheck(ctx, spec.GetParameters()["component"])}
	case agentv1.CheckType_CHECK_TYPE_SCRIPT:
		return []Measurement{c.scriptCheck(ctx, spec)}
	default:
		return []Measurement{{Error: fmt.Sprintf("check type %s is not supported by this agent version; update the agent", spec.GetType())}}
	}
}

// scriptCheck runs the library script of the verified configuration; its exit code is the value. The server judges the code.
func (c SystemCollector) scriptCheck(ctx context.Context, spec *agentv1.CheckSpec) Measurement {
	if reason := strings.TrimSpace(spec.GetParameters()["unavailable"]); reason != "" {
		return Measurement{Error: reason}
	}
	if c.ScriptDir == "" {
		return Measurement{Error: "script checks are not available in this agent mode"}
	}
	timeout := jobs.MaxCheckScriptTimeout
	if n, err := strconv.Atoi(strings.TrimSpace(spec.GetParameters()["timeout_seconds"])); err == nil && n > 0 {
		timeout = time.Duration(n) * time.Second
	}
	if interval := time.Duration(spec.GetIntervalSeconds()) * time.Second; interval > 0 && timeout > interval {
		timeout = interval
	}
	result := jobs.RunCheckScript(ctx, c.ScriptDir, c.Access, spec.GetId(), spec.GetScript(), timeout)
	if result.Error != "" {
		return Measurement{Error: result.Error, Detail: result.Detail}
	}
	return Measurement{Value: float64(result.ExitCode), Detail: result.Detail}
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

// intParam reads an integer parameter within [min, max], or returns def when it is missing or invalid. The server validates
// parameters; the agent checks again because they decide what runs on the endpoint.
func intParam(params map[string]string, name string, def, min, max int) int {
	v, ok := params[name]
	if !ok {
		return def
	}
	n, err := strconv.Atoi(strings.TrimSpace(v))
	if err != nil || n < min || n > max {
		return def
	}
	return n
}

// hasControlChars reports whether a parameter contains characters no valid parameter contains.
func hasControlChars(s string) bool {
	return strings.ContainsFunc(s, func(r rune) bool { return r < 0x20 || r == 0x7f })
}

func errOrEmpty(err error) any {
	if err == nil {
		return "no data"
	}
	return err
}
