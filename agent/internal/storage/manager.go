package storage

import (
	"context"
	"fmt"
	"log/slog"
	"math/rand/v2"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"sort"
	"strings"
	"sync"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

const (
	// RequestGap is how long after the start of a scan a StorageScanRequest is ignored.
	RequestGap = 15 * time.Minute
	// DefaultMaxSpooled bounds the reports waiting for their acknowledgement; the oldest are dropped.
	DefaultMaxSpooled = 20
	// spoolKeepFor drops reports the gateway never acknowledged.
	spoolKeepFor = 7 * 24 * time.Hour
	spoolSuffix  = ".pb"
)

// scanIDPattern is the form of a scan id this agent creates. An acknowledgement names a spool file by it, so anything else is
// ignored rather than used as a file name.
var scanIDPattern = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$`)

// Options configures a Manager.
type Options struct {
	// Dir is the spool directory, inside the protected state directory.
	Dir    string
	Access platform.Access
	Logger *slog.Logger
	Now    func() time.Time
	// Managed reports whether the applied configuration is managed; nothing is scanned otherwise.
	Managed func() bool
	// IntervalHours is storage_scan_interval_hours of the applied configuration; 0 schedules no scans.
	IntervalHours func() uint32
	// LastStarted and SaveStarted keep the start of the last scan in the state file, so a restart does not scan again.
	LastStarted func() time.Time
	SaveStarted func(time.Time) error
	// Scan scans every volume; default ScanAll. It runs on a thread with the lowest priority.
	Scan func(ctx context.Context, requestID string) []*agentv1.StorageScanReport
	// MaxSpooled defaults to DefaultMaxSpooled.
	MaxSpooled int
	// FirstDelayMax is the longest wait for a first scheduled scan; default one hour.
	FirstDelayMax time.Duration
	// CheckInterval is how often the schedule is looked at; default one minute.
	CheckInterval time.Duration
}

// Manager runs storage scans on the schedule of the configuration and on request, one at a time, and keeps their reports on
// disk until the gateway acknowledges them.
type Manager struct {
	opts   Options
	ctx    context.Context
	cancel context.CancelFunc
	notify chan struct{}
	wg     sync.WaitGroup

	mu          sync.Mutex
	running     bool
	lastStarted time.Time
	started     time.Time
	firstDue    time.Time
	// spooled holds the ids of the reports on disk, oldest first.
	spooled []string
}

// NewManager opens the spool.
func NewManager(opts Options) (*Manager, error) {
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.Logger == nil {
		opts.Logger = slog.Default()
	}
	if opts.Scan == nil {
		logger := opts.Logger
		opts.Scan = func(ctx context.Context, requestID string) []*agentv1.StorageScanReport { return ScanAll(ctx, requestID, logger) }
	}
	if opts.MaxSpooled <= 0 {
		opts.MaxSpooled = DefaultMaxSpooled
	}
	if opts.FirstDelayMax <= 0 {
		opts.FirstDelayMax = time.Hour
	}
	if opts.CheckInterval <= 0 {
		opts.CheckInterval = time.Minute
	}
	if opts.Managed == nil {
		opts.Managed = func() bool { return false }
	}
	if opts.IntervalHours == nil {
		opts.IntervalHours = func() uint32 { return 0 }
	}
	if err := platform.EnsureProtectedDir(opts.Dir, opts.Access); err != nil {
		return nil, err
	}
	ctx, cancel := context.WithCancel(context.Background())
	m := &Manager{opts: opts, ctx: ctx, cancel: cancel, notify: make(chan struct{}, 1)}
	now := opts.Now()
	if opts.LastStarted != nil {
		m.lastStarted = opts.LastStarted()
	}
	m.started = now
	m.firstDue = now.Add(time.Duration(rand.Int64N(int64(opts.FirstDelayMax) + 1)))
	m.loadSpool(now)
	return m, nil
}

func (m *Manager) loadSpool(now time.Time) {
	items, err := os.ReadDir(m.opts.Dir)
	if err != nil {
		m.opts.Logger.Warn("the storage scan spool could not be read", "error", err)
		return
	}
	type spooledFile struct {
		id  string
		mod time.Time
	}
	var files []spooledFile
	for _, item := range items {
		id, ok := strings.CutSuffix(item.Name(), spoolSuffix)
		if !ok || !scanIDPattern.MatchString(id) {
			continue
		}
		info, err := item.Info()
		if err != nil {
			continue
		}
		if now.Sub(info.ModTime()) > spoolKeepFor {
			_ = os.Remove(filepath.Join(m.opts.Dir, item.Name()))
			continue
		}
		files = append(files, spooledFile{id, info.ModTime()})
	}
	sort.Slice(files, func(i, j int) bool { return files[i].mod.Before(files[j].mod) })
	for _, f := range files {
		m.spooled = append(m.spooled, f.id)
	}
	m.trimSpool()
}

// Notify signals that reports wait to be sent.
func (m *Manager) Notify() <-chan struct{} { return m.notify }

func (m *Manager) signal() {
	select {
	case m.notify <- struct{}{}:
	default:
	}
}

// Close stops a running scan and waits for it.
func (m *Manager) Close() {
	m.cancel()
	m.wg.Wait()
}

// Run starts scheduled scans until ctx is cancelled.
func (m *Manager) Run(ctx context.Context) {
	ticker := time.NewTicker(m.opts.CheckInterval)
	defer ticker.Stop()
	for {
		m.Tick()
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

// Tick starts a scheduled scan when one is due.
func (m *Manager) Tick() {
	m.mu.Lock()
	due := m.dueLocked(m.opts.Now())
	m.mu.Unlock()
	if due {
		m.start("")
	}
}

// dueLocked reports whether a scheduled scan should start now.
func (m *Manager) dueLocked(now time.Time) bool {
	if m.running || !m.opts.Managed() {
		return false
	}
	hours := m.opts.IntervalHours()
	if hours == 0 {
		return false
	}
	interval := time.Duration(hours) * time.Hour
	last := m.lastStarted
	if !last.IsZero() && !now.Before(last) {
		if next := last.Add(interval); next.After(m.started) {
			return !now.Before(next)
		}
	}
	// No scan within the interval before the agent started (or the clock went back): the first one waits a random delay, so
	// endpoints that start together do not scan together.
	return !now.Before(m.firstDue)
}

// Request starts a scan for a StorageScanRequest. It returns false with the reason when the request is ignored.
func (m *Manager) Request(requestID string) (bool, string) {
	now := m.opts.Now()
	m.mu.Lock()
	switch {
	case !m.opts.Managed():
		m.mu.Unlock()
		return false, "the endpoint is not managed"
	case m.running:
		m.mu.Unlock()
		return false, "a storage scan is running"
	case !m.lastStarted.IsZero() && !now.Before(m.lastStarted) && now.Sub(m.lastStarted) < RequestGap:
		m.mu.Unlock()
		return false, "a storage scan started less than 15 minutes ago"
	}
	m.mu.Unlock()
	return m.start(requestID), ""
}

// start runs a scan in the background, unless one runs already.
func (m *Manager) start(requestID string) bool {
	now := m.opts.Now()
	m.mu.Lock()
	if m.running || m.ctx.Err() != nil {
		m.mu.Unlock()
		return false
	}
	m.running = true
	m.lastStarted = now
	m.mu.Unlock()
	if m.opts.SaveStarted != nil {
		if err := m.opts.SaveStarted(now); err != nil {
			m.opts.Logger.Warn("the start of the storage scan could not be stored", "error", err)
		}
	}
	m.wg.Add(1)
	go func() {
		defer m.wg.Done()
		defer func() {
			m.mu.Lock()
			m.running = false
			m.mu.Unlock()
			m.signal()
		}()
		defer safego.Recover(m.opts.Logger, "storage scan")
		// The thread is never unlocked: it ends with this goroutine, so its lowered priority never reaches other work.
		runtime.LockOSThread()
		if err := lowerThreadPriority(); err != nil {
			m.opts.Logger.Warn("the storage scan could not lower its priority", "error", err)
		}
		reports := m.opts.Scan(m.ctx, requestID)
		for _, r := range reports {
			if err := m.store(r); err != nil {
				m.opts.Logger.Error("a storage scan report could not be stored and is lost", "error", err)
			}
		}
	}()
	return true
}

// store writes a report to the spool.
func (m *Manager) store(r *agentv1.StorageScanReport) error {
	if !scanIDPattern.MatchString(r.GetScanId()) {
		return fmt.Errorf("invalid scan id")
	}
	data, err := proto.Marshal(r)
	if err != nil {
		return err
	}
	if err := platform.WriteFileAtomic(m.path(r.GetScanId()), data, m.opts.Access); err != nil {
		return err
	}
	m.mu.Lock()
	m.spooled = append(m.spooled, r.GetScanId())
	m.trimSpool()
	m.mu.Unlock()
	return nil
}

// trimSpool drops the oldest reports above the cap.
func (m *Manager) trimSpool() {
	for len(m.spooled) > m.opts.MaxSpooled {
		_ = os.Remove(m.path(m.spooled[0]))
		m.spooled = m.spooled[1:]
		m.opts.Logger.Warn("dropped the oldest storage scan report: too many wait for the gateway")
	}
}

func (m *Manager) path(id string) string { return filepath.Join(m.opts.Dir, id+spoolSuffix) }

// Pending returns the ids of the reports waiting for their acknowledgement, oldest first.
func (m *Manager) Pending() []string {
	m.mu.Lock()
	defer m.mu.Unlock()
	return append([]string(nil), m.spooled...)
}

// Load reads a spooled report. A report that cannot be read is dropped.
func (m *Manager) Load(id string) (*agentv1.StorageScanReport, error) {
	data, err := os.ReadFile(m.path(id))
	if err == nil {
		var r agentv1.StorageScanReport
		if err = proto.Unmarshal(data, &r); err == nil {
			return &r, nil
		}
	}
	m.Ack(id)
	return nil, err
}

// Ack deletes an acknowledged report. Unknown or malformed ids are ignored.
func (m *Manager) Ack(id string) bool {
	if !scanIDPattern.MatchString(id) {
		return false
	}
	m.mu.Lock()
	defer m.mu.Unlock()
	for i, s := range m.spooled {
		if s == id {
			_ = os.Remove(m.path(id))
			m.spooled = append(m.spooled[:i], m.spooled[i+1:]...)
			return true
		}
	}
	return false
}

// Running reports whether a scan runs.
func (m *Manager) Running() bool {
	m.mu.Lock()
	defer m.mu.Unlock()
	return m.running
}
