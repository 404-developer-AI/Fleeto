// Package checks runs the checks of the signed configuration on their own schedule, also while the agent is offline.
package checks

import (
	"context"
	"log/slog"
	"maps"
	"math/rand/v2"
	"sync"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

// MinInterval is the shortest interval the agent runs a check at; shorter values are raised to it.
const MinInterval = time.Second

// Limits on runs requested by the server (RunChecksNow). The request is not signed, so a misbehaving gateway must not be
// able to turn checks into a busy loop: at most one manual run per check per MinRunNowGap, MaxRunNowPerMinute manual runs
// in total per minute, and MaxRunNowIDs ids per request.
const (
	MinRunNowGap       = 30 * time.Second
	MaxRunNowPerMinute = 20
	MaxRunNowIDs       = 100
)

// Measurement is one result of a check run, before the scheduler stamps check id, version and time.
type Measurement struct {
	Value  float64
	Target string
	Detail string
	Error  string
}

// Collector measures a check. It must honour ctx and never panic (the scheduler recovers anyway).
type Collector interface {
	Collect(ctx context.Context, spec *agentv1.CheckSpec) []Measurement
}

// Sink receives results. It must not block for long.
type Sink func(results []*agentv1.CheckResult)

// FirstDelay returns a random delay in [0, interval), so agents that start together do not run in the same second.
func FirstDelay(interval time.Duration) time.Duration {
	if interval <= 0 {
		return 0
	}
	return time.Duration(rand.Int64N(int64(interval)))
}

// NextDelay returns interval with up to ±10 % random jitter.
func NextDelay(interval time.Duration) time.Duration {
	if interval <= 0 {
		return MinInterval
	}
	spread := int64(interval) / 10
	if spread == 0 {
		return interval
	}
	d := interval + time.Duration(rand.Int64N(2*spread+1)-spread)
	if d < MinInterval {
		d = MinInterval
	}
	return d
}

// Scheduler runs one goroutine per check.
type Scheduler struct {
	collector Collector
	sink      Sink
	logger    *slog.Logger
	now       func() time.Time

	mu      sync.Mutex
	runners map[string]*runner
	stopped bool

	// Manual runs in the current one-minute window (RunNow).
	manualWindow time.Time
	manualCount  int
}

type runner struct {
	spec    *agentv1.CheckSpec
	cancel  context.CancelFunc
	done    chan struct{}
	trigger chan struct{} // capacity 1: repeated requests before the run starts coalesce
	mu      sync.Mutex
	version uint64
	// Guarded by Scheduler.mu.
	lastManual time.Time
}

// NewScheduler returns a scheduler without checks.
func NewScheduler(collector Collector, sink Sink, logger *slog.Logger) *Scheduler {
	return &Scheduler{collector: collector, sink: sink, logger: logger, now: time.Now, runners: map[string]*runner{}}
}

// Apply replaces the running checks with those of cfg. Unchanged checks keep their schedule and only report the new
// configuration version. Agent-only (or unspecified) configurations stop every check.
func (s *Scheduler) Apply(cfg *agentv1.AgentConfig) {
	specs := signedconfig.EffectiveChecks(cfg)
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.stopped {
		return
	}
	wanted := map[string]*agentv1.CheckSpec{}
	for _, spec := range specs {
		if spec.GetId() == "" || spec.GetIntervalSeconds() == 0 {
			s.logger.Warn("ignored a check without id or interval", "checkId", spec.GetId())
			continue
		}
		wanted[spec.GetId()] = spec
	}
	for id, r := range s.runners {
		spec, keep := wanted[id]
		if keep && sameSpec(r.spec, spec) {
			r.mu.Lock()
			r.version = cfg.GetVersion()
			r.mu.Unlock()
			delete(wanted, id)
			continue
		}
		r.cancel()
		delete(s.runners, id)
	}
	for id, spec := range wanted {
		ctx, cancel := context.WithCancel(context.Background())
		r := &runner{spec: spec, cancel: cancel, done: make(chan struct{}), trigger: make(chan struct{}, 1), version: cfg.GetVersion()}
		s.runners[id] = r
		safego.Go(s.logger, "check "+id, func() { s.run(ctx, r) })
	}
	s.logger.Info("checks applied", "configVersion", cfg.GetVersion(), "tier", cfg.GetTier().String(), "checks", len(s.runners))
}

// RunNow starts the named checks at once, outside their schedule. Only checks of the applied signed configuration can
// run: ids that are not scheduled (unknown, or an agent-only configuration without checks) are ignored. Rate limits apply
// (MinRunNowGap per check, MaxRunNowPerMinute in total). It returns how many checks were started and how many were
// dropped by a limit.
func (s *Scheduler) RunNow(ids []string) (started, dropped int) {
	if len(ids) > MaxRunNowIDs {
		dropped += len(ids) - MaxRunNowIDs
		ids = ids[:MaxRunNowIDs]
	}
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.stopped {
		return 0, len(ids)
	}
	now := s.now()
	if now.Sub(s.manualWindow) >= time.Minute || now.Before(s.manualWindow) {
		s.manualWindow = now
		s.manualCount = 0
	}
	seen := map[string]bool{}
	for _, id := range ids {
		r, ok := s.runners[id]
		if !ok || seen[id] {
			continue
		}
		seen[id] = true
		if s.manualCount >= MaxRunNowPerMinute || (!r.lastManual.IsZero() && now.Sub(r.lastManual) < MinRunNowGap && !now.Before(r.lastManual)) {
			dropped++
			continue
		}
		select {
		case r.trigger <- struct{}{}:
			r.lastManual = now
			s.manualCount++
			started++
		default:
			// A run requested earlier has not started yet; this request is served by it.
		}
	}
	return started, dropped
}

// Count returns the number of scheduled checks.
func (s *Scheduler) Count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.runners)
}

