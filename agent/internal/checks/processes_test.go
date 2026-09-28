package checks

import (
	"context"
	"os"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func specWith(parameters map[string]string) *agentv1.CheckSpec {
	return &agentv1.CheckSpec{Id: "c", Type: agentv1.CheckType_CHECK_TYPE_CPU_USAGE, IntervalSeconds: 60, Parameters: parameters}
}

func TestProcessListFollowsTheServerThreshold(t *testing.T) {
	ctx := context.Background()
	spec := specWith(map[string]string{ProcessListAtParameter: "85"})
	if !processListWanted(ctx, spec) {
		t.Fatal("a check with process_list_at wants a process list")
	}
	if processListDue(ctx, spec, 84.9) {
		t.Fatal("a value below the threshold carries no process list")
	}
	if !processListDue(ctx, spec, 85) || !processListDue(ctx, spec, 99) {
		t.Fatal("a value at or above the threshold carries a process list")
	}
}

func TestProcessListWithoutThresholdOnlyOnAManualRun(t *testing.T) {
	for _, parameters := range []map[string]string{nil, {ProcessListAtParameter: ""}, {ProcessListAtParameter: "NaN"}, {ProcessListAtParameter: "high"}} {
		spec := specWith(parameters)
		if processListWanted(context.Background(), spec) || processListDue(context.Background(), spec, 100) {
			t.Fatalf("parameters %v: a scheduled run listed processes", parameters)
		}
		manual := context.WithValue(context.Background(), manualRunKey{}, true)
		if !processListWanted(manual, spec) || !processListDue(manual, spec, 1) {
			t.Fatalf("parameters %v: a manual run did not list processes", parameters)
		}
	}
}

func TestRankByCPUIsTheShareOfTheWholeMachine(t *testing.T) {
	start := time.Unix(1000, 0)
	first := cpuSnapshot{at: start, times: map[int32]float64{10: 5, 20: 100, 30: 7}}
	second := cpuSnapshot{at: start.Add(10 * time.Second), times: map[int32]float64{
		10: 25,  // 20 s of CPU in 10 s on 4 cores: 50 %
		20: 104, // 4 s: 10 %
		30: 7,   // idle: left out
		40: 2,   // started during the window: 2 s counts, 5 %
		50: 500, // cannot exceed the machine
	}}
	ranked := rankByCPU(first, second, 4)
	want := []share{{50, 100}, {10, 50}, {20, 10}, {40, 5}}
	if len(ranked) != len(want) {
		t.Fatalf("ranked %v, want %v", ranked, want)
	}
	for i := range want {
		if ranked[i] != want[i] {
			t.Fatalf("ranked %v, want %v", ranked, want)
		}
	}
	if rankByCPU(first, first, 4) != nil {
		t.Fatal("no elapsed time must give no ranking")
	}
}

func TestTopByMemoryListsRealProcessesHighestFirst(t *testing.T) {
	top := topByMemory(context.Background())
	if len(top) == 0 || len(top) > MaxListedProcesses {
		t.Fatalf("listed %d processes", len(top))
	}
	for i, p := range top {
		if p.GetName() == "" || p.GetPid() == 0 {
			t.Fatalf("process %d has no name or pid: %v", i, p)
		}
		if i > 0 && p.GetMemoryBytes() > top[i-1].GetMemoryBytes() {
			t.Fatalf("not sorted by memory: %v", top)
		}
	}
}

func TestTopByCPUIncludesABusyProcess(t *testing.T) {
	first, _ := takeCPUSnapshot(context.Background())
	stop := make(chan struct{})
	go func() {
		for {
			select {
			case <-stop:
				return
			default:
			}
		}
	}()
	time.Sleep(700 * time.Millisecond)
	close(stop)
	top := topByCPU(context.Background(), first)
	for _, p := range top {
		if int(p.GetPid()) == os.Getpid() {
			if p.GetCpuPercent() <= 0 || p.GetMemoryBytes() == 0 {
				t.Fatalf("the busy test process has no CPU or memory: %v", p)
			}
			return
		}
	}
	t.Fatalf("the busy test process is not in the list: %v", top)
}

type manualRecorder struct{ manual chan bool }

func (c manualRecorder) Collect(ctx context.Context, _ *agentv1.CheckSpec) []Measurement {
	c.manual <- IsManualRun(ctx)
	return []Measurement{{Value: 1, Processes: []*agentv1.ProcessSample{{Pid: 7, Name: "p"}}}}
}

func TestRunNowMarksTheRunAsManualAndSendsTheProcessList(t *testing.T) {
	collector := manualRecorder{manual: make(chan bool, 4)}
	rec := &sinkRecorder{}
	s := NewScheduler(collector, rec.sink, logging.Discard())
	defer s.Stop()
	s.Apply(dailyConfig(agentv1.Tier_TIER_MANAGED, "c1"))
	if started, _ := s.RunNow([]string{"c1"}); started != 1 {
		t.Fatal("the manual run did not start")
	}
	select {
	case manual := <-collector.manual:
		if !manual {
			t.Fatal("a run started by RunNow is not marked manual")
		}
	case <-time.After(3 * time.Second):
		t.Fatal("the check did not run")
	}
	waitFor(t, 3*time.Second, func() bool { return rec.count() == 1 })
	rec.mu.Lock()
	defer rec.mu.Unlock()
	if len(rec.results[0].GetProcesses()) != 1 || rec.results[0].GetProcesses()[0].GetName() != "p" {
		t.Fatalf("the process list did not reach the result: %v", rec.results[0])
	}
}
