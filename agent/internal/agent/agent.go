// Package agent is the runtime of an enrolled agent: it keeps the gateway session alive, applies signed
// configurations, runs checks and delivers their results, reports inventory and renews its certificate.
package agent

import (
	"context"
	"errors"
	"fmt"
	"log/slog"
	"path/filepath"
	"sync"
	"sync/atomic"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/backoff"
	"github.com/404-developer-AI/Fleeto/agent/internal/buffer"
	"github.com/404-developer-AI/Fleeto/agent/internal/checks"
	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
	"github.com/404-developer-AI/Fleeto/agent/internal/jobs"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/remote"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/screen"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
)

// JobsDirName is the job spool inside the state directory.
const JobsDirName = "jobs"

// CheckScriptsDirName holds the scripts of script checks while they run.
const CheckScriptsDirName = "check-scripts"

// ErrRevoked is returned by Run when the gateway revoked the agent. The agent must be enrolled again.
var ErrRevoked = errors.New("the agent certificate was revoked: enroll the agent again")

// Options configures the runtime. Zero values get production defaults.
type Options struct {
	Store     *state.Store
	Logger    *slog.Logger
	Collector checks.Collector
	// Inventory collects the inventory; default inventory.Collect.
	Inventory func(ctx context.Context) *agentv1.Inventory
	// OSInfo describes the operating system for Hello; default inventory.OSInfo.
	OSInfo func(ctx context.Context) *agentv1.OsInfo
	// SignedInUsers lists the users signed in on the endpoint (0.2.2); default jobs.SignedInUsers.
	SignedInUsers func() ([]*agentv1.SignedInUser, error)
	// SignedInUsersInterval is how often that list is read; default 30 seconds.
	SignedInUsersInterval time.Duration

	MaxBufferedResults       int
	BatchMinResults          int
	BatchMaxResults          int
	BatchFlushInterval       time.Duration
	BatchAckTimeout          time.Duration
	BackoffBase              time.Duration
	BackoffMax               time.Duration
	HealthyAfter             time.Duration
	DefaultInventoryInterval time.Duration
	RenewalRetry             time.Duration
	RenewalCheckInterval     time.Duration
	HandshakeTimeout         time.Duration
	WriteTimeout             time.Duration
	Now                      func() time.Time

	// Watchdog makes the agent install, supervise and update the watchdog (0.2.1). Nil in foreground development mode.
	Watchdog *WatchdogOptions

	// ScreenLauncher starts the remote control helper (0.3.0); default screen.WindowsLauncher on Windows. Tests replace it.
	ScreenLauncher screen.Launcher
}

func (o *Options) setDefaults() {
	if o.Collector == nil {
		collector := checks.SystemCollector{}
		if o.Store != nil {
			collector.ScriptDir = filepath.Join(o.Store.Dir(), CheckScriptsDirName)
			collector.Access = o.Store.Access()
		}
		o.Collector = collector
	}
	if o.Inventory == nil {
		logger := o.Logger
		o.Inventory = func(ctx context.Context) *agentv1.Inventory { return inventory.Collect(ctx, logger) }
	}
	if o.OSInfo == nil {
		o.OSInfo = inventory.OSInfo
	}
	if o.SignedInUsers == nil {
		o.SignedInUsers = jobs.SignedInUsers
	}
	setDefault(&o.SignedInUsersInterval, 30*time.Second)
	setDefault(&o.MaxBufferedResults, buffer.DefaultMaxResults)
	setDefault(&o.BatchMinResults, 100)
	setDefault(&o.BatchMaxResults, buffer.MaxBatchResults)
	setDefault(&o.BatchFlushInterval, 10*time.Second)
	setDefault(&o.BatchAckTimeout, 2*time.Minute)
	setDefault(&o.BackoffBase, time.Second)
	setDefault(&o.BackoffMax, 5*time.Minute)
	setDefault(&o.HealthyAfter, 30*time.Second)
	setDefault(&o.DefaultInventoryInterval, time.Hour)
	setDefault(&o.RenewalRetry, time.Hour)
	setDefault(&o.RenewalCheckInterval, time.Minute)
	setDefault(&o.HandshakeTimeout, 30*time.Second)
	setDefault(&o.WriteTimeout, 30*time.Second)
	if o.Now == nil {
		o.Now = time.Now
	}
}

func setDefault[T int | time.Duration](v *T, def T) {
	if *v <= 0 {
		*v = def
	}
}

