// Package watchdog is the runtime of fleeto-watchdog (0.2.1): the second service on an endpoint. It keeps the agent service running,
// installs agent updates from verified releases and rolls them back when the new version does not come up, and holds its own gateway
// session with its own certificate, so the instance can tell a stopped agent from an endpoint that is gone. The agent gave it its
// identity; the watchdog renews its certificate itself.
package watchdog

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"log/slog"
	"net"
	"net/http"
	"os"
	"path/filepath"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/backoff"
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

const (
	// ServiceName is the watchdog service.
	ServiceName = "fleeto-watchdog"
	// AgentServiceName is the agent service the watchdog supervises.
	AgentServiceName = "fleeto-agent"

	connectPath      = "/v1/connect"
	maxMessageBytes  = 4 * 1024 * 1024
	defaultHeartbeat = 30 * time.Second
	renewalWait      = 2 * time.Minute
)

// ErrNotProvisioned means the agent has not given the watchdog an identity yet.
var ErrNotProvisioned = errors.New("the watchdog has no identity yet; the agent provisions it")

// Options configures the watchdog. Zero values get production defaults.
type Options struct {
	Store         *state.Store
	AgentStateDir string
	ProgramDir    string
	Controller    svcctl.Controller
	Logger        *slog.Logger
	Keys          []ed25519.PublicKey
	// UpdateMaxDelay spreads agent updates; negative for none (tests).
	UpdateMaxDelay   time.Duration
	HandshakeTimeout time.Duration
	Now              func() time.Time
}

// Watchdog is a provisioned watchdog.
type Watchdog struct {
	opts       Options
	logger     *slog.Logger
	store      *state.Store
	key        keystore.Key
	st         atomic.Pointer[state.State]
	connected  atomic.Bool
	peer       atomic.Pointer[agentv1.PeerStatus]
	outbox     chan *agentv1.AgentMessage
	supervisor *update.Supervisor
	updater    *update.Updater
}

// New loads the identity the agent provisioned.
func New(opts Options) (*Watchdog, error) {
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.HandshakeTimeout <= 0 {
		opts.HandshakeTimeout = 30 * time.Second
	}
	if opts.Keys == nil {
		keys, err := version.ParseReleasePublicKeys(version.ReleasePublicKeys)
		if err != nil {
			return nil, err
		}
		opts.Keys = keys
	}
	st, err := opts.Store.Load()
	if errors.Is(err, state.ErrNotEnrolled) {
		return nil, ErrNotProvisioned
	}
	if err != nil {
		return nil, err
	}
	key, err := keystore.Open(opts.Store.Dir(), st.Key)
	if err != nil {
		return nil, fmt.Errorf("open the watchdog key: %w", err)
	}
	cert, err := st.Certificate()
	if err != nil || !keystore.SamePublicKey(cert, key) {
		_ = key.Close()
		return nil, errors.New("the watchdog certificate does not match its key; the agent provisions a new identity")
	}
	w := &Watchdog{opts: opts, logger: opts.Logger, store: opts.Store, key: key, outbox: make(chan *agentv1.AgentMessage, 32)}
	w.st.Store(st)
	agentExe := filepath.Join(opts.ProgramDir, platform.BinaryName)
	w.supervisor = &update.Supervisor{Controller: opts.Controller, Service: AgentServiceName, Exe: agentExe, Logger: opts.Logger}
	w.updater = update.NewUpdater(update.UpdaterOptions{
		Target:     update.Target{Component: release.ComponentAgent, Service: AgentServiceName, Exe: agentExe, HealthDir: opts.AgentStateDir},
		Keys:       opts.Keys,
		StateDir:   opts.Store.Dir(),
		Access:     opts.Store.Access(),
		Controller: opts.Controller,
		Client:     w.releaseClient,
		Report:     w.reportUpdate,
		Logger:     opts.Logger,
		Connected:  w.connected.Load,
		InstalledVersion: func(ctx context.Context) (string, error) {
			serviceState, err := opts.Controller.Query(AgentServiceName)
			if err != nil || serviceState == svcctl.StateNotInstalled {
				// Without an agent service there is nothing to update; the watchdog never installs the agent itself.
				return "", err
			}
			return update.ProbeVersion(ctx, agentExe)
		},
		Pause:    w.supervisor.Pause,
		MaxDelay: opts.UpdateMaxDelay,
	})
	return w, nil
}

