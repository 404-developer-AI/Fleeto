package remote

import (
	"context"
	"errors"
	"fmt"
	"runtime"
	"sort"
	"time"

	"github.com/shirou/gopsutil/v4/process"
)

// Processes in the remote background window (0.3.0 step 2): the endpoint lists its processes with CPU, memory and user, and ends one.
// Ending a process is audited; listing is not. CPU is sampled over a short interval so it reflects the moment, not the whole lifetime.

const (
	maxProcesses   = 4000
	cpuSampleDelay = 400 * time.Millisecond
)

type processItem struct {
	Pid    int32   `json:"pid"`
	Name   string  `json:"name"`
	User   string  `json:"user"`
	CPU    float64 `json:"cpu"`
	Memory int64   `json:"memory"`
}

func (b *background) processes(ctx context.Context) (map[string]any, error) {
	procs, err := process.ProcessesWithContext(ctx)
	if err != nil {
		return nil, fmt.Errorf("the processes could not be listed: %w", err)
	}
	if len(procs) > maxProcesses {
		procs = procs[:maxProcesses]
	}

	// Two snapshots of CPU time, so the percentage is of the sample window, not the process lifetime.
	first := cpuTimes(ctx, procs)
	select {
	case <-ctx.Done():
		return nil, ctx.Err()
	case <-time.After(cpuSampleDelay):
	}
	second := cpuTimes(ctx, procs)
	elapsed := cpuSampleDelay.Seconds()
	cores := float64(runtime.NumCPU())

	items := make([]processItem, 0, len(procs))
	for _, p := range procs {
		name, err := p.NameWithContext(ctx)
		if err != nil || name == "" {
			continue
		}
		item := processItem{Pid: p.Pid, Name: name}
		if user, err := p.UsernameWithContext(ctx); err == nil {
			item.User = user
		}
		if mem, err := p.MemoryInfoWithContext(ctx); err == nil && mem != nil {
			item.Memory = int64(mem.RSS)
		}
		if t1, ok := first[p.Pid]; ok {
			if t2, ok := second[p.Pid]; ok && elapsed > 0 {
				percent := (t2 - t1) / elapsed * 100
				item.CPU = clampCPU(percent, cores)
			}
		}
		items = append(items, item)
	}
	sort.Slice(items, func(i, j int) bool {
		if items[i].CPU != items[j].CPU {
			return items[i].CPU > items[j].CPU
		}
		return items[i].Memory > items[j].Memory
	})
	return map[string]any{"processes": items}, nil
}

func cpuTimes(ctx context.Context, procs []*process.Process) map[int32]float64 {
	out := make(map[int32]float64, len(procs))
	for _, p := range procs {
		if t, err := p.TimesWithContext(ctx); err == nil && t != nil {
			out[p.Pid] = t.User + t.System
		}
	}
	return out
}

func clampCPU(percent, cores float64) float64 {
	if percent < 0 {
		return 0
	}
	max := 100 * cores
	if percent > max {
		return max
	}
	// One decimal place is enough for a list.
	return float64(int(percent*10+0.5)) / 10
}

func (b *background) processAction(req requestBody) (map[string]any, error) {
	if req.Action != "end" {
		return nil, fmt.Errorf("%q is not a process action", req.Action)
	}
	if req.Pid <= 0 {
		return nil, errors.New("choose a process")
	}
	p, err := process.NewProcess(req.Pid)
	if err != nil {
		return nil, errors.New("that process is no longer running")
	}
	name, _ := p.Name()
	if err := p.Kill(); err != nil {
		return nil, fmt.Errorf("the process could not be ended: %w", err)
	}
	b.report("process.end", fmt.Sprintf("%s (pid %d)", name, req.Pid), "")
	return nil, nil
}
