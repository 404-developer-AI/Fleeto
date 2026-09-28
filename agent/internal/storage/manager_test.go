package storage

import (
	"context"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"sync"
	"sync/atomic"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

type fakeClock struct {
	mu  sync.Mutex
	now time.Time
}

func (c *fakeClock) Now() time.Time {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.now
}

func (c *fakeClock) Advance(d time.Duration) {
	c.mu.Lock()
	c.now = c.now.Add(d)
	c.mu.Unlock()
}

type harness struct {
	m       *Manager
	clock   *fakeClock
	scans   atomic.Int32
	release chan struct{}
	managed atomic.Bool
	hours   atomic.Uint32
	saved   time.Time
}

func newHarness(t *testing.T, dir string, last time.Time, block bool) *harness {
	t.Helper()
	h := &harness{clock: &fakeClock{now: time.Date(2026, 9, 28, 10, 0, 0, 0, time.UTC)}, release: make(chan struct{})}
	h.managed.Store(true)
	h.hours.Store(24)
	if !block {
		close(h.release)
	}
	m, err := NewManager(Options{
		Dir:           dir,
		Access:        platform.AccessCurrentUser,
		Logger:        slog.New(slog.NewTextHandler(io.Discard, nil)),
		Now:           h.clock.Now,
		Managed:       h.managed.Load,
		IntervalHours: h.hours.Load,
		LastStarted:   func() time.Time { return last },
		SaveStarted:   func(at time.Time) error { h.saved = at; return nil },
		FirstDelayMax: time.Hour,
		Scan: func(ctx context.Context, requestID string) []*agentv1.StorageScanReport {
			h.scans.Add(1)
			<-h.release
			return []*agentv1.StorageScanReport{{ScanId: newUUID(), Volume: "C:", RequestId: requestID}}
		},
	})
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(m.Close)
	h.m = m
	return h
}

func waitIdle(t *testing.T, m *Manager) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for m.Running() {
		if time.Now().After(deadline) {
			t.Fatal("the scan did not finish")
		}
		time.Sleep(5 * time.Millisecond)
	}
}

func TestRequestsFollowTheRules(t *testing.T) {
	h := newHarness(t, t.TempDir(), time.Time{}, true)
	h.managed.Store(false)
	if ok, reason := h.m.Request("r0"); ok || reason != "the endpoint is not managed" {
		t.Fatalf("agent-only: %v %q", ok, reason)
	}
	h.managed.Store(true)
	if ok, _ := h.m.Request("r1"); !ok {
		t.Fatal("the first request starts a scan")
	}
	if ok, reason := h.m.Request("r2"); ok || reason != "a storage scan is running" {
		t.Fatalf("while running: %v %q", ok, reason)
	}
	close(h.release)
	waitIdle(t, h.m)
	h.clock.Advance(14 * time.Minute)
	if ok, reason := h.m.Request("r3"); ok || reason != "a storage scan started less than 15 minutes ago" {
		t.Fatalf("within 15 minutes: %v %q", ok, reason)
	}
	h.clock.Advance(time.Minute)
	if ok, _ := h.m.Request("r4"); !ok {
		t.Fatal("after 15 minutes a request starts a scan")
	}
	waitIdle(t, h.m)
	pending := h.m.Pending()
	if len(pending) != 2 || h.scans.Load() != 2 {
		t.Fatalf("pending = %v, scans = %d", pending, h.scans.Load())
	}
	r, err := h.m.Load(pending[0])
	if err != nil || r.GetRequestId() != "r1" {
		t.Fatalf("first report = %+v, %v", r, err)
	}
	if h.saved.IsZero() {
		t.Fatal("the start of a scan is stored")
	}
}

func TestScheduleWaitsForTheIntervalAndTheFirstDelay(t *testing.T) {
	// A scan 2 hours before the agent started: the next one is due 24 hours after it, not after a random first delay.
	h := newHarness(t, t.TempDir(), time.Date(2026, 9, 28, 8, 0, 0, 0, time.UTC), false)
	h.m.Tick()
	if h.m.Running() || h.scans.Load() != 0 {
		t.Fatal("no scan within the interval")
	}
	h.clock.Advance(22 * time.Hour)
	h.m.Tick()
	waitIdle(t, h.m)
	if h.scans.Load() != 1 {
		t.Fatalf("scans = %d", h.scans.Load())
	}
	// Agent-only or no interval: nothing is scheduled.
	h.clock.Advance(48 * time.Hour)
	h.managed.Store(false)
	h.m.Tick()
	h.managed.Store(true)
	h.hours.Store(0)
	h.m.Tick()
	waitIdle(t, h.m)
	if h.scans.Load() != 1 {
		t.Fatalf("scans = %d", h.scans.Load())
	}
}

func TestFirstScanWaitsARandomDelay(t *testing.T) {
	h := newHarness(t, t.TempDir(), time.Time{}, false)
	h.m.mu.Lock()
	h.m.firstDue = h.clock.Now().Add(30 * time.Minute)
	h.m.mu.Unlock()
	h.m.Tick()
	if h.scans.Load() != 0 {
		t.Fatal("the first scan waits its delay")
	}
	h.clock.Advance(30 * time.Minute)
	h.m.Tick()
	waitIdle(t, h.m)
	if h.scans.Load() != 1 {
		t.Fatalf("scans = %d", h.scans.Load())
	}
}

func TestSpoolSurvivesARestartAndAcksDelete(t *testing.T) {
	dir := t.TempDir()
	h := newHarness(t, dir, time.Time{}, false)
	if ok, _ := h.m.Request("r"); !ok {
		t.Fatal("scan did not start")
	}
	waitIdle(t, h.m)
	id := h.m.Pending()[0]
	h.m.Close()

	again := newHarness(t, dir, time.Time{}, false)
	if p := again.m.Pending(); len(p) != 1 || p[0] != id {
		t.Fatalf("pending after restart = %v", p)
	}
	if again.m.Ack("../../state.json") || again.m.Ack("unknown") {
		t.Fatal("a malformed id must be ignored")
	}
	if !again.m.Ack(id) || len(again.m.Pending()) != 0 {
		t.Fatal("ack must remove the report")
	}
	if _, err := os.Stat(filepath.Join(dir, id+spoolSuffix)); !os.IsNotExist(err) {
		t.Fatalf("the spool file must be deleted: %v", err)
	}
}

func TestSpoolDropsTheOldestAboveTheCap(t *testing.T) {
	h := newHarness(t, t.TempDir(), time.Time{}, false)
	var ids []string
	for range DefaultMaxSpooled + 3 {
		r := &agentv1.StorageScanReport{ScanId: newUUID()}
		ids = append(ids, r.ScanId)
		if err := h.m.store(r); err != nil {
			t.Fatal(err)
		}
	}
	pending := h.m.Pending()
	if len(pending) != DefaultMaxSpooled || pending[0] != ids[3] {
		t.Fatalf("pending = %d, first %s", len(pending), pending[0])
	}
}
