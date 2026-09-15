package update

import (
	"context"
	"crypto/ed25519"
	"crypto/sha256"
	"errors"
	"log/slog"
	"math/rand/v2"
	"net/http"
	"os"
	"path/filepath"
	"runtime"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/release"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
)

// Target is the service an updater installs: the watchdog installs the agent, the agent installs the watchdog.
type Target struct {
	// Component as named in the release manifest.
	Component string
	Service   string
	// Exe is where the service binary is installed.
	Exe string
	// HealthDir is the state directory of the target, holding its health file.
	HealthDir string
}

// Status is one progress report of an installation, sent to the gateway as UpdateStatus.
type Status struct {
	Component string
	Version   string
	State     agentv1.UpdateState
	Detail    string
}

// UpdaterOptions configures an Updater. Zero durations get production defaults.
type UpdaterOptions struct {
	Target     Target
	Keys       []ed25519.PublicKey
	StateDir   string
	Access     platform.Access
	Controller svcctl.Controller
	// Client returns an mTLS HTTP client for the gateway and the gateway host:port.
	Client func() (*http.Client, string, error)
	Report func(Status)
	Logger *slog.Logger
	// Connected reports whether the installing service has a gateway session now.
	Connected func() bool
	// InstalledVersion returns the installed version of the target, "" when it is not installed.
	InstalledVersion func(ctx context.Context) (string, error)
	// Ready reports whether the installing service already runs the release. The agent updates the watchdog only then; it provisions a
	// missing watchdog when the ring allows the release or when it runs the release itself, so a paused release reaches no new endpoint.
	Ready func(m *release.Manifest) bool
	// Provision installs a target that is not installed (the agent installs the watchdog). Nil: a missing target is left alone.
	Provision func(ctx context.Context, staged string, m *release.Manifest) error
	// Pause stops the supervision of the target while its binary is replaced.
	Pause func(paused bool)

	MaxDelay         time.Duration
	HealthTimeout    time.Duration
	HealthMaxTimeout time.Duration
	RetryAfter       time.Duration
	Now              func() time.Time
}

type offer struct {
	manifest  []byte
	signature []byte
	allowed   bool
}

// Updater acts on the release offers of the gateway for one target, one installation at a time.
type Updater struct {
	opts      UpdaterOptions
	memory    *Memory
	mu        sync.Mutex
	latest    *offer
	notBefore map[string]time.Time
	lastBad   [32]byte
	wake      chan struct{}
}

// NewUpdater creates an updater; call Run in a goroutine and Offer for every UpdateOffer.
func NewUpdater(opts UpdaterOptions) *Updater {
	if opts.MaxDelay < 0 {
		opts.MaxDelay = 0
	} else if opts.MaxDelay == 0 {
		opts.MaxDelay = 10 * time.Minute
	}
	if opts.HealthTimeout <= 0 {
		opts.HealthTimeout = 5 * time.Minute
	}
	if opts.HealthMaxTimeout <= 0 {
		opts.HealthMaxTimeout = 30 * time.Minute
	}
	if opts.RetryAfter <= 0 {
		opts.RetryAfter = time.Hour
	}
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.Report == nil {
		opts.Report = func(Status) {}
	}
	return &Updater{
		opts:      opts,
		memory:    OpenMemory(opts.StateDir, opts.Access),
		notBefore: map[string]time.Time{},
		wake:      make(chan struct{}, 1),
	}
}

// Offer records the latest offer of the gateway and wakes the updater.
func (u *Updater) Offer(manifest, signature []byte, allowed bool) {
	u.mu.Lock()
	u.latest = &offer{manifest: manifest, signature: signature, allowed: allowed}
	u.mu.Unlock()
	u.Wake()
}

// Wake makes the updater look at the latest offer again, e.g. after the agent installed its own update.
func (u *Updater) Wake() {
	select {
	case u.wake <- struct{}{}:
	default:
	}
}

// Run evaluates offers until ctx is cancelled: when one arrives and every 5 minutes (retry waits and delays pass).
func (u *Updater) Run(ctx context.Context) {
	ticker := time.NewTicker(5 * time.Minute)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-u.wake:
		case <-ticker.C:
		}
		u.Evaluate(ctx)
	}
}