// Close releases the key.
func (w *Watchdog) Close() error { return w.key.Close() }

// Run supervises the agent and keeps the gateway session until ctx is cancelled. A revoked or expired certificate stops the session
// but not the supervision: the agent gives the watchdog a new identity and restarts it.
func (w *Watchdog) Run(ctx context.Context) error {
	update.Recover(w.store.Dir(), w.logger)
	safego.Go(w.logger, "agent updater", func() { w.updater.Run(ctx) })
	safego.Go(w.logger, "agent supervisor", func() { w.supervise(ctx) })
	defer w.writeHealth(false)

	bo := backoff.New(time.Second, 5*time.Minute)
	for ctx.Err() == nil {
		st := w.st.Load()
		if st.Revoked {
			w.logger.Error("the watchdog certificate was revoked; waiting for the agent to provision a new identity")
			<-ctx.Done()
			return nil
		}
		started := w.opts.Now()
		renewed, err := w.session(ctx)
		if ctx.Err() != nil {
			return nil
		}
		if renewed {
			bo.Reset()
			continue
		}
		if w.opts.Now().Sub(started) > time.Minute {
			bo.Reset()
		}
		delay := bo.Next()
		if err != nil {
			w.logger.Warn("gateway session ended", "error", err, "retryIn", delay.Round(time.Millisecond).String())
		}
		select {
		case <-ctx.Done():
		case <-time.After(delay):
		}
	}
	return nil
}

func (w *Watchdog) supervise(ctx context.Context) {
	ticker := time.NewTicker(30 * time.Second)
	defer ticker.Stop()
	for {
		if status := w.supervisor.Check(ctx); status != nil {
			w.peer.Store(status)
		}
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
	}
}

func (w *Watchdog) tlsConfig(st *state.State) (*tls.Config, *x509.Certificate, error) {
	host, _, err := net.SplitHostPort(st.Server)
	if err != nil {
		return nil, nil, fmt.Errorf("the stored server %q is invalid: %w", st.Server, err)
	}
	cert, err := st.Certificate()
	if err != nil {
		return nil, nil, err
	}
	ca, err := st.CACertificate()
	if err != nil {
		return nil, nil, err
	}
	roots := x509.NewCertPool()
	roots.AddCert(ca)
	return &tls.Config{
		MinVersion:   tls.VersionTLS12,
		RootCAs:      roots,
		ServerName:   host,
		Certificates: []tls.Certificate{{Certificate: [][]byte{cert.Raw}, PrivateKey: w.key, Leaf: cert}},
	}, cert, nil
}

