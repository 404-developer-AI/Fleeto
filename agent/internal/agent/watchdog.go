package agent

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"fmt"
	"net/http"
	"os"
	"path/filepath"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/enroll"
	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/release"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
)

// Names of the watchdog service (0.2.1).
const (
	WatchdogServiceName = "fleeto-watchdog"
	WatchdogDisplayName = "Fleeto Watchdog"
	WatchdogDescription = "Fleeto Watchdog: keeps the Fleeto Agent running and installs its updates."
	// WatchdogKeyName is the CNG key name of the watchdog identity.
	WatchdogKeyName = "Fleeto Watchdog Identity"
	// AgentServiceName is the service name of the agent, supervised by the watchdog.
	AgentServiceName = "fleeto-agent"

	watchdogCertificateWait = 2 * time.Minute
	supervisionInterval     = 30 * time.Second
	identityCheckInterval   = 10 * time.Minute
)

// WatchdogOptions makes the agent install, supervise and update the watchdog. Only in service mode; nil in foreground development mode.
type WatchdogOptions struct {
	StateDir   string
	ProgramDir string
	Controller svcctl.Controller
	// Keys are the release public keys; default the keys compiled into this binary.
	Keys []ed25519.PublicKey
	// UpdateMaxDelay spreads updates of many endpoints; default 10 minutes, negative for none (tests).
	UpdateMaxDelay time.Duration
	// UpdateTransientRetry is the first wait after a transient failure; default 1 minute (tests shorten it).
	UpdateTransientRetry time.Duration
	// Create creates the watchdog service; default svcctl.Create.
	Create func(svcctl.Definition) error
	// Delete deletes the watchdog service; default svcctl.Delete.
	Delete func(ctx context.Context, name string, timeout time.Duration) error
}

// watchdogManager is the agent side of the watchdog: it gives the watchdog its identity, installs and updates its binary and service,
// restarts it when it stops, and reports its state in the heartbeat.
type watchdogManager struct {
	a            *Agent
	opts         WatchdogOptions
	supervisor   *update.Supervisor
	updater      *update.Updater
	certificates chan *agentv1.WatchdogCertificateResponse
}

func newWatchdogManager(a *Agent, opts WatchdogOptions) (*watchdogManager, error) {
	if opts.Keys == nil {
		keys, err := version.ParseReleasePublicKeys(version.ReleasePublicKeys)
		if err != nil {
			return nil, err
		}
		opts.Keys = keys
	}
	if opts.Create == nil {
		opts.Create = svcctl.Create
	}
	if opts.Delete == nil {
		opts.Delete = svcctl.Delete
	}
	m := &watchdogManager{a: a, opts: opts, certificates: make(chan *agentv1.WatchdogCertificateResponse, 1)}
	exe := filepath.Join(opts.ProgramDir, platform.WatchdogBinaryName)
	m.supervisor = &update.Supervisor{Controller: opts.Controller, Service: WatchdogServiceName, Exe: exe, Logger: a.logger}
	m.updater = update.NewUpdater(update.UpdaterOptions{
		Target:     update.Target{Component: release.ComponentWatchdog, Service: WatchdogServiceName, Exe: exe, HealthDir: opts.StateDir},
		Keys:       opts.Keys,
		StateDir:   a.store.Dir(),
		Access:     a.store.Access(),
		Controller: opts.Controller,
		Client:     a.releaseClient,
		Report:     a.reportUpdate,
		Logger:     a.logger,
		Connected:  a.connected.Load,
		InstalledVersion: func(ctx context.Context) (string, error) {
			return m.installedVersion(ctx)
		},
		// The watchdog installs the agent first; the agent replaces the watchdog only once it runs that release itself, so the two never
		// replace each other at the same time.
		Ready: func(manifest *release.Manifest) bool {
			own, ok := release.ParseVersion(version.Version)
			target, ok2 := release.ParseVersion(manifest.Version)
			return ok && ok2 && release.Compare(own, target) >= 0
		},
		Provision:      m.provision,
		Pause:          m.supervisor.Pause,
		MaxDelay:       opts.UpdateMaxDelay,
		TransientRetry: opts.UpdateTransientRetry,
	})
	return m, nil
}

// run supervises the watchdog and acts on offers until ctx is cancelled.
func (m *watchdogManager) run(ctx context.Context) {
	safego.Go(m.a.logger, "watchdog updater", func() { m.updater.Run(ctx) })
	supervise := time.NewTicker(supervisionInterval)
	defer supervise.Stop()
	identity := time.NewTicker(identityCheckInterval)
	defer identity.Stop()
	m.check(ctx)
	for {
		select {
		case <-ctx.Done():
			return
		case <-supervise.C:
			m.check(ctx)
		case <-identity.C:
			if err := safego.Call(m.a.logger, "watchdog identity", func() error { return m.repairIdentity(ctx) }); err != nil {
				m.a.logger.Warn("the watchdog identity could not be renewed; retrying later", "error", err)
			}
		}
	}
}

func (m *watchdogManager) check(ctx context.Context) {
	if status := m.supervisor.Check(ctx); status != nil {
		m.a.peer.Store(status)
	}
}