// Agent is an enrolled, running agent.
type Agent struct {
	opts      Options
	logger    *slog.Logger
	store     *state.Store
	key       keystore.Key
	buffer    *buffer.Buffer
	scheduler *checks.Scheduler
	jobs      *jobs.Manager

	resultsAdded chan struct{}

	// outbox holds messages from background work (update reports, the watchdog certificate request) for the next accepted session.
	outbox    chan *agentv1.AgentMessage
	connected atomic.Bool
	peer      atomic.Pointer[agentv1.PeerStatus]
	// signedIn is the latest list of signed-in users; nil until it was read once.
	signedIn atomic.Pointer[agentv1.SignedInUsers]
	watchdog *watchdogManager
	// remote serves remote control sessions (0.3.0 step 3).
	remote *remote.Server

	mu             sync.Mutex
	st             *state.State
	trust          signedconfig.Trust
	config         *agentv1.AgentConfig
	appliedPayload []byte

	// runCtx is the context of Run: remote control sessions live as long as the agent, not as long as one control connection.
	runCtx context.Context

	lastBufferError time.Time
	nextRenewal     time.Time
	nextRecovery    time.Time
}

// New loads the state, opens the identity key and the result buffer, and re-verifies the stored configuration.
func New(opts Options) (*Agent, error) {
	opts.setDefaults()
	st, err := opts.Store.Load()
	if err != nil {
		return nil, err
	}
	key, err := keystore.Open(opts.Store.Dir(), st.Key)
	if err != nil {
		return nil, fmt.Errorf("open the identity key: %w", err)
	}
	cert, err := st.Certificate()
	if err != nil {
		_ = key.Close()
		return nil, err
	}
	if !keystore.SamePublicKey(cert, key) {
		_ = key.Close()
		return nil, errors.New("the stored agent certificate does not match the identity key: enroll the agent again")
	}
	signingKey, err := st.SigningKey()
	if err != nil {
		_ = key.Close()
		return nil, err
	}
	buf, err := buffer.Open(filepath.Join(opts.Store.Dir(), BufferFileName), opts.MaxBufferedResults, opts.Logger)
	if err != nil {
		_ = key.Close()
		return nil, err
	}
	a := &Agent{
		opts:         opts,
		logger:       opts.Logger,
		store:        opts.Store,
		key:          key,
		buffer:       buf,
		resultsAdded: make(chan struct{}, 1),
		outbox:       make(chan *agentv1.AgentMessage, 64),
		st:           st,
		trust: signedconfig.Trust{
			SigningKey: signingKey, KeyID: st.SigningKeyID, InstanceID: st.InstanceID, EndpointID: st.EndpointID,
		},
	}
	a.scheduler = checks.NewScheduler(opts.Collector, a.storeResults, opts.Logger)
	a.loadAppliedConfig()
	manager, err := jobs.NewManager(jobs.Options{
		Dir:    filepath.Join(opts.Store.Dir(), JobsDirName),
		Access: opts.Store.Access(),
		Logger: opts.Logger,
		Now:    opts.Now,
		Trust: func() signedconfig.Trust {
			a.mu.Lock()
			defer a.mu.Unlock()
			return a.trust
		},
		Managed: func() bool {
			a.mu.Lock()
			defer a.mu.Unlock()
			return a.config.GetTier() == agentv1.Tier_TIER_MANAGED
		},
	})
	if err != nil {
		_ = buf.Close()
		_ = key.Close()
		return nil, fmt.Errorf("open the job spool: %w", err)
	}
	a.jobs = manager
	a.remote = a.newRemoteServer()
	if opts.Watchdog != nil {
		watchdog, err := newWatchdogManager(a, *opts.Watchdog)
		if err != nil {
			manager.Close()
			_ = buf.Close()
			_ = key.Close()
			return nil, fmt.Errorf("prepare the watchdog: %w", err)
		}
		a.watchdog = watchdog
	}
	return a, nil
}