// Stop cancels every check and waits for running collectors to return.
func (s *Scheduler) Stop() {
	s.mu.Lock()
	s.stopped = true
	runners := make([]*runner, 0, len(s.runners))
	for _, r := range s.runners {
		r.cancel()
		runners = append(runners, r)
	}
	s.runners = map[string]*runner{}
	s.mu.Unlock()
	for _, r := range runners {
		<-r.done
	}
}

func sameSpec(a, b *agentv1.CheckSpec) bool {
	return a.GetId() == b.GetId() && a.GetType() == b.GetType() && a.GetIntervalSeconds() == b.GetIntervalSeconds() &&
		maps.Equal(a.GetParameters(), b.GetParameters())
}

func (s *Scheduler) run(ctx context.Context, r *runner) {
	defer close(r.done)
	interval := time.Duration(r.spec.GetIntervalSeconds()) * time.Second
	if interval < MinInterval {
		interval = MinInterval
	}
	timer := time.NewTimer(FirstDelay(interval))
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-timer.C:
		case <-r.trigger:
		}
		s.runOnce(ctx, r)
		if ctx.Err() != nil {
			return
		}
		// A manual run counts as a run: the next scheduled one follows an interval later.
		timer.Reset(NextDelay(interval))
	}
}

func (s *Scheduler) runOnce(ctx context.Context, r *runner) {
	r.mu.Lock()
	version := r.version
	r.mu.Unlock()
	var measurements []Measurement
	err := safego.Call(s.logger, "check "+r.spec.GetId(), func() error {
		measurements = s.collector.Collect(ctx, r.spec)
		return nil
	})
	if ctx.Err() != nil {
		// Cancelled by a new configuration or shutdown: a partial measurement is not a result.
		return
	}
	if err != nil {
		measurements = []Measurement{{Error: "the check failed inside the agent; see the agent log"}}
	}
	collected := timestamppb.New(s.now())
	results := make([]*agentv1.CheckResult, 0, len(measurements))
	for _, m := range measurements {
		results = append(results, &agentv1.CheckResult{
			CheckId: r.spec.GetId(), ConfigVersion: version, CollectedAt: collected,
			Value: m.Value, Target: m.Target, Detail: m.Detail, Error: m.Error,
		})
	}
	if len(results) > 0 {
		s.sink(results)
	}
}