// installedVersion is "" when the watchdog service or its binary is missing.
func (m *watchdogManager) installedVersion(ctx context.Context) (string, error) {
	serviceState, err := m.opts.Controller.Query(WatchdogServiceName)
	if err != nil {
		return "", err
	}
	exe := filepath.Join(m.opts.ProgramDir, platform.WatchdogBinaryName)
	if serviceState == svcctl.StateNotInstalled {
		return "", nil
	}
	if _, err := os.Stat(exe); errors.Is(err, os.ErrNotExist) {
		return "", nil
	}
	return update.ProbeVersion(ctx, exe)
}

// provision installs the watchdog: its identity, its binary and its service.
func (m *watchdogManager) provision(ctx context.Context, staged string, manifest *release.Manifest) error {
	if update.UninstallInProgress() {
		return errors.New("the agent is being uninstalled")
	}
	if err := m.ensureIdentity(ctx, false); err != nil {
		return err
	}
	exe := filepath.Join(m.opts.ProgramDir, platform.WatchdogBinaryName)
	if err := m.opts.Delete(ctx, WatchdogServiceName, time.Minute); err != nil {
		return fmt.Errorf("remove the previous watchdog service: %w", err)
	}
	if err := copyExecutable(staged, exe); err != nil {
		return err
	}
	if err := m.opts.Create(svcctl.Definition{
		Name: WatchdogServiceName, DisplayName: WatchdogDisplayName, Description: WatchdogDescription, Executable: exe, Args: []string{"run"},
	}); err != nil {
		return err
	}
	if err := m.opts.Controller.Start(WatchdogServiceName); err != nil {
		return err
	}
	m.a.logger.Info("watchdog installed", "version", manifest.Version)
	// Report the new service with the next heartbeat instead of after the next supervision interval.
	m.check(ctx)
	return nil
}

// repairIdentity gives an installed watchdog a new certificate when its own is missing, revoked or about to expire unrenewed, then
// restarts it so it connects with the new certificate.
func (m *watchdogManager) repairIdentity(ctx context.Context) error {
	if update.UninstallInProgress() || !m.a.connected.Load() {
		return nil
	}
	if serviceState, err := m.opts.Controller.Query(WatchdogServiceName); err != nil || serviceState == svcctl.StateNotInstalled {
		return err
	}
	changed, err := m.ensureIdentityChanged(ctx)
	if err != nil || !changed {
		return err
	}
	_ = m.opts.Controller.Stop(ctx, WatchdogServiceName, time.Minute)
	return m.opts.Controller.Start(WatchdogServiceName)
}

func (m *watchdogManager) ensureIdentityChanged(ctx context.Context) (bool, error) {
	if m.identityUsable() {
		return false, nil
	}
	return true, m.ensureIdentity(ctx, true)
}

// identityUsable: the watchdog state exists, is not revoked, its certificate is valid for more than a week and matches its key.
func (m *watchdogManager) identityUsable() bool {
	st, err := state.NewStore(m.opts.StateDir, m.a.store.Access()).Load()
	if err != nil || st.Revoked {
		return false
	}
	agentState := m.a.State()
	if st.EndpointID != agentState.EndpointID || st.InstanceID != agentState.InstanceID {
		return false
	}
	cert, err := st.Certificate()
	if err != nil || m.a.opts.Now().Add(7*24*time.Hour).After(cert.NotAfter) {
		return false
	}
	key, err := keystore.Open(m.opts.StateDir, st.Key)
	if err != nil {
		return false
	}
	defer key.Close()
	return keystore.SamePublicKey(cert, key)
}

