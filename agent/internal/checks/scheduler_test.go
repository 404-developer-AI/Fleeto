package checks

import (
	"context"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func TestFirstDelayIsWithinTheInterval(t *testing.T) {
	for _, interval := range []time.Duration{time.Second, time.Minute, 24 * time.Hour} {
		for range 2000 {
			d := FirstDelay(interval)
			if d < 0 || d >= interval {
				t.Fatalf("first delay %v outside [0, %v)", d, interval)
			}
		}
	}
}

func TestNextDelayStaysWithinTenPercent(t *testing.T) {
	for _, interval := range []time.Duration{10 * time.Second, time.Minute, time.Hour, 30 * 24 * time.Hour} {
		low, high := interval-interval/10, interval+interval/10
		sawBelow, sawAbove := false, false
		for range 5000 {
			d := NextDelay(interval)
			if d < low || d > high {
				t.Fatalf("next delay %v outside [%v, %v]", d, low, high)
			}
			sawBelow = sawBelow || d < interval
			sawAbove = sawAbove || d > interval
		}
		if !sawBelow || !sawAbove {
			t.Fatalf("expected jitter in both directions for %v", interval)
		}
	}
}

type countingCollector struct{ calls atomic.Int32 }

func (c *countingCollector) Collect(context.Context, *agentv1.CheckSpec) []Measurement {
	c.calls.Add(1)
	return []Measurement{{Value: 42, Target: "x"}}
}

type panickingCollector struct{}

func (panickingCollector) Collect(context.Context, *agentv1.CheckSpec) []Measurement { panic("boom") }

type sinkRecorder struct {
	mu      sync.Mutex
	results []*agentv1.CheckResult
}

func (s *sinkRecorder) sink(r []*agentv1.CheckResult) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.results = append(s.results, r...)
}

func (s *sinkRecorder) count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.results)
}

func config(tier agentv1.Tier, version uint64) *agentv1.AgentConfig {
	return &agentv1.AgentConfig{Version: version, Tier: tier, Checks: []*agentv1.CheckSpec{
		{Id: "c1", Type: agentv1.CheckType_CHECK_TYPE_MEMORY_USAGE, IntervalSeconds: 1},
		{Id: "c2", Type: agentv1.CheckType_CHECK_TYPE_UPTIME, IntervalSeconds: 1},
	}}
}

func waitFor(t *testing.T, timeout time.Duration, cond func() bool) {
	t.Helper()
	deadline := time.Now().Add(timeout)
	for time.Now().Before(deadline) {
		if cond() {
			return
		}
		time.Sleep(20 * time.Millisecond)
	}
	t.Fatal("condition not met in time")
}

func TestManagedConfigRunsChecksAndStampsResults(t *testing.T) {
	collector := &countingCollector{}
	rec := &sinkRecorder{}
	s := NewScheduler(collector, rec.sink, logging.Discard())
	defer s.Stop()
	s.Apply(config(agentv1.Tier_TIER_MANAGED, 7))
	waitFor(t, 3*time.Second, func() bool { return rec.count() >= 2 })
	rec.mu.Lock()
	r := rec.results[0]
	rec.mu.Unlock()
	if r.GetConfigVersion() != 7 || r.GetCollectedAt() == nil || (r.GetCheckId() != "c1" && r.GetCheckId() != "c2") {
		t.Fatalf("unexpected result %v", r)
	}
}

func TestAgentOnlyConfigWithChecksRunsNothing(t *testing.T) {
	for _, tier := range []agentv1.Tier{agentv1.Tier_TIER_AGENT_ONLY, agentv1.Tier_TIER_UNSPECIFIED} {
		collector := &countingCollector{}
		rec := &sinkRecorder{}
		s := NewScheduler(collector, rec.sink, logging.Discard())
		s.Apply(config(tier, 3))
		if s.Count() != 0 {
			t.Fatalf("tier %v scheduled %d checks", tier, s.Count())
		}
		time.Sleep(1500 * time.Millisecond)
		s.Stop()
		if collector.calls.Load() != 0 || rec.count() != 0 {
			t.Fatalf("tier %v ran checks: %d calls", tier, collector.calls.Load())
		}
	}
}

func TestSwitchingToAgentOnlyStopsRunningChecks(t *testing.T) {
	collector := &countingCollector{}
	rec := &sinkRecorder{}
	s := NewScheduler(collector, rec.sink, logging.Discard())
	defer s.Stop()
	s.Apply(config(agentv1.Tier_TIER_MANAGED, 1))
	waitFor(t, 3*time.Second, func() bool { return collector.calls.Load() > 0 })
	s.Apply(config(agentv1.Tier_TIER_AGENT_ONLY, 2))
	time.Sleep(100 * time.Millisecond)
	before := collector.calls.Load()
	time.Sleep(1500 * time.Millisecond)
	if after := collector.calls.Load(); after != before {
		t.Fatalf("checks kept running after switching to agent-only: %d -> %d", before, after)
	}
}

func TestPanickingCollectorReportsAnErrorResult(t *testing.T) {
	rec := &sinkRecorder{}
	s := NewScheduler(panickingCollector{}, rec.sink, logging.Discard())
	defer s.Stop()
	s.Apply(config(agentv1.Tier_TIER_MANAGED, 1))
	waitFor(t, 3*time.Second, func() bool { return rec.count() > 0 })
	rec.mu.Lock()
	defer rec.mu.Unlock()
	if rec.results[0].GetError() == "" {
		t.Fatal("expected an error result")
	}
}