// session runs one gateway session. renewed reports that the certificate was renewed and the session must reconnect at once.
func (w *Watchdog) session(ctx context.Context) (renewed bool, err error) {
	st := w.st.Load()
	tlsConf, cert, err := w.tlsConfig(st)
	if err != nil {
		return false, err
	}
	if w.opts.Now().After(cert.NotAfter) {
		// A watchdog does not recover an expired certificate; the agent provisions a new one.
		return false, errors.New("the watchdog certificate has expired; waiting for the agent to provision a new one")
	}
	transport := &http.Transport{Proxy: nil, TLSClientConfig: tlsConf, TLSHandshakeTimeout: w.opts.HandshakeTimeout}
	defer transport.CloseIdleConnections()
	dialCtx, cancelDial := context.WithTimeout(ctx, w.opts.HandshakeTimeout)
	conn, resp, err := websocket.Dial(dialCtx, "wss://"+st.Server+connectPath, &websocket.DialOptions{HTTPClient: &http.Client{Transport: transport}})
	cancelDial()
	if err != nil {
		if resp != nil {
			return false, fmt.Errorf("the gateway at %s refused the connection (HTTP %d): %w", st.Server, resp.StatusCode, err)
		}
		return false, fmt.Errorf("cannot connect to the gateway at %s: %w", st.Server, err)
	}
	defer conn.CloseNow()
	conn.SetReadLimit(maxMessageBytes)
	defer func() {
		w.connected.Store(false)
		w.writeHealth(false)
	}()

	sessCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	incoming := make(chan *agentv1.ServerMessage, 16)
	readErr := make(chan error, 1)
	safego.Go(w.logger, "watchdog session reader", func() {
		for {
			typ, data, err := conn.Read(sessCtx)
			if err != nil {
				readErr <- err
				return
			}
			if typ != websocket.MessageBinary {
				continue
			}
			var msg agentv1.ServerMessage
			if proto.Unmarshal(data, &msg) != nil {
				continue
			}
			select {
			case incoming <- &msg:
			case <-sessCtx.Done():
				return
			}
		}
	})

	send := func(msg *agentv1.AgentMessage) error {
		data, err := proto.Marshal(msg)
		if err != nil {
			return err
		}
		writeCtx, cancel := context.WithTimeout(sessCtx, 30*time.Second)
		defer cancel()
		return conn.Write(writeCtx, websocket.MessageBinary, data)
	}
	heartbeat := func() error {
		w.writeHealth(true)
		return send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_Heartbeat{
			Heartbeat: &agentv1.Heartbeat{AgentTime: timestamppb.New(w.opts.Now()), Peer: w.peer.Load()},
		}})
	}

	osInfo := inventory.OSInfo(sessCtx)
	if err := send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_Hello{Hello: &agentv1.Hello{
		AgentVersion: version.Version, Hostname: inventory.Hostname(), Os: osInfo, Component: agentv1.Component_COMPONENT_WATCHDOG,
	}}}); err != nil {
		return false, err
	}

	acked := false
	helloTimer := time.NewTimer(w.opts.HandshakeTimeout)
	defer helloTimer.Stop()
	ticker := time.NewTicker(time.Hour)
	ticker.Stop()
	defer ticker.Stop()
	renewal := time.NewTicker(time.Minute)
	defer renewal.Stop()
	var renewalSentAt time.Time
	var nextRenewal time.Time

	for {
		var outbox chan *agentv1.AgentMessage
		if acked {
			outbox = w.outbox
		}
		select {
		case <-ctx.Done():
			_ = conn.Close(websocket.StatusGoingAway, "watchdog stopping")
			return false, nil
		case err := <-readErr:
			return false, fmt.Errorf("connection lost: %w", err)
		case <-helloTimer.C:
			if !acked {
				return false, errors.New("the gateway did not answer Hello in time")
			}
		case msg := <-outbox:
			if err := send(msg); err != nil {
				return false, err
			}
		case <-ticker.C:
			if err := heartbeat(); err != nil {
				return false, err
			}
		case <-renewal.C:
			if !acked || !renewalSentAt.IsZero() && w.opts.Now().Sub(renewalSentAt) < renewalWait || w.opts.Now().Before(nextRenewal) {
				continue
			}
			lifetime := cert.NotAfter.Sub(cert.NotBefore)
			if w.opts.Now().Before(cert.NotBefore.Add(lifetime * 2 / 3)) {
				continue
			}
			csr, err := keystore.CreateCSR(rand.Reader, w.key, inventory.Hostname())
			if err != nil {
				nextRenewal = w.opts.Now().Add(time.Hour)
				continue
			}
			if err := send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_RenewCertificate{RenewCertificate: &agentv1.RenewCertificateRequest{CsrDer: csr}}}); err != nil {
				return false, err
			}
			renewalSentAt = w.opts.Now()
			w.logger.Info("requested renewal of the watchdog certificate", "expires", cert.NotAfter.UTC().Format(time.RFC3339))
		case msg := <-incoming:
			switch body := msg.GetBody().(type) {
			case *agentv1.ServerMessage_HelloAck:
				acked = true
				interval := time.Duration(body.HelloAck.GetHeartbeatIntervalSeconds()) * time.Second
				if interval <= 0 {
					interval = defaultHeartbeat
				}
				ticker.Reset(interval)
				w.connected.Store(true)
				w.logger.Info("connected to the gateway", "server", st.Server)
				if err := heartbeat(); err != nil {
					return false, err
				}
			case *agentv1.ServerMessage_Ping:
				if err := send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_Pong{Pong: &agentv1.Pong{Nonce: body.Ping.GetNonce()}}}); err != nil {
					return false, err
				}
			case *agentv1.ServerMessage_UpdateOffer:
				w.updater.Offer(body.UpdateOffer.GetManifest(), body.UpdateOffer.GetSignature(), body.UpdateOffer.GetUpdateAllowed())
			case *agentv1.ServerMessage_RenewCertificate:
				renewalSentAt = time.Time{}
				if w.storeRenewal(body.RenewCertificate) {
					_ = conn.Close(websocket.StatusNormalClosure, "certificate renewed")
					return true, nil
				}
				nextRenewal = w.opts.Now().Add(time.Hour)
			case *agentv1.ServerMessage_Disconnect:
				if body.Disconnect.GetCode() == agentv1.DisconnectCode_DISCONNECT_CODE_REVOKED {
					w.markRevoked(body.Disconnect.GetReason())
					_ = conn.Close(websocket.StatusNormalClosure, "revoked")
					return false, nil
				}
				return false, fmt.Errorf("disconnected by the gateway: %s %s", body.Disconnect.GetCode(), body.Disconnect.GetReason())
			default:
				// A watchdog only handles the messages above; anything else is not meant for it.
			}
		}
	}
}