// ensureIdentity creates a new watchdog key and gets its certificate over the agent session, unless the current identity is usable.
func (m *watchdogManager) ensureIdentity(ctx context.Context, force bool) error {
	if !force && m.identityUsable() {
		return nil
	}
	if !m.a.connected.Load() {
		return update.Transient(errors.New("the agent is not connected, so the watchdog cannot get a certificate now"))
	}
	access := m.a.store.Access()
	if err := platform.EnsureProtectedDir(m.opts.StateDir, access); err != nil {
		return err
	}
	agentState := m.a.State()
	ref := state.KeyRef{Kind: agentState.Key.Kind, Machine: agentState.Key.Machine}
	switch ref.Kind {
	case keystore.KindCNG:
		ref.Name = WatchdogKeyName
	case keystore.KindFile:
		ref.File = "watchdog-identity.key"
	case keystore.KindTPM:
		ref.File = "watchdog-identity.tpmkey"
	}
	if err := keystore.Delete(m.opts.StateDir, ref); err != nil {
		return fmt.Errorf("remove the previous watchdog key: %w", err)
	}
	key, ref, err := keystore.Create(m.opts.StateDir, ref, access)
	if err != nil {
		return fmt.Errorf("create the watchdog key: %w", err)
	}
	defer key.Close()
	csr, err := keystore.CreateCSR(rand.Reader, key, inventory.Hostname())
	if err != nil {
		return err
	}

	// Drop an answer to an earlier request that is no longer awaited.
	select {
	case <-m.certificates:
	default:
	}
	if !m.a.enqueue(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_WatchdogCertificate{
		WatchdogCertificate: &agentv1.WatchdogCertificateRequest{CsrDer: csr},
	}}) {
		return update.Transient(errors.New("the request for a watchdog certificate could not be queued"))
	}
	var response *agentv1.WatchdogCertificateResponse
	select {
	case <-ctx.Done():
		return ctx.Err()
	case response = <-m.certificates:
	case <-time.After(watchdogCertificateWait):
		return update.Transient(errors.New("no answer to the watchdog certificate request"))
	}
	if response.GetError() != "" && response.GetTemporary() {
		return update.Transient(fmt.Errorf("the watchdog certificate could not be issued right now: %s", response.GetError()))
	}
	if response.GetError() != "" {
		return fmt.Errorf("the gateway refused the watchdog certificate: %s", response.GetError())
	}
	ca, err := agentState.CACertificate()
	if err != nil {
		return err
	}
	cert, err := enroll.ValidateAgentCertificate(response.GetCertificateDer(), ca, key, agentState.EndpointID, m.a.opts.Now())
	if err != nil {
		return fmt.Errorf("the watchdog certificate is invalid: %w", err)
	}
	watchdogState := &state.State{
		Server: agentState.Server, EndpointID: agentState.EndpointID, InstanceID: agentState.InstanceID,
		CACertificatePEM: agentState.CACertificatePEM, SigningPublicKey: agentState.SigningPublicKey, SigningKeyID: agentState.SigningKeyID,
		CertificatePEM: state.EncodeCertificatePEM(cert.Raw), Key: ref, EnrolledAt: m.a.opts.Now().UTC(),
	}
	if err := state.NewStore(m.opts.StateDir, access).Save(watchdogState); err != nil {
		return fmt.Errorf("save the watchdog state: %w", err)
	}
	m.a.logger.Info("the watchdog has a new certificate", "expires", cert.NotAfter.UTC().Format(time.RFC3339), "keyStore", key.Description())
	return nil
}

// offer hands a release offer to the watchdog updater.
func (m *watchdogManager) offer(o *agentv1.UpdateOffer) {
	m.updater.Offer(o.GetManifest(), o.GetSignature(), o.GetUpdateAllowed())
}

func (m *watchdogManager) certificateResponse(r *agentv1.WatchdogCertificateResponse) {
	select {
	case m.certificates <- r:
	default:
	}
}

// releaseClient is an HTTP client with the agent certificate and the pinned instance CA, for release downloads.
func (a *Agent) releaseClient() (*http.Client, string, error) {
	st := a.State()
	tlsConf, _, err := a.tlsConfig(&st)
	if err != nil {
		return nil, "", err
	}
	return &http.Client{
		Transport: &http.Transport{Proxy: nil, TLSClientConfig: tlsConf, TLSHandshakeTimeout: a.opts.HandshakeTimeout},
		Timeout:   15 * time.Minute,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return errors.New("the gateway answered with a redirect, which downloads do not follow")
		},
	}, st.Server, nil
}

// reportUpdate queues an update report for the gateway.
func (a *Agent) reportUpdate(s update.Status) {
	a.enqueue(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_UpdateStatus{UpdateStatus: s.Message()}})
}

// enqueue queues a message for the next accepted session. Returns false when the queue is full.
func (a *Agent) enqueue(msg *agentv1.AgentMessage) bool {
	select {
	case a.outbox <- msg:
		return true
	default:
		a.logger.Warn("a message for the gateway was dropped: the queue is full")
		return false
	}
}

// writeHealthConnected refreshes the health file during a session, keeping the time the session was accepted.
func (a *Agent) writeHealthConnected() {
	if h, err := update.ReadHealth(a.store.Dir()); err == nil && h.Connected && h.PID == os.Getpid() {
		h.UpdatedAt = a.opts.Now().UTC()
		_ = update.WriteHealth(a.store.Dir(), a.store.Access(), h)
		return
	}
	a.writeHealth(true)
}

// writeHealth tells the watchdog whether this agent is connected.
func (a *Agent) writeHealth(connected bool) {
	now := a.opts.Now().UTC()
	h := update.Health{Component: release.ComponentAgent, Version: version.Version, PID: os.Getpid(), Connected: connected, UpdatedAt: now}
	if connected {
		h.ConnectedAt = now
	}
	if err := update.WriteHealth(a.store.Dir(), a.store.Access(), h); err != nil {
		a.logger.Debug("could not write the health file", "error", err)
	}
}

func copyExecutable(from, to string) error {
	data, err := os.ReadFile(from)
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(to), 0o755); err != nil {
		return err
	}
	tmp := to + ".new"
	if err := os.WriteFile(tmp, data, 0o755); err != nil {
		return fmt.Errorf("write %s: %w", tmp, err)
	}
	if err := os.Rename(tmp, to); err != nil {
		_ = os.Remove(tmp)
		return fmt.Errorf("install %s: %w", to, err)
	}
	return nil
}