func TestCPUSampleWindow(t *testing.T) {
	cases := []struct {
		params   map[string]string
		interval uint32
		want     time.Duration
	}{
		{nil, 300, 60 * time.Second},
		{map[string]string{"sample_seconds": "10"}, 300, 10 * time.Second},
		{map[string]string{"sample_seconds": "600"}, 300, 300 * time.Second},
		{nil, 30, 30 * time.Second},
		{map[string]string{"sample_seconds": "abc"}, 300, 60 * time.Second},
	}
	for _, c := range cases {
		got := CPUSampleWindow(&agentv1.CheckSpec{Parameters: c.params, IntervalSeconds: c.interval})
		if got != c.want {
			t.Errorf("params %v interval %d: got %v want %v", c.params, c.interval, got, c.want)
		}
	}
}

func TestFormatBytes(t *testing.T) {
	cases := map[uint64]string{
		500:                           "500 B",
		13207024435:                   "12.3 GB",
		254476812288:                  "237 GB",
		512 * 1024 * 1024:             "512 MB",
		2 * 1024 * 1024 * 1024 * 1024: "2.0 TB",
	}
	for in, want := range cases {
		if got := FormatBytes(in); got != want {
			t.Errorf("FormatBytes(%d) = %q, want %q", in, got, want)
		}
	}
}

func dailyConfig(tier agentv1.Tier, ids ...string) *agentv1.AgentConfig {
	cfg := &agentv1.AgentConfig{Version: 1, Tier: tier}
	for _, id := range ids {
		cfg.Checks = append(cfg.Checks, &agentv1.CheckSpec{Id: id, Type: agentv1.CheckType_CHECK_TYPE_UPTIME, IntervalSeconds: 86400})
	}
	return cfg
}

func TestRunNowRunsOnlyScheduledChecks(t *testing.T) {
	collector := &countingCollector{}
	rec := &sinkRecorder{}
	s := NewScheduler(collector, rec.sink, logging.Discard())
	defer s.Stop()
	s.Apply(dailyConfig(agentv1.Tier_TIER_MANAGED, "c1", "c2"))

	started, dropped := s.RunNow([]string{"c1", "unknown", "c1"})
	if started != 1 || dropped != 0 {
		t.Fatalf("started %d dropped %d, want 1 and 0", started, dropped)
	}
	waitFor(t, 3*time.Second, func() bool { return rec.count() == 1 })
	time.Sleep(200 * time.Millisecond)
	rec.mu.Lock()
	defer rec.mu.Unlock()
	if len(rec.results) != 1 || rec.results[0].GetCheckId() != "c1" {
		t.Fatalf("unexpected results %v", rec.results)
	}
}

func TestRunNowDoesNothingWithoutAManagedConfig(t *testing.T) {
	collector := &countingCollector{}
	s := NewScheduler(collector, (&sinkRecorder{}).sink, logging.Discard())
	defer s.Stop()
	s.Apply(dailyConfig(agentv1.Tier_TIER_AGENT_ONLY, "c1"))
	if started, _ := s.RunNow([]string{"c1"}); started != 0 {
		t.Fatalf("an agent-only configuration started %d checks", started)
	}
	time.Sleep(200 * time.Millisecond)
	if collector.calls.Load() != 0 {
		t.Fatal("a check ran on an agent-only configuration")
	}
}

func TestRunNowIsRateLimitedPerCheck(t *testing.T) {
	collector := &countingCollector{}
	s := NewScheduler(collector, (&sinkRecorder{}).sink, logging.Discard())
	defer s.Stop()
	var clock atomic.Int64 // read by the runner goroutine as well
	clock.Store(time.Date(2026, 9, 14, 12, 0, 0, 0, time.UTC).UnixNano())
	s.now = func() time.Time { return time.Unix(0, clock.Load()) }
	s.Apply(dailyConfig(agentv1.Tier_TIER_MANAGED, "c1"))

	if started, _ := s.RunNow([]string{"c1"}); started != 1 {
		t.Fatal("the first request did not start the check")
	}
	waitFor(t, 3*time.Second, func() bool { return collector.calls.Load() == 1 })
	clock.Add(int64(MinRunNowGap - time.Second))
	if started, dropped := s.RunNow([]string{"c1"}); started != 0 || dropped != 1 {
		t.Fatalf("a request within the gap started %d, dropped %d", started, dropped)
	}
	clock.Add(int64(2 * time.Second))
	if started, _ := s.RunNow([]string{"c1"}); started != 1 {
		t.Fatal("a request after the gap did not start the check")
	}
	waitFor(t, 3*time.Second, func() bool { return collector.calls.Load() == 2 })
}

func TestRunNowIsRateLimitedInTotal(t *testing.T) {
	ids := make([]string, 0, MaxRunNowPerMinute+5)
	for i := range MaxRunNowPerMinute + 5 {
		ids = append(ids, "c"+string(rune('a'+i)))
	}
	s := NewScheduler(&countingCollector{}, (&sinkRecorder{}).sink, logging.Discard())
	defer s.Stop()
	s.Apply(dailyConfig(agentv1.Tier_TIER_MANAGED, ids...))
	started, dropped := s.RunNow(ids)
	if started != MaxRunNowPerMinute || dropped != 5 {
		t.Fatalf("started %d dropped %d, want %d and 5", started, dropped, MaxRunNowPerMinute)
	}
}