// Evaluate acts on the latest offer once. Exported for tests.
func (u *Updater) Evaluate(ctx context.Context) {
	u.mu.Lock()
	o := u.latest
	u.mu.Unlock()
	if o == nil {
		return
	}
	logger := u.opts.Logger.With("component", u.opts.Target.Component)
	m, err := release.Verify(o.manifest, o.signature, u.opts.Keys)
	if err != nil {
		if sum := sha256.Sum256(o.manifest); sum != u.lastBad {
			u.lastBad = sum
			logger.Warn("the offered release is not used", "error", err)
		}
		return
	}
	binary, ok := m.Binary(u.opts.Target.Component, runtime.GOOS, runtime.GOARCH)
	if !ok {
		return
	}
	now := u.opts.Now()
	installed, err := u.opts.InstalledVersion(ctx)
	if err != nil {
		logger.Warn("the installed version could not be read; no update now", "error", err)
		return
	}
	provision := installed == ""
	switch {
	case provision && u.opts.Provision == nil:
		return
	case provision && !o.allowed && (u.opts.Ready == nil || !u.opts.Ready(m)):
		return
	case !provision && (!release.Newer(m.Version, installed) || !o.allowed):
		return
	case u.memory.RolledBack(u.opts.Target.Component, m.Version):
		return
	case !u.memory.MayAttempt(u.opts.Target.Component, now):
		return
	case !provision && u.opts.Ready != nil && !u.opts.Ready(m):
		return
	}

	// Spread the downloads of many endpoints over time; a missing service is repaired without waiting.
	if !provision {
		at, planned := u.notBefore[m.Version]
		if !planned {
			at = now.Add(time.Duration(rand.Int64N(int64(u.opts.MaxDelay) + 1)))
			u.notBefore[m.Version] = at
			logger.Info("release offered; installing after a random delay", "version", m.Version, "installed", installed, "at", at.UTC().Format(time.RFC3339))
		}
		if now.Before(at) {
			time.AfterFunc(at.Sub(now), u.Wake)
			return
		}
	}

	u.install(ctx, logger, m, binary, installed, provision)
}

func (u *Updater) install(ctx context.Context, logger *slog.Logger, m *release.Manifest, binary release.Binary, installed string, provision bool) {
	t := u.opts.Target
	report := func(state agentv1.UpdateState, detail string) {
		u.opts.Report(Status{Component: t.Component, Version: m.Version, State: state, Detail: truncate(detail, 500)})
	}
	fail := func(err error) {
		logger.Warn("installing the release failed; the installed version keeps running", "version", m.Version, "error", err)
		report(agentv1.UpdateState_UPDATE_STATE_FAILED, err.Error())
		u.memory.RetryAt(t.Component, u.opts.Now().Add(u.opts.RetryAfter))
	}

	report(agentv1.UpdateState_UPDATE_STATE_DOWNLOADING, "")
	client, server, err := u.opts.Client()
	if err != nil {
		fail(err)
		return
	}
	staged := filepath.Join(u.opts.StateDir, "updates", filepath.Base(binary.File))
	defer os.Remove(staged)
	if err := Download(ctx, client, server, m.Version, binary, staged, u.opts.Access); err != nil {
		var later *RetryLaterError
		if errors.As(err, &later) {
			logger.Info("the gateway is busy; downloading later", "retryIn", later.After.String())
			u.memory.RetryAt(t.Component, u.opts.Now().Add(later.After))
			time.AfterFunc(later.After, u.Wake)
			return
		}
		fail(err)
		return
	}
	reported, err := ProbeVersion(ctx, staged)
	if err != nil || reported != m.Version {
		if err == nil {
			err = errors.New("the downloaded binary reports version " + reported + " instead of " + m.Version)
		}
		// A defect of the release itself: do not download it again.
		u.memory.RecordRollback(t.Component, m.Version)
		report(agentv1.UpdateState_UPDATE_STATE_FAILED, err.Error())
		logger.Error("the downloaded binary does not match its release; this version is skipped", "version", m.Version, "error", err)
		return
	}

	report(agentv1.UpdateState_UPDATE_STATE_INSTALLING, "")
	if provision {
		if err := u.opts.Provision(ctx, staged, m); err != nil {
			fail(err)
			return
		}
		u.memory.Clear(t.Component)
		logger.Info("installed", "version", m.Version)
		report(agentv1.UpdateState_UPDATE_STATE_INSTALLED, "")
		return
	}

	if u.opts.Pause != nil {
		u.opts.Pause(true)
		defer u.opts.Pause(false)
	}
	outcome, err := Install(ctx, InstallRequest{
		Controller: u.opts.Controller, Service: t.Service, Exe: t.Exe, Staged: staged, SHA256: binary.SHA256, Size: binary.Size,
		Version: m.Version, JournalDir: u.opts.StateDir, Access: u.opts.Access, Logger: logger, Now: u.opts.Now,
		Healthy: func(ctx context.Context, since time.Time) error {
			return WaitHealthy(HealthWait{
				Dir: t.HealthDir, Version: m.Version, Since: since, Timeout: u.opts.HealthTimeout, MaxTimeout: u.opts.HealthMaxTimeout,
				InstallerConnected: u.opts.Connected, Now: u.opts.Now,
			}, ctx.Done())
		},
	})
	switch outcome {
	case Installed:
		u.memory.Clear(t.Component)
		logger.Info("updated", "from", installed, "to", m.Version)
		report(agentv1.UpdateState_UPDATE_STATE_INSTALLED, "")
	case RolledBack:
		u.memory.RecordRollback(t.Component, m.Version)
		logger.Error("the update was rolled back; this version is not tried again", "version", m.Version, "error", err)
		report(agentv1.UpdateState_UPDATE_STATE_ROLLED_BACK, err.Error())
	default:
		fail(err)
	}
}
