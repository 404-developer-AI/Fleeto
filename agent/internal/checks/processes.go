package checks

import (
	"context"
	"math"
	"runtime"
	"sort"
	"strconv"
	"strings"
	"time"

	"github.com/shirou/gopsutil/v4/process"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Process list of the CPU and memory usage checks (0.6.0): a result that reaches the value the server set in
// "process_list_at" (the lowest threshold of the check), and every manual run, carries the processes using the most CPU or
// memory. Below that value nothing is listed, so a healthy endpoint pays only for its CPU start snapshot. Names, users and
// memory are read for the listed processes only. User names are personal data and are never logged.

const (
	// ProcessListAtParameter is the check parameter, set by the server only, from which a result carries its process list.
	ProcessListAtParameter = "process_list_at"
	// MaxListedProcesses is the length of the process list.
	MaxListedProcesses = 10
	// maxScannedProcesses bounds the work on an endpoint with a runaway number of processes.
	maxScannedProcesses = 4000
)

// processListAt returns the value from which a result carries its process list, and false when the check has none.
func processListAt(spec *agentv1.CheckSpec) (float64, bool) {
	v, ok := spec.GetParameters()[ProcessListAtParameter]
	if !ok {
		return 0, false
	}
	at, err := strconv.ParseFloat(strings.TrimSpace(v), 64)
	if err != nil || math.IsNaN(at) || math.IsInf(at, 0) {
		return 0, false
	}
	return at, true
}

// processListWanted reports whether a run may need a process list: a manual run, or a check with "process_list_at".
func processListWanted(ctx context.Context, spec *agentv1.CheckSpec) bool {
	if IsManualRun(ctx) {
		return true
	}
	_, ok := processListAt(spec)
	return ok
}

// processListDue reports whether a result with value carries a process list.
func processListDue(ctx context.Context, spec *agentv1.CheckSpec, value float64) bool {
	if IsManualRun(ctx) {
		return true
	}
	at, ok := processListAt(spec)
	return ok && value >= at
}

// cpuSnapshot is the CPU time (user + system, seconds) per process at one moment.
type cpuSnapshot struct {
	at    time.Time
	times map[int32]float64
}

func takeCPUSnapshot(ctx context.Context) (cpuSnapshot, []*process.Process) {
	procs := listProcesses(ctx)
	snap := cpuSnapshot{at: time.Now(), times: make(map[int32]float64, len(procs))}
	for _, p := range procs {
		if t, err := p.TimesWithContext(ctx); err == nil && t != nil {
			snap.times[p.Pid] = t.User + t.System
		}
	}
	return snap, procs
}

func listProcesses(ctx context.Context) []*process.Process {
	procs, err := process.ProcessesWithContext(ctx)
	if err != nil {
		return nil
	}
	out := procs[:0]
	for _, p := range procs {
		// PID 0 is the idle process on Windows (its CPU time is idle time) and the scheduler on Linux.
		if p.Pid > 0 {
			out = append(out, p)
		}
	}
	if len(out) > maxScannedProcesses {
		out = out[:maxScannedProcesses]
	}
	return out
}

// share is a process with the number it is ranked by.
type share struct {
	pid   int32
	value float64
}

// rankByCPU returns the processes by their share of the whole machine between two snapshots, in percent, highest first.
// A process missing from the first snapshot started in between, so all of its CPU time counts.
func rankByCPU(first, second cpuSnapshot, cores int) []share {
	elapsed := second.at.Sub(first.at).Seconds()
	if elapsed <= 0 || cores < 1 {
		return nil
	}
	out := make([]share, 0, len(second.times))
	for pid, t2 := range second.times {
		used := t2 - first.times[pid]
		if used <= 0 {
			continue
		}
		percent := used / elapsed / float64(cores) * 100
		out = append(out, share{pid: pid, value: math.Min(round(percent, 1), 100)})
	}
	sortShares(out)
	return out
}

func sortShares(s []share) {
	sort.Slice(s, func(i, j int) bool {
		if s[i].value != s[j].value {
			return s[i].value > s[j].value
		}
		return s[i].pid < s[j].pid
	})
}

// topByCPU lists the processes that used the most CPU since first.
func topByCPU(ctx context.Context, first cpuSnapshot) []*agentv1.ProcessSample {
	second, procs := takeCPUSnapshot(ctx)
	ranked := rankByCPU(first, second, runtime.NumCPU())
	return describe(ctx, ranked, procs, func(s *agentv1.ProcessSample, value float64) { s.CpuPercent = value })
}

// topByMemory lists the processes using the most physical memory now.
func topByMemory(ctx context.Context) []*agentv1.ProcessSample {
	procs := listProcesses(ctx)
	ranked := make([]share, 0, len(procs))
	for _, p := range procs {
		if m, err := p.MemoryInfoWithContext(ctx); err == nil && m != nil && m.RSS > 0 {
			ranked = append(ranked, share{pid: p.Pid, value: float64(m.RSS)})
		}
	}
	sortShares(ranked)
	return describe(ctx, ranked, procs, nil)
}

// describe turns the first MaxListedProcesses ranked processes that still run into samples with name, user and memory.
func describe(ctx context.Context, ranked []share, procs []*process.Process, set func(*agentv1.ProcessSample, float64)) []*agentv1.ProcessSample {
	byPid := make(map[int32]*process.Process, len(procs))
	for _, p := range procs {
		byPid[p.Pid] = p
	}
	out := make([]*agentv1.ProcessSample, 0, MaxListedProcesses)
	for _, r := range ranked {
		if len(out) == MaxListedProcesses || ctx.Err() != nil {
			break
		}
		p, ok := byPid[r.pid]
		if !ok {
			continue
		}
		name, err := p.NameWithContext(ctx)
		if err != nil || name == "" {
			continue
		}
		sample := &agentv1.ProcessSample{Pid: uint32(r.pid), Name: name}
		if user, err := p.UsernameWithContext(ctx); err == nil {
			sample.User = user
		}
		if m, err := p.MemoryInfoWithContext(ctx); err == nil && m != nil {
			sample.MemoryBytes = m.RSS
		}
		if set != nil {
			set(sample, r.value)
		}
		out = append(out, sample)
	}
	return out
}
