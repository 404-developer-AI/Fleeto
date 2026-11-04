package patch

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"math"
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
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

const (
	// RequestGap is how long after the start of a scan a PatchScanRequest is ignored.
	RequestGap = 15 * time.Minute
	// ScanTimeout bounds one scan, the refresh of the package lists included.
	ScanTimeout = 15 * time.Minute
	// DefaultMaxSpooled bounds the reports waiting for their acknowledgement; the oldest are dropped.
	DefaultMaxSpooled = 10
	// spoolKeepFor drops reports the gateway never acknowledged.
	spoolKeepFor = 7 * 24 * time.Hour
	spoolSuffix  = ".pb"

	problemTimeout = "The scan took longer than 15 minutes and was stopped; the updates listed are what it read until then."
	problemStopped = "The scan was stopped because the agent stopped; the updates listed are what it read until then."

	reasonNotManaged = "the endpoint is not managed"
	reasonNotOn      = "patch management by Fleeto is not on for the endpoint"
	reasonRunning    = "a patch scan is running"
	reasonTooSoon    = "a patch scan started less than 15 minutes ago"
	reasonInstalling = "an installation of updates is running; a scan follows when it ends"
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
	// IntervalHours is patch_scan_interval_hours of the applied configuration; 0 means patch management by Fleeto is off: no
	// scheduled scans and no scans on request.
	IntervalHours func() uint32
	// LastStarted and SaveStarted keep the start of the last scan in the state file, so a restart does not scan again.
	LastStarted func() time.Time
	SaveStarted func(time.Time) error
	// Scan reads the missing updates; default Scan. It returns nil on a platform without a supported package manager. It runs on
	// a thread with the lowest priority, which the commands it starts inherit.
	Scan func(ctx context.Context, requestID string) *agentv1.PatchScanReport
	// MaxSpooled defaults to DefaultMaxSpooled.
	MaxSpooled int
	// FirstDelayMax is the longest wait for a first scheduled scan; default one hour.
	FirstDelayMax time.Duration
	// CheckInterval is how often the schedule is looked at; default one minute.
	CheckInterval time.Duration
	// Timeout bounds one scan; default ScanTimeout.
	Timeout time.Duration
}

// Manager runs patch scans on the schedule of the configuration and on request, one at a time, and keeps their reports on disk
// until the gateway acknowledges them.
type Manager struct {
	opts   Options
	ctx    context.Context
	cancel context.CancelFunc
	notify chan struct{}
	wg     sync.WaitGroup

	mu      sync.Mutex
	running bool
	// again starts another scan when the running one ends: an installation ended while it ran.
	again       bool
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
		opts.Scan = func(ctx context.Context, requestID string) *agentv1.PatchScanReport {
			return Scan(ctx, requestID, logger)
		}
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
	if opts.Timeout <= 0 {
		opts.Timeout = ScanTimeout
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
		m.opts.Logger.Warn("the patch scan spool could not be read", "error", err)
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
	if m.running || !m.opts.Managed() || InstallRunning() {
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

// Request starts a scan for a PatchScanRequest. It returns false with the reason when the request is ignored.
func (m *Manager) Request(requestID string) (bool, string) {
	now := m.opts.Now()
	m.mu.Lock()
	switch {
	case !m.opts.Managed():
		m.mu.Unlock()
		return false, reasonNotManaged
	case m.opts.IntervalHours() == 0:
		m.mu.Unlock()
		return false, reasonNotOn
	case InstallRunning():
		m.mu.Unlock()
		return false, reasonInstalling
	case m.running:
		m.mu.Unlock()
		return false, reasonRunning
	case !m.lastStarted.IsZero() && !now.Before(m.lastStarted) && now.Sub(m.lastStarted) < RequestGap:
		m.mu.Unlock()
		return false, reasonTooSoon
	}
	m.mu.Unlock()
	return m.start(requestID), ""
}

// ScanAfterInstall scans now, so the server sees what an installation of updates did. A scan that runs already read the state from
// before the installation ended, so another one follows it. The gap between requested scans does not apply.
func (m *Manager) ScanAfterInstall() {
	if !m.opts.Managed() || m.opts.IntervalHours() == 0 {
		return
	}
	m.mu.Lock()
	if m.running {
		m.again = true
		m.mu.Unlock()
		return
	}
	m.mu.Unlock()
	m.start("")
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
			m.opts.Logger.Warn("the start of the patch scan could not be stored", "error", err)
		}
	}
	m.wg.Add(1)
	go func() {
		defer m.wg.Done()
		defer func() {
			m.mu.Lock()
			m.running = false
			again := m.again
			m.again = false
			m.mu.Unlock()
			m.signal()
			if again {
				m.start("")
			}
		}()
		defer safego.Recover(m.opts.Logger, "patch scan")
		// The thread is never unlocked: it ends with this goroutine, so its lowered priority never reaches other work. The commands
		// the scan starts are forked from this thread and inherit its CPU and I/O priority.
		runtime.LockOSThread()
		if err := lowerThreadPriority(); err != nil {
			m.opts.Logger.Warn("the patch scan could not lower its priority", "error", err)
		}
		report := m.scan(requestID, now)
		if report == nil {
			return
		}
		if err := m.store(report); err != nil {
			m.opts.Logger.Error("a patch scan report could not be stored and is lost", "error", err)
			return
		}
		m.opts.Logger.Info("patch scan finished", "engine", report.GetEngine(), "updates", len(report.GetUpdates()),
			"truncated", report.GetTruncated(), "problem", report.GetProblem() != "", "durationMs", report.GetDurationMs())
	}()
	return true
}

// scan runs one scan within the timeout and completes its report.
func (m *Manager) scan(requestID string, started time.Time) *agentv1.PatchScanReport {
	ctx, cancel := context.WithTimeout(m.ctx, m.opts.Timeout)
	defer cancel()
	report := m.opts.Scan(ctx, requestID)
	if report == nil {
		return nil
	}
	switch {
	case errors.Is(ctx.Err(), context.DeadlineExceeded):
		report.Problem = joinProblem(problemTimeout, report.GetProblem())
	case m.ctx.Err() != nil:
		report.Problem = joinProblem(problemStopped, report.GetProblem())
	}
	report.RequestId = requestID
	report.StartedAt = timestamppb.New(started)
	elapsed := m.opts.Now().Sub(started).Milliseconds()
	report.DurationMs = uint32(min(max(elapsed, 0), math.MaxUint32))
	Finalize(report)
	return report
}

// store writes a report to the spool.
func (m *Manager) store(r *agentv1.PatchScanReport) error {
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
		m.opts.Logger.Warn("dropped the oldest patch scan report: too many wait for the gateway")
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
func (m *Manager) Load(id string) (*agentv1.PatchScanReport, error) {
	data, err := os.ReadFile(m.path(id))
	if err == nil {
		var r agentv1.PatchScanReport
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
