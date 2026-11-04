package patch

import (
	"context"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
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
	saved   atomic.Pointer[time.Time]
}

type harnessOptions struct {
	last    time.Time
	block   bool
	timeout time.Duration
	scan    func(ctx context.Context, requestID string) *agentv1.PatchScanReport
}

func newHarness(t *testing.T, dir string, o harnessOptions) *harness {
	t.Helper()
	h := &harness{clock: &fakeClock{now: time.Date(2026, 11, 4, 10, 0, 0, 0, time.UTC)}, release: make(chan struct{})}
	h.managed.Store(true)
	h.hours.Store(6)
	if !o.block {
		close(h.release)
	}
	scan := o.scan
	if scan == nil {
		scan = func(ctx context.Context, requestID string) *agentv1.PatchScanReport {
			h.scans.Add(1)
			<-h.release
			return &agentv1.PatchScanReport{Engine: EngineApt, Updates: []*agentv1.PatchUpdate{
				{Id: "openssl:amd64", Name: "openssl", Version: "3.0.2-0ubuntu1.19", Classification: "security"},
			}}
		}
	}
	m, err := NewManager(Options{
		Dir:           dir,
		Access:        platform.AccessCurrentUser,
		Logger:        slog.New(slog.NewTextHandler(io.Discard, nil)),
		Now:           h.clock.Now,
		Managed:       h.managed.Load,
		IntervalHours: h.hours.Load,
		LastStarted:   func() time.Time { return o.last },
		SaveStarted:   func(at time.Time) error { h.saved.Store(&at); return nil },
		FirstDelayMax: time.Hour,
		Timeout:       o.timeout,
		Scan:          scan,
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
	h := newHarness(t, t.TempDir(), harnessOptions{block: true})
	h.managed.Store(false)
	if ok, reason := h.m.Request("r0"); ok || reason != "the endpoint is not managed" {
		t.Fatalf("agent-only: %v %q", ok, reason)
	}
	h.managed.Store(true)
	h.hours.Store(0)
	if ok, reason := h.m.Request("r0"); ok || reason != "patch management by Fleeto is not on for the endpoint" {
		t.Fatalf("no interval: %v %q", ok, reason)
	}
	h.hours.Store(6)
	if ok, _ := h.m.Request("r1"); !ok {
		t.Fatal("the first request starts a scan")
	}
	if ok, reason := h.m.Request("r2"); ok || reason != "a patch scan is running" {
		t.Fatalf("while running: %v %q", ok, reason)
	}
	close(h.release)
	waitIdle(t, h.m)
	h.clock.Advance(14 * time.Minute)
	if ok, reason := h.m.Request("r3"); ok || reason != "a patch scan started less than 15 minutes ago" {
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
	if err != nil || r.GetRequestId() != "r1" || r.GetStartedAt() == nil || r.GetEngine() != EngineApt || len(r.GetUpdates()) != 1 {
		t.Fatalf("first report = %+v, %v", r, err)
	}
	if !scanIDPattern.MatchString(r.GetScanId()) {
		t.Fatalf("scan id %q", r.GetScanId())
	}
	if h.saved.Load() == nil {
		t.Fatal("the start of a scan is stored")
	}
}

func TestScheduleWaitsForTheIntervalAndTheFirstDelay(t *testing.T) {
	// A scan 2 hours before the agent started: the next one is due 6 hours after it, not after a random first delay.
	h := newHarness(t, t.TempDir(), harnessOptions{last: time.Date(2026, 11, 4, 8, 0, 0, 0, time.UTC)})
	h.m.Tick()
	if h.m.Running() || h.scans.Load() != 0 {
		t.Fatal("no scan within the interval")
	}
	h.clock.Advance(4 * time.Hour)
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
	h := newHarness(t, t.TempDir(), harnessOptions{})
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

func TestAScanThatTakesTooLongReportsAProblem(t *testing.T) {
	h := newHarness(t, t.TempDir(), harnessOptions{
		timeout: 50 * time.Millisecond,
		scan: func(ctx context.Context, _ string) *agentv1.PatchScanReport {
			<-ctx.Done()
			return &agentv1.PatchScanReport{Engine: EngineDnf, Problem: "The repository epel could not be read."}
		},
	})
	if ok, _ := h.m.Request("slow"); !ok {
		t.Fatal("scan did not start")
	}
	waitIdle(t, h.m)
	r, err := h.m.Load(h.m.Pending()[0])
	if err != nil {
		t.Fatal(err)
	}
	if r.GetProblem() != problemTimeout+" The repository epel could not be read." {
		t.Fatalf("problem %q", r.GetProblem())
	}
}

func TestNoReportOnAPlatformWithoutAnEngine(t *testing.T) {
	h := newHarness(t, t.TempDir(), harnessOptions{scan: func(context.Context, string) *agentv1.PatchScanReport { return nil }})
	if ok, _ := h.m.Request("r"); !ok {
		t.Fatal("scan did not start")
	}
	waitIdle(t, h.m)
	if p := h.m.Pending(); len(p) != 0 {
		t.Fatalf("pending = %v", p)
	}
}

func TestSpoolSurvivesARestartAndAcksDelete(t *testing.T) {
	dir := t.TempDir()
	h := newHarness(t, dir, harnessOptions{})
	if ok, _ := h.m.Request("r"); !ok {
		t.Fatal("scan did not start")
	}
	waitIdle(t, h.m)
	id := h.m.Pending()[0]
	h.m.Close()

	again := newHarness(t, dir, harnessOptions{})
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
	h := newHarness(t, t.TempDir(), harnessOptions{})
	var ids []string
	for range DefaultMaxSpooled + 3 {
		r := &agentv1.PatchScanReport{ScanId: newUUID()}
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

func TestAnUnreadableReportIsDropped(t *testing.T) {
	dir := t.TempDir()
	id := newUUID()
	if err := os.WriteFile(filepath.Join(dir, id+spoolSuffix), []byte("\xff\xff\xff"), 0o600); err != nil {
		t.Fatal(err)
	}
	h := newHarness(t, dir, harnessOptions{})
	if _, err := h.m.Load(id); err == nil {
		t.Fatal("garbage must not load")
	}
	if len(h.m.Pending()) != 0 || strings.Contains(strings.Join(h.m.Pending(), ","), id) {
		t.Fatal("an unreadable report is dropped")
	}
}

func TestNoScanRunsDuringAnInstallationAndOneFollowsIt(t *testing.T) {
	h := newHarness(t, t.TempDir(), harnessOptions{block: true})
	release, ok := BeginInstall()
	if !ok {
		t.Fatal("the guard is free")
	}
	if ok, reason := h.m.Request("r1"); ok || reason != reasonInstalling {
		t.Fatalf("during an installation: %v %q", ok, reason)
	}
	h.clock.Advance(2 * time.Hour)
	h.m.Tick()
	if h.m.Running() {
		t.Fatal("no scheduled scan during an installation")
	}
	release()

	// The installation ended while a scan ran: another scan follows it, whatever the gap between requested scans.
	h.m.Tick()
	if !h.m.Running() {
		t.Fatal("the scheduled scan starts once the installation ended")
	}
	h.m.ScanAfterInstall()
	close(h.release)
	deadline := time.Now().Add(5 * time.Second)
	for h.scans.Load() < 2 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	waitIdle(t, h.m)
	if h.scans.Load() != 2 {
		t.Fatalf("scans = %d, want the running one and the one after the installation", h.scans.Load())
	}
	h.m.ScanAfterInstall()
	deadline = time.Now().Add(5 * time.Second)
	for h.scans.Load() < 3 && time.Now().Before(deadline) {
		time.Sleep(5 * time.Millisecond)
	}
	waitIdle(t, h.m)
	if h.scans.Load() != 3 {
		t.Fatalf("scans = %d, a scan after an installation ignores the gap of 15 minutes", h.scans.Load())
	}

	h.hours.Store(0)
	h.m.ScanAfterInstall()
	time.Sleep(50 * time.Millisecond)
	if h.scans.Load() != 3 {
		t.Fatal("without patch management by Fleeto there is no scan after an installation")
	}
}
