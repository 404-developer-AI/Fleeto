package update

import (
	"context"
	"crypto/ed25519"
	"crypto/sha256"
	"errors"
	"log/slog"
	"math"
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
	// WaitReason and WaitFor go with UPDATE_STATE_WAITING; WaitFor is zero when the end of the wait is not known.
	WaitReason agentv1.UpdateWaitReason
	WaitFor    time.Duration
}

// Message is the status as the protocol message for the gateway.
func (s Status) Message() *agentv1.UpdateStatus {
	component := agentv1.Component_COMPONENT_AGENT
	if s.Component == release.ComponentWatchdog {
		component = agentv1.Component_COMPONENT_WATCHDOG
	}
	wait := uint32(0)
	if s.WaitFor > 0 {
		wait = uint32(min(math.Ceil(s.WaitFor.Seconds()), math.MaxUint32))
	}
	return &agentv1.UpdateStatus{
		Component: component, Version: s.Version, State: s.State, Detail: s.Detail, WaitReason: s.WaitReason, WaitSeconds: wait,
	}
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
	// TransientRetry is the first wait after a transient failure; it doubles with each further one up to RetryAfter.
	TransientRetry time.Duration
	Now            func() time.Time
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
	// lastWait is the wait logged last, so each reason is logged once per release instead of at every evaluation.
	lastWait string
	// transientFailures counts transient failures in a row, for the backoff.
	transientFailures int
	wake              chan struct{}
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
	if opts.TransientRetry <= 0 {
		opts.TransientRetry = time.Minute
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
	if provision && u.opts.Provision == nil || !provision && !release.Newer(m.Version, installed) {
		return
	}
	if reason, until := u.waitReason(m, o.allowed, provision, now); reason != agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_UNSPECIFIED {
		u.reportWait(logger, m.Version, installed, reason, until, now)
		return
	}
	u.lastWait = ""

	// Spread the downloads of many endpoints over time; a missing service is repaired without waiting.
	if !provision {
		at, planned := u.notBefore[m.Version]
		if !planned {
			at = now.Add(time.Duration(rand.Int64N(int64(u.opts.MaxDelay) + 1)))
			u.notBefore[m.Version] = at
			logger.Info("release offered; installing after a random delay", "version", m.Version, "installed", installed, "at", at.UTC().Format(time.RFC3339))
			if at.After(now) {
				u.opts.Report(Status{Component: u.opts.Target.Component, Version: m.Version, State: agentv1.UpdateState_UPDATE_STATE_WAITING,
					WaitReason: agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_RANDOM_DELAY, WaitFor: at.Sub(now)})
			}
		}
		if now.Before(at) {
			time.AfterFunc(at.Sub(now), u.Wake)
			return
		}
	}

	u.install(ctx, logger, m, binary, installed, provision)
}

// waitReason says why an offered release that this updater would install is not installed now; unspecified when nothing holds it
// back. until is the time the wait ends, when it is known.
func (u *Updater) waitReason(m *release.Manifest, allowed, provision bool, now time.Time) (reason agentv1.UpdateWaitReason, until time.Time) {
	component := u.opts.Target.Component
	switch {
	case u.memory.RolledBack(component, m.Version):
		return agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_ROLLED_BACK, time.Time{}
	// A missing target is installed from a release the ring has not reached when this service already runs that release.
	case !allowed && (!provision || u.opts.Ready == nil || !u.opts.Ready(m)):
		return agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_UPDATE_RING, time.Time{}
	case !u.memory.MayAttempt(component, now):
		return agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_NEXT_ATTEMPT, u.memory.NextAttempt(component)
	case !provision && u.opts.Ready != nil && !u.opts.Ready(m):
		return agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_INSTALLER_UPDATE, time.Time{}
	}
	return agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_UNSPECIFIED, time.Time{}
}

var waitTexts = map[agentv1.UpdateWaitReason]string{
	agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_ROLLED_BACK:      "this version was rolled back before and is not tried again",
	agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_UPDATE_RING:      "waiting for the update ring of the endpoint",
	agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_NEXT_ATTEMPT:     "waiting for the next attempt after a failed or postponed one",
	agentv1.UpdateWaitReason_UPDATE_WAIT_REASON_INSTALLER_UPDATE: "waiting until this service runs the release itself",
}

// reportWait logs a wait and reports it to the gateway, once per release, reason and end time.
func (u *Updater) reportWait(logger *slog.Logger, version, installed string, reason agentv1.UpdateWaitReason, until, now time.Time) {
	key := version + "|" + reason.String() + "|" + until.UTC().Format(time.RFC3339)
	if key == u.lastWait {
		return
	}
	u.lastWait = key
	if installed == "" {
		installed = "not installed"
	}
	attrs := []any{"version", version, "installed", installed, "reason", waitTexts[reason]}
	status := Status{Component: u.opts.Target.Component, Version: version, State: agentv1.UpdateState_UPDATE_STATE_WAITING, WaitReason: reason}
	if !until.IsZero() {
		attrs = append(attrs, "nextAttempt", until.UTC().Format(time.RFC3339))
		status.WaitFor = until.Sub(now)
	}
	logger.Info("release offered; not installing it yet", attrs...)
	u.opts.Report(status)
}

func (u *Updater) install(ctx context.Context, logger *slog.Logger, m *release.Manifest, binary release.Binary, installed string, provision bool) {
	t := u.opts.Target
	report := func(state agentv1.UpdateState, detail string) {
		u.opts.Report(Status{Component: t.Component, Version: m.Version, State: state, Detail: truncate(detail, 500)})
	}
	fail := func(err error) {
		report(agentv1.UpdateState_UPDATE_STATE_FAILED, err.Error())
		if IsTransient(err) {
			wait := u.transientWait()
			logger.Warn("installing the release failed for now; retrying soon", "version", m.Version, "retryIn", wait.Round(time.Second).String(), "error", err)
			u.memory.RetryAt(t.Component, u.opts.Now().Add(wait))
			time.AfterFunc(wait, u.Wake)
			return
		}
		u.transientFailures = 0
		logger.Warn("installing the release failed; the installed version keeps running", "version", m.Version, "error", err)
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
		u.transientFailures = 0
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
		u.transientFailures = 0
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

// transientWait is the wait after one more transient failure in a row: TransientRetry doubled per earlier failure, at most RetryAfter,
// between half and all of that so the endpoints that failed together do not retry together.
func (u *Updater) transientWait() time.Duration {
	ceiling := u.opts.TransientRetry
	for i := 0; i < u.transientFailures && ceiling < u.opts.RetryAfter; i++ {
		ceiling *= 2
	}
	ceiling = min(ceiling, u.opts.RetryAfter)
	if u.transientFailures < 32 {
		u.transientFailures++
	}
	half := ceiling / 2
	return half + time.Duration(rand.Int64N(int64(ceiling-half)+1))
}