func (w *Watchdog) storeRenewal(resp *agentv1.RenewCertificateResponse) bool {
	if resp.GetError() != "" {
		w.logger.Warn("the gateway refused the watchdog certificate renewal", "reason", resp.GetError())
		return false
	}
	st := w.st.Load()
	ca, err := st.CACertificate()
	if err != nil {
		return false
	}
	current, err := st.Certificate()
	if err != nil {
		return false
	}
	cert, err := enroll.ValidateAgentCertificate(resp.GetCertificateDer(), ca, w.key, st.EndpointID, w.opts.Now())
	if err != nil || !cert.NotAfter.After(current.NotAfter) {
		w.logger.Warn("the renewed watchdog certificate is invalid; keeping the current one", "error", err)
		return false
	}
	updated, err := w.store.Update(func(s *state.State) error {
		s.CertificatePEM = state.EncodeCertificatePEM(cert.Raw)
		s.CertificateRenewedAt = w.opts.Now().UTC()
		return nil
	})
	if err != nil {
		w.logger.Warn("could not store the renewed watchdog certificate", "error", err)
		return false
	}
	w.st.Store(updated)
	w.logger.Info("watchdog certificate renewed", "expires", cert.NotAfter.UTC().Format(time.RFC3339))
	return true
}

func (w *Watchdog) markRevoked(reason string) {
	updated, err := w.store.Update(func(s *state.State) error {
		s.Revoked = true
		s.RevokedReason = reason
		s.RevokedAt = w.opts.Now().UTC()
		return nil
	})
	if err != nil {
		w.logger.Error("could not record the revocation", "error", err)
		return
	}
	w.st.Store(updated)
}

func (w *Watchdog) releaseClient() (*http.Client, string, error) {
	st := w.st.Load()
	tlsConf, _, err := w.tlsConfig(st)
	if err != nil {
		return nil, "", err
	}
	return &http.Client{
		Transport: &http.Transport{Proxy: nil, TLSClientConfig: tlsConf, TLSHandshakeTimeout: w.opts.HandshakeTimeout},
		Timeout:   15 * time.Minute,
		CheckRedirect: func(*http.Request, []*http.Request) error {
			return errors.New("the gateway answered with a redirect, which downloads do not follow")
		},
	}, st.Server, nil
}

func (w *Watchdog) reportUpdate(s update.Status) {
	component := agentv1.Component_COMPONENT_AGENT
	if s.Component == release.ComponentWatchdog {
		component = agentv1.Component_COMPONENT_WATCHDOG
	}
	select {
	case w.outbox <- &agentv1.AgentMessage{Body: &agentv1.AgentMessage_UpdateStatus{UpdateStatus: &agentv1.UpdateStatus{
		Component: component, Version: s.Version, State: s.State, Detail: s.Detail,
	}}}:
	default:
	}
}

func (w *Watchdog) writeHealth(connected bool) {
	now := w.opts.Now().UTC()
	h := update.Health{Component: release.ComponentWatchdog, Version: version.Version, PID: os.Getpid(), Connected: connected, UpdatedAt: now}
	if connected {
		if previous, err := update.ReadHealth(w.store.Dir()); err == nil && previous.Connected && previous.PID == os.Getpid() {
			h.ConnectedAt = previous.ConnectedAt
		} else {
			h.ConnectedAt = now
		}
	}
	_ = update.WriteHealth(w.store.Dir(), w.store.Access(), h)
}
