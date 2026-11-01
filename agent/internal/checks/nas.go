package checks

import (
	"bufio"
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

// The checks of a NAS (0.8.0, ROADMAP.md, Synology DSM as an endpoint platform), read by the agent on the endpoint itself: the state of
// every software RAID array (Linux md, which Synology DSM and mdadm use), the SMART health of every disk, and its temperature. The parsers
// here have no platform; reading /proc/mdstat and running smartctl is in nas_linux.go.

// The values of a RAID state check: higher is worse, so a warning threshold of 1 and a critical one of 2 are the defaults.
const (
	RaidHealthy    = 0
	RaidRebuilding = 1
	RaidDegraded   = 2
)

// The values of a disk health check, in the same way.
const (
	DiskHealthy = 0
	DiskWarning = 1
	DiskFailing = 2
)

// RaidArray is one array of /proc/mdstat.
type RaidArray struct {
	Name     string
	Active   bool
	Level    string
	Members  int // the slots of the array, from [n/m]; 0 when mdstat names none (raid0, linear)
	Working  int // the members that work, from [n/m]
	Failed   int // members marked (F)
	Progress string
	Percent  string
}

var (
	mdSlots    = regexp.MustCompile(`\[(\d+)/(\d+)\]`)
	mdProgress = regexp.MustCompile(`\b(recovery|resync|reshape|check|repair)\s*=\s*([0-9.]+)%`)
	mdPending  = regexp.MustCompile(`\bresync\s*=\s*(PENDING|DELAYED)`)
	// ArrayName is what the array parameter may name: md0, md127, md_d0.
	ArrayName = regexp.MustCompile(`^md[a-z0-9_]{1,16}$`)
)

// ParseMdstat reads the arrays of /proc/mdstat.
func ParseMdstat(content string) []RaidArray {
	var arrays []RaidArray
	var current *RaidArray
	scanner := bufio.NewScanner(strings.NewReader(content))
	for scanner.Scan() {
		line := scanner.Text()
		if name, rest, ok := strings.Cut(line, " : "); ok && strings.HasPrefix(name, "md") && !strings.HasPrefix(line, " ") {
			arrays = append(arrays, RaidArray{Name: strings.TrimSpace(name)})
			current = &arrays[len(arrays)-1]
			fields := strings.Fields(rest)
			for i, f := range fields {
				switch {
				case i == 0:
					current.Active = f == "active"
				case strings.HasPrefix(f, "(") && strings.HasSuffix(f, ")"):
					// (auto-read-only), (read-only)
				case current.Level == "" && (strings.HasPrefix(f, "raid") || f == "linear" || f == "multipath"):
					current.Level = f
				case strings.Contains(f, "[") && strings.HasSuffix(f, "(F)"):
					current.Failed++
				}
			}
			continue
		}
		if current == nil || strings.TrimSpace(line) == "" {
			if strings.TrimSpace(line) == "" {
				current = nil
			}
			continue
		}
		if m := mdSlots.FindStringSubmatch(line); m != nil && current.Members == 0 {
			current.Members, _ = strconv.Atoi(m[1])
			current.Working, _ = strconv.Atoi(m[2])
		}
		if m := mdProgress.FindStringSubmatch(line); m != nil {
			current.Progress, current.Percent = m[1], m[2]
		} else if m := mdPending.FindStringSubmatch(line); m != nil && current.Progress == "" {
			current.Progress = "resync"
		}
	}
	return arrays
}

// RaidMeasurement judges one array: degraded when it is inactive, a member failed or fewer members work than it has slots; rebuilding
// while it recovers, resyncs or reshapes. A check or repair is the data scrubbing DSM and mdadm schedule: healthy, with its progress.
func RaidMeasurement(a RaidArray) Measurement {
	level := a.Level
	if level == "" {
		level = "md"
	}
	detail := level
	if a.Members > 0 {
		detail += fmt.Sprintf(", %d of %d disks", a.Working, a.Members)
	}
	if a.Failed > 0 {
		detail += fmt.Sprintf(", %d failed", a.Failed)
	}
	progress := ""
	if a.Progress != "" {
		names := map[string]string{"recovery": "rebuilding", "resync": "resyncing", "reshape": "reshaping", "check": "data scrubbing",
			"repair": "data scrubbing"}
		progress = ", " + names[a.Progress]
		if a.Percent != "" {
			progress += " " + a.Percent + "%"
		}
	}
	switch {
	case !a.Active:
		return Measurement{Target: a.Name, Value: RaidDegraded, Detail: detail + ", inactive"}
	case a.Failed > 0 || (a.Members > 0 && a.Working < a.Members):
		return Measurement{Target: a.Name, Value: RaidDegraded, Detail: detail + progress}
	case a.Progress == "recovery" || a.Progress == "resync" || a.Progress == "reshape":
		return Measurement{Target: a.Name, Value: RaidRebuilding, Detail: detail + progress}
	default:
		return Measurement{Target: a.Name, Value: RaidHealthy, Detail: detail + progress}
	}
}

// RaidMeasurements answers a RAID state check from the content of /proc/mdstat. With "*" every array counts except, on Synology DSM,
// md0 and md1: the system and swap partitions DSM puts on every disk, with a slot for every bay, so a NAS with an empty bay would always
// look degraded. A named array counts also when it is one of those.
func RaidMeasurements(mdstat, array string, dsm bool) []Measurement {
	array = strings.TrimSpace(array)
	if array == "" {
		array = "*"
	}
	if array != "*" && !ArrayName.MatchString(array) {
		return []Measurement{{Error: "the array parameter must be * or the name of an md array, such as md2"}}
	}
	var out []Measurement
	for _, a := range ParseMdstat(mdstat) {
		switch {
		case array == "*" && dsm && (a.Name == "md0" || a.Name == "md1"):
		case array == "*" || a.Name == array:
			out = append(out, RaidMeasurement(a))
		}
	}
	if len(out) == 0 {
		if array == "*" {
			return []Measurement{{Target: "*", Error: "this endpoint has no software RAID array (md) to check"}}
		}
		return []Measurement{{Target: array, Error: fmt.Sprintf("RAID array %s does not exist on this endpoint", array)}}
	}
	return out
}

// SmartReport is what smartctl reported for one disk.
type SmartReport struct {
	Standby       bool
	Model         string
	Health        string // PASSED, FAILED, OK; "" when smartctl reported none
	FailingNow    bool   // an attribute is at or below its threshold now
	Reallocated   int64
	Pending       int64
	Uncorrectable int64
	PowerOnHours  int64
	Temperature   int64 // °C; -1 when unknown
	NvmeWarning   int64 // the critical warning bits of an NVMe disk
	MediaErrors   int64
	HasAttributes bool
	Problem       string // the first line that says why SMART could not be read
}

var (
	smartHealth     = regexp.MustCompile(`(?i)SMART overall-health self-assessment test result:\s*(\S+)`)
	smartHealthSAS  = regexp.MustCompile(`(?i)SMART Health Status:\s*(\S+)`)
	smartModel      = regexp.MustCompile(`(?m)^(?:Device Model|Model Number|Product):\s*(.+)$`)
	smartNvmeTemp   = regexp.MustCompile(`(?m)^Temperature:\s+(\d+) Celsius`)
	smartNvmeWarn   = regexp.MustCompile(`(?m)^Critical Warning:\s+0x([0-9a-fA-F]+)`)
	smartNvmeMedia  = regexp.MustCompile(`(?m)^Media and Data Integrity Errors:\s+([\d,]+)`)
	smartNvmeHours  = regexp.MustCompile(`(?m)^Power On Hours:\s+([\d,]+)`)
	smartAttribute  = regexp.MustCompile(`^\s*(\d{1,3})\s+(\S+)\s+0x[0-9a-fA-F]{4}\s+\d+\s+\d+\s+\S+\s+\S+\s+\S+\s+(\S+)\s+(\S+)`)
	smartLeadingInt = regexp.MustCompile(`^\d+`)
	// DiskName is what the disk parameter may name, and what the agent passes to smartctl: sda, sata1, nvme0n1.
	DiskName = regexp.MustCompile(`^(sd[a-z]{1,3}|sata\d{1,3}|nvme\d{1,3}n\d{1,3})$`)
)

// ParseSmart reads the output of smartctl -n standby -i -H -A (smartctl 6 and 7, ATA, SAS and NVMe; text, because smartctl 6.5 on
// Synology DSM has no JSON).
func ParseSmart(output string) SmartReport {
	r := SmartReport{Temperature: -1}
	if strings.Contains(output, "Device is in STANDBY mode") || strings.Contains(output, "Device is in SLEEP mode") {
		r.Standby = true
		return r
	}
	if m := smartModel.FindStringSubmatch(output); m != nil {
		r.Model = strings.TrimSpace(m[1])
	}
	if m := smartHealth.FindStringSubmatch(output); m != nil {
		r.Health = strings.ToUpper(strings.TrimRight(m[1], "!."))
	} else if m := smartHealthSAS.FindStringSubmatch(output); m != nil {
		r.Health = strings.ToUpper(strings.TrimRight(m[1], "!."))
	}
	number := func(s string) int64 {
		n, _ := strconv.ParseInt(strings.ReplaceAll(s, ",", ""), 10, 64)
		return n
	}
	if m := smartNvmeTemp.FindStringSubmatch(output); m != nil {
		r.Temperature = number(m[1])
		r.HasAttributes = true
	}
	if m := smartNvmeWarn.FindStringSubmatch(output); m != nil {
		r.NvmeWarning, _ = strconv.ParseInt(m[1], 16, 64)
		r.HasAttributes = true
	}
	if m := smartNvmeMedia.FindStringSubmatch(output); m != nil {
		r.MediaErrors = number(m[1])
	}
	if m := smartNvmeHours.FindStringSubmatch(output); m != nil {
		r.PowerOnHours = number(m[1])
	}
	airflow := int64(-1)
	scanner := bufio.NewScanner(strings.NewReader(output))
	for scanner.Scan() {
		line := scanner.Text()
		m := smartAttribute.FindStringSubmatch(line)
		if m == nil {
			if r.Problem == "" && (strings.Contains(line, "failed:") || strings.Contains(line, "Unavailable") ||
				strings.Contains(line, "not supported") || strings.HasPrefix(line, "Smartctl open device")) {
				r.Problem = strings.TrimSpace(line)
			}
			continue
		}
		r.HasAttributes = true
		id, _ := strconv.Atoi(m[1])
		if strings.EqualFold(m[3], "FAILING_NOW") {
			r.FailingNow = true
		}
		raw := smartLeadingInt.FindString(m[4])
		value := number(raw)
		switch id {
		case 5:
			r.Reallocated = value
		case 9:
			r.PowerOnHours = value
		case 197:
			r.Pending = value
		case 198:
			r.Uncorrectable = value
		case 194:
			if raw != "" {
				r.Temperature = value
			}
		case 190:
			if raw != "" {
				airflow = value
			}
		}
	}
	if r.Temperature < 0 && airflow >= 0 {
		r.Temperature = airflow
	}
	return r
}

// HealthMeasurement judges one disk: failing when SMART says FAILED, an attribute is failing now or an NVMe disk raises a critical
// warning; a warning when sectors were reallocated, wait to be or could not be read, or an NVMe disk has media errors.
func HealthMeasurement(disk string, r SmartReport) Measurement {
	if r.Health == "" && !r.HasAttributes {
		problem := r.Problem
		if problem == "" {
			problem = "smartctl reported no SMART data"
		}
		return Measurement{Target: disk, Error: fmt.Sprintf("SMART of %s could not be read: %s", disk, problem)}
	}
	health := r.Health
	if health == "" {
		health = "no overall assessment"
	}
	var detail string
	if strings.HasPrefix(disk, "nvme") {
		detail = fmt.Sprintf("%s, %d media errors", health, r.MediaErrors)
	} else {
		detail = fmt.Sprintf("%s, %d reallocated, %d pending, %d uncorrectable sectors", health, r.Reallocated, r.Pending, r.Uncorrectable)
	}
	if r.PowerOnHours > 0 {
		detail += fmt.Sprintf(", %d power-on hours", r.PowerOnHours)
	}
	if r.Model != "" {
		detail = r.Model + ": " + detail
	}
	value := DiskHealthy
	switch {
	case r.Health == "FAILED" || r.FailingNow || r.NvmeWarning != 0:
		value = DiskFailing
	case r.Reallocated > 0 || r.Pending > 0 || r.Uncorrectable > 0 || r.MediaErrors > 0:
		value = DiskWarning
	}
	return Measurement{Target: disk, Value: float64(value), Detail: detail}
}

// TemperatureMeasurement is the temperature of one disk in °C.
func TemperatureMeasurement(disk string, r SmartReport) Measurement {
	if r.Temperature < 0 {
		problem := r.Problem
		if problem == "" {
			problem = "smartctl reported no temperature"
		}
		return Measurement{Target: disk, Error: fmt.Sprintf("the temperature of %s could not be read: %s", disk, problem)}
	}
	detail := fmt.Sprintf("%d °C", r.Temperature)
	if r.Model != "" {
		detail = r.Model + ": " + detail
	}
	return Measurement{Target: disk, Value: float64(r.Temperature), Detail: detail}
}