// loadAppliedConfig re-verifies the persisted configuration. The state directory is protected, but verifying again
// costs nothing and means a modified state file cannot make the agent run anything.
func (a *Agent) loadAppliedConfig() {
	if len(a.st.AppliedConfig) == 0 {
		return
	}
	var sc agentv1.SignedConfig
	if err := proto.Unmarshal(a.st.AppliedConfig, &sc); err != nil {
		a.logger.Error("the stored configuration is unreadable; waiting for the gateway to send it again", "error", err)
		return
	}
	cfg, err := signedconfig.VerifySignature(&sc, a.trust)
	if err != nil || cfg.GetVersion() != a.st.AppliedConfigVersion {
		a.logger.Error("the stored configuration failed verification; waiting for the gateway to send it again", "error", err)
		return
	}
	a.config = cfg
	a.appliedPayload = sc.GetPayload()
}

// Close releases the key and the buffer. Run must have returned.
func (a *Agent) Close() error {
	a.scheduler.Stop()
	a.jobs.Close()
	return errors.Join(a.buffer.Close(), a.key.Close())
}

// State returns a copy of the current state.
func (a *Agent) State() state.State {
	a.mu.Lock()
	defer a.mu.Unlock()
	return *a.st
}

// Run keeps the agent connected until ctx is cancelled. It returns nil on cancellation and ErrRevoked when the
// gateway revoked the agent.
func (a *Agent) Run(ctx context.Context) error {
	a.mu.Lock()
	revoked := a.st.Revoked
	cfg := a.config
	a.mu.Unlock()
	if revoked {
		a.logger.Error("the agent is revoked and does not connect; enroll the agent again")
		return ErrRevoked
	}
	if cfg != nil {
		a.scheduler.Apply(cfg)
	}
	defer a.scheduler.Stop()
	a.mu.Lock()
	a.runCtx = ctx
	a.mu.Unlock()
	if a.watchdog != nil {
		// A replacement of the watchdog binary that a crash interrupted is undone first.
		update.Recover(a.store.Dir(), a.logger)
		watchdogCtx, stopWatchdog := context.WithCancel(ctx)
		defer stopWatchdog()
		safego.Go(a.logger, "watchdog manager", func() { a.watchdog.run(watchdogCtx) })
	}

	usersCtx, stopUsers := context.WithCancel(ctx)
	defer stopUsers()
	safego.Go(a.logger, "signed-in users", func() { a.watchSignedInUsers(usersCtx) })

	bo := backoff.New(a.opts.BackoffBase, a.opts.BackoffMax)
	for {
		started := a.opts.Now()
		var outcome sessionOutcome
		err := safego.Call(a.logger, "session", func() error {
			var err error
			outcome, err = a.runSession(ctx)
			return err
		})
		if ctx.Err() != nil {
			return nil
		}
		switch outcome {
		case outcomeRevoked:
			a.scheduler.Stop()
			return ErrRevoked
		case outcomeRenewed:
			a.logger.Info("reconnecting with the renewed certificate")
			bo.Reset()
			continue
		}
		if outcome == outcomeHealthy && a.opts.Now().Sub(started) >= a.opts.HealthyAfter {
			bo.Reset()
		}
		delay := bo.Next()
		if err != nil {
			a.logger.Warn("gateway session ended", "error", err, "retryIn", delay.Round(time.Millisecond).String())
		}
		select {
		case <-ctx.Done():
			return nil
		case <-time.After(delay):
		}
	}
}

// storeResults is the scheduler sink: results go to disk first, whether or not the gateway is reachable.
func (a *Agent) storeResults(results []*agentv1.CheckResult) {
	if err := a.buffer.Add(results...); err != nil {
		now := a.opts.Now()
		a.mu.Lock()
		report := now.Sub(a.lastBufferError) >= time.Minute
		if report {
			a.lastBufferError = now
		}
		a.mu.Unlock()
		if report {
			a.logger.Error("check results could not be stored and were dropped (is the disk full?)", "error", err, "results", len(results))
		}
		return
	}
	select {
	case a.resultsAdded <- struct{}{}:
	default:
	}
}

// watchSignedInUsers reads the signed-in users now and every interval, off the session loop: listing sessions may run loginctl. A list
// that cannot be read keeps the previous one; the error is logged once until the list can be read again.
func (a *Agent) watchSignedInUsers(ctx context.Context) {
	ticker := time.NewTicker(a.opts.SignedInUsersInterval)
	defer ticker.Stop()
	failing := false
	for {
		users, err := a.opts.SignedInUsers()
		switch {
		case err != nil && !failing:
			failing = true
			a.logger.Warn("the signed-in users could not be read; the list is not updated", "error", err)
		case err == nil:
			failing = false
			a.signedIn.Store(&agentv1.SignedInUsers{Users: users})
		}
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}
