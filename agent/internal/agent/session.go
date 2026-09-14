package agent

import (
	"context"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"errors"
	"fmt"
	"net"
	"net/http"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/enroll"
	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
)

const (
	// ConnectPath is the WebSocket endpoint of the gateway.
	ConnectPath = "/v1/connect"
	// MaxMessageBytes is the protocol limit for one WebSocket message.
	MaxMessageBytes = 4 * 1024 * 1024
	// DefaultHeartbeat is used until HelloAck or a configuration sets an interval.
	DefaultHeartbeat   = 30 * time.Second
	renewalWait        = 2 * time.Minute
	maxInventoryTrials = 8
)

type sessionOutcome int

const (
	// outcomeFailed: the session never became healthy or ended with an error; back off further.
	outcomeFailed sessionOutcome = iota
	// outcomeHealthy: the session reached HelloAck.
	outcomeHealthy
	// outcomeRevoked: the gateway revoked the agent; stop reconnecting.
	outcomeRevoked
	// outcomeRenewed: the certificate was renewed; reconnect at once with the new one.
	outcomeRenewed
)

type session struct {
	a    *Agent
	ctx  context.Context
	conn *websocket.Conn

	incoming chan *agentv1.ServerMessage
	readErr  chan error

	acked            bool
	ackHeartbeat     time.Duration
	heartbeat        *time.Ticker
	inventoryTimer   *time.Timer
	inflight         uint64
	inflightSentAt   time.Time
	lastFlush        time.Time
	renewalPending   bool
	renewalSentAt    time.Time
	inventoryRunning bool
	inventoryForce   bool
	inventoryReady   chan *agentv1.Inventory
	startedAt        time.Time
}

// tlsConfig builds the mTLS configuration: the pinned instance CA is the only root, the client certificate is the
// agent certificate with the key from the key store.
func (a *Agent) tlsConfig(st *state.State) (*tls.Config, *x509.Certificate, error) {
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
		MinVersion: tls.VersionTLS12,
		RootCAs:    roots,
		ServerName: host,
		Certificates: []tls.Certificate{{
			Certificate: [][]byte{cert.Raw},
			PrivateKey:  a.key,
			Leaf:        cert,
		}},
	}, cert, nil
}

func (a *Agent) runSession(ctx context.Context) (sessionOutcome, error) {
	st := a.State()
	tlsConf, cert, err := a.tlsConfig(&st)
	if err != nil {
		return outcomeFailed, err
	}
	now := a.opts.Now()
	if now.After(cert.NotAfter) {
		a.logger.Error("the agent certificate has expired; enroll the agent again if the system clock is correct",
			"expired", cert.NotAfter.UTC().Format(time.RFC3339))
	}

	transport := &http.Transport{
		Proxy:               nil,
		TLSClientConfig:     tlsConf,
		TLSHandshakeTimeout: a.opts.HandshakeTimeout,
	}
	defer transport.CloseIdleConnections()
	dialCtx, cancelDial := context.WithTimeout(ctx, a.opts.HandshakeTimeout)
	conn, resp, err := websocket.Dial(dialCtx, "wss://"+st.Server+ConnectPath, &websocket.DialOptions{
		HTTPClient: &http.Client{Transport: transport},
	})
	cancelDial()
	if err != nil {
		if resp != nil {
			return outcomeFailed, fmt.Errorf("the gateway at %s refused the connection (HTTP %d): %w", st.Server, resp.StatusCode, err)
		}
		return outcomeFailed, fmt.Errorf("cannot connect to the gateway at %s: %w", st.Server, err)
	}
	defer conn.CloseNow()
	conn.SetReadLimit(MaxMessageBytes)

	sessCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	s := &session{
		a:              a,
		ctx:            sessCtx,
		conn:           conn,
		incoming:       make(chan *agentv1.ServerMessage, 16),
		readErr:        make(chan error, 1),
		inventoryReady: make(chan *agentv1.Inventory, 1),
		startedAt:      now,
		lastFlush:      now,
	}
	safego.Go(a.logger, "session reader", s.readLoop)
	a.logger.Info("connected to the gateway", "server", st.Server)
	return s.loop()
}

func (s *session) readLoop() {
	for {
		typ, data, err := s.conn.Read(s.ctx)
		if err != nil {
			s.readErr <- err
			return
		}
		if typ != websocket.MessageBinary {
			s.a.logger.Warn("ignored a non-binary message from the gateway")
			continue
		}
		var msg agentv1.ServerMessage
		if err := proto.Unmarshal(data, &msg); err != nil {
			s.a.logger.Warn("ignored an unreadable message from the gateway", "error", err, "bytes", len(data))
			continue
		}
		select {
		case s.incoming <- &msg:
		case <-s.ctx.Done():
			return
		}
	}
}

func (s *session) outcome() sessionOutcome {
	if s.acked {
		return outcomeHealthy
	}
	return outcomeFailed
}

func (s *session) loop() (sessionOutcome, error) {
	a := s.a
	if err := s.sendHello(); err != nil {
		return outcomeFailed, err
	}
	helloTimer := time.NewTimer(a.opts.HandshakeTimeout)
	defer helloTimer.Stop()
	tick := time.Second
	if a.opts.BatchFlushInterval/2 < tick {
		tick = max(a.opts.BatchFlushInterval/2, 10*time.Millisecond)
	}
	flush := time.NewTicker(tick)
	defer flush.Stop()
	renewal := time.NewTicker(a.opts.RenewalCheckInterval)
	defer renewal.Stop()
	s.heartbeat = time.NewTicker(time.Hour)
	s.heartbeat.Stop()
	defer s.heartbeat.Stop()
	s.inventoryTimer = time.NewTimer(time.Hour)
	s.inventoryTimer.Stop()
	defer s.inventoryTimer.Stop()

	for {
		var err error
		select {
		case <-s.ctx.Done():
			_ = s.conn.Close(websocket.StatusGoingAway, "agent stopping")
			return s.outcome(), nil
		case err := <-s.readErr:
			return s.outcome(), fmt.Errorf("connection lost: %w", err)
		case <-helloTimer.C:
			if !s.acked {
				return outcomeFailed, errors.New("the gateway did not answer Hello in time")
			}
		case msg := <-s.incoming:
			var outcome sessionOutcome
			var done bool
			perr := safego.Call(a.logger, "message handler", func() error {
				var herr error
				outcome, done, herr = s.handle(msg)
				return herr
			})
			if done {
				return outcome, perr
			}
			err = perr
		case <-a.resultsAdded:
			err = s.maybeSendBatch()
		case <-flush.C:
			err = s.maybeSendBatch()
		case <-s.heartbeat.C:
			err = s.send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_Heartbeat{
				Heartbeat: &agentv1.Heartbeat{AgentTime: timestamppb.New(a.opts.Now())},
			}})
		case <-s.inventoryTimer.C:
			s.startInventory(false)
			s.inventoryTimer.Reset(s.inventoryInterval())
		case inv := <-s.inventoryReady:
			err = s.inventoryCollected(inv)
		case <-renewal.C:
			err = s.maybeRenew()
		}
		if err != nil {
			if errors.Is(err, errWrite) {
				return s.outcome(), err
			}
			a.logger.Warn("error while handling the gateway session", "error", err)
		}
	}
}

var errWrite = errors.New("write to the gateway failed")

func (s *session) send(msg *agentv1.AgentMessage) error {
	data, err := proto.Marshal(msg)
	if err != nil {
		return fmt.Errorf("encode message: %w", err)
	}
	if len(data) > MaxMessageBytes {
		return fmt.Errorf("message of %d bytes exceeds the protocol limit and was not sent", len(data))
	}
	ctx, cancel := context.WithTimeout(s.ctx, s.a.opts.WriteTimeout)
	defer cancel()
	if err := s.conn.Write(ctx, websocket.MessageBinary, data); err != nil {
		return fmt.Errorf("%w: %w", errWrite, err)
	}
	return nil
}

func (s *session) sendHello() error {
	a := s.a
	a.mu.Lock()
	hash := a.st.InventoryHash
	applied := a.config.GetVersion()
	a.mu.Unlock()
	return s.send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_Hello{Hello: &agentv1.Hello{
		AgentVersion:  version.Version,
		Hostname:      inventory.Hostname(),
		Os:            a.opts.OSInfo(s.ctx),
		ConfigVersion: applied,
		InventoryHash: hash,
	}}})
}

// handle processes one server message. done reports that the session must end with the given outcome.
func (s *session) handle(msg *agentv1.ServerMessage) (sessionOutcome, bool, error) {
	a := s.a
	switch body := msg.GetBody().(type) {
	case *agentv1.ServerMessage_HelloAck:
		return outcomeHealthy, false, s.handleHelloAck(body.HelloAck)
	case *agentv1.ServerMessage_Ping:
		return 0, false, s.send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_Pong{Pong: &agentv1.Pong{Nonce: body.Ping.GetNonce()}}})
	case *agentv1.ServerMessage_Config:
		return 0, false, s.handleConfig(body.Config)
	case *agentv1.ServerMessage_BatchAck:
		return 0, false, s.handleBatchAck(body.BatchAck.GetSequence())
	case *agentv1.ServerMessage_RenewCertificate:
		if s.handleRenewal(body.RenewCertificate) {
			_ = s.conn.Close(websocket.StatusNormalClosure, "certificate renewed")
			return outcomeRenewed, true, nil
		}
		return 0, false, nil
	case *agentv1.ServerMessage_InventoryRequest:
		s.startInventory(true)
		return 0, false, nil
	case *agentv1.ServerMessage_Disconnect:
		return s.handleDisconnect(body.Disconnect)
	default:
		a.logger.Warn("ignored an unknown message from the gateway")
		return 0, false, nil
	}
}

func (s *session) handleHelloAck(ack *agentv1.HelloAck) error {
	a := s.a
	a.mu.Lock()
	endpointID := a.st.EndpointID
	a.mu.Unlock()
	if ack.GetEndpointId() != "" && ack.GetEndpointId() != endpointID {
		a.logger.Warn("the gateway names a different endpoint id", "expected", endpointID, "received", ack.GetEndpointId())
	}
	first := !s.acked
	s.acked = true
	s.ackHeartbeat = time.Duration(ack.GetHeartbeatIntervalSeconds()) * time.Second
	s.heartbeat.Reset(s.heartbeatInterval())
	if !first {
		return nil
	}
	if ack.GetServerTime() != nil {
		skew := a.opts.Now().Sub(ack.GetServerTime().AsTime())
		if skew > 5*time.Minute || skew < -5*time.Minute {
			a.logger.Warn("the system clock differs from the gateway clock", "skew", skew.Round(time.Second).String())
		}
	}
	// Inventory right away (sent only when it changed since the last report), then on the interval.
	s.startInventory(false)
	s.inventoryTimer.Reset(s.inventoryInterval())
	if err := s.maybeRenew(); err != nil {
		return err
	}
	// Resend the unacknowledged batch from before the reconnect.
	return s.maybeSendBatch()
}

func (s *session) heartbeatInterval() time.Duration {
	s.a.mu.Lock()
	fromConfig := time.Duration(s.a.config.GetHeartbeatIntervalSeconds()) * time.Second
	s.a.mu.Unlock()
	switch {
	case fromConfig > 0:
		return fromConfig
	case s.ackHeartbeat > 0:
		return s.ackHeartbeat
	default:
		return DefaultHeartbeat
	}
}

func (s *session) inventoryInterval() time.Duration {
	s.a.mu.Lock()
	fromConfig := time.Duration(s.a.config.GetInventoryIntervalSeconds()) * time.Second
	s.a.mu.Unlock()
	if fromConfig > 0 {
		return max(fromConfig, time.Minute)
	}
	return s.a.opts.DefaultInventoryInterval
}

func (s *session) handleConfig(sc *agentv1.SignedConfig) error {
	a := s.a
	a.mu.Lock()
	applied := a.config.GetVersion()
	appliedPayload := a.appliedPayload
	trust := a.trust
	a.mu.Unlock()

	cfg, err := signedconfig.Verify(sc, trust, applied, appliedPayload)
	if errors.Is(err, signedconfig.ErrAlreadyApplied) {
		return s.send(configApplied(applied, ""))
	}
	if err != nil {
		a.logger.Warn("refused a configuration", "error", err, "appliedVersion", applied)
		return s.send(configApplied(applied, err.Error()))
	}
	raw, err := proto.Marshal(sc)
	if err != nil {
		return s.send(configApplied(applied, "the agent could not encode the configuration"))
	}
	st, err := a.store.Update(func(st *state.State) error {
		st.AppliedConfig = raw
		st.AppliedConfigVersion = cfg.GetVersion()
		return nil
	})
	if err != nil {
		a.logger.Error("could not store a verified configuration; keeping the current one", "error", err)
		return s.send(configApplied(applied, "the agent could not store the configuration: "+err.Error()))
	}
	a.mu.Lock()
	a.st = st
	a.config = cfg
	a.appliedPayload = sc.GetPayload()
	a.mu.Unlock()
	a.scheduler.Apply(cfg)
	if s.acked {
		s.heartbeat.Reset(s.heartbeatInterval())
	}
	a.logger.Info("configuration applied", "version", cfg.GetVersion(), "tier", cfg.GetTier().String(),
		"checks", len(signedconfig.EffectiveChecks(cfg)))
	return s.send(configApplied(cfg.GetVersion(), ""))
}

func configApplied(v uint64, errText string) *agentv1.AgentMessage {
	return &agentv1.AgentMessage{Body: &agentv1.AgentMessage_ConfigApplied{
		ConfigApplied: &agentv1.ConfigApplied{ConfigVersion: v, Error: errText},
	}}
}

// maybeSendBatch sends the outstanding batch or builds a new one when enough results wait or the flush interval passed.
// One batch is in flight at a time.
func (s *session) maybeSendBatch() error {
	if !s.acked {
		return nil
	}
	a := s.a
	now := a.opts.Now()
	if s.inflight != 0 {
		if now.Sub(s.inflightSentAt) < a.opts.BatchAckTimeout {
			return nil
		}
		a.logger.Warn("no acknowledgement for a check result batch; sending it again", "sequence", s.inflight)
		s.inflight = 0
	}
	outstanding, err := a.buffer.Outstanding()
	if err != nil {
		a.logger.Error("could not read the outstanding batch", "error", err)
	}
	if outstanding == nil {
		pending := a.buffer.Pending()
		if pending == 0 {
			return nil
		}
		if pending < a.opts.BatchMinResults && now.Sub(s.lastFlush) < a.opts.BatchFlushInterval {
			return nil
		}
	}
	batch, err := a.buffer.NextBatch(a.opts.BatchMaxResults)
	if err != nil {
		a.logger.Error("could not build a check result batch", "error", err)
		return nil
	}
	if batch == nil {
		return nil
	}
	if err := s.send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_CheckResults{CheckResults: batch}}); err != nil {
		return err
	}
	s.inflight = batch.GetSequence()
	s.inflightSentAt = now
	s.lastFlush = now
	a.logger.Debug("sent check result batch", "sequence", batch.GetSequence(), "results", len(batch.GetResults()))
	return nil
}

func (s *session) handleBatchAck(sequence uint64) error {
	a := s.a
	removed, err := a.buffer.Ack(sequence)
	if err != nil {
		a.logger.Error("could not remove an acknowledged batch", "sequence", sequence, "error", err)
		return nil
	}
	if !removed {
		a.logger.Debug("acknowledgement for an unknown batch ignored", "sequence", sequence)
	}
	if sequence != s.inflight {
		return nil
	}
	s.inflight = 0
	return s.maybeSendBatch()
}

func (s *session) handleDisconnect(d *agentv1.Disconnect) (sessionOutcome, bool, error) {
	a := s.a
	reason := d.GetReason()
	switch d.GetCode() {
	case agentv1.DisconnectCode_DISCONNECT_CODE_REVOKED:
		a.logger.Error("the gateway revoked this agent; it stops connecting until it is enrolled again", "reason", reason)
		st, err := a.store.Update(func(st *state.State) error {
			st.Revoked = true
			st.RevokedReason = reason
			st.RevokedAt = a.opts.Now().UTC()
			return nil
		})
		if err != nil {
			a.logger.Error("could not record the revocation in the state file", "error", err)
		} else {
			a.mu.Lock()
			a.st = st
			a.mu.Unlock()
		}
		_ = s.conn.Close(websocket.StatusNormalClosure, "revoked")
		return outcomeRevoked, true, nil
	case agentv1.DisconnectCode_DISCONNECT_CODE_SERVER_SHUTDOWN:
		a.logger.Info("the gateway is shutting down; reconnecting later", "reason", reason)
		return s.outcome(), true, nil
	case agentv1.DisconnectCode_DISCONNECT_CODE_DUPLICATE_IDENTITY:
		a.logger.Error("the gateway reports another live connection with this agent's certificate; reconnecting later", "reason", reason)
		return outcomeFailed, true, errors.New("duplicate agent identity")
	default:
		a.logger.Warn("the gateway closed the session", "code", d.GetCode().String(), "reason", reason)
		return outcomeFailed, true, fmt.Errorf("disconnected by the gateway: %s", d.GetCode())
	}
}

// maybeRenew requests a new certificate for the same key once two thirds of the lifetime has passed.
func (s *session) maybeRenew() error {
	if !s.acked {
		return nil
	}
	a := s.a
	now := a.opts.Now()
	if s.renewalPending {
		if now.Sub(s.renewalSentAt) < renewalWait {
			return nil
		}
		s.renewalPending = false
		a.nextRenewal = now.Add(a.opts.RenewalRetry)
		a.logger.Warn("no answer to the certificate renewal request; retrying later", "retryAt", a.nextRenewal.UTC().Format(time.RFC3339))
		return nil
	}
	if now.Before(a.nextRenewal) {
		return nil
	}
	st := a.State()
	cert, err := st.Certificate()
	if err != nil {
		return err
	}
	lifetime := cert.NotAfter.Sub(cert.NotBefore)
	if now.Before(cert.NotBefore.Add(lifetime * 2 / 3)) {
		return nil
	}
	csr, err := keystore.CreateCSR(rand.Reader, a.key, inventory.Hostname())
	if err != nil {
		a.nextRenewal = now.Add(a.opts.RenewalRetry)
		return fmt.Errorf("certificate renewal: %w", err)
	}
	if err := s.send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_RenewCertificate{
		RenewCertificate: &agentv1.RenewCertificateRequest{CsrDer: csr},
	}}); err != nil {
		return err
	}
	s.renewalPending = true
	s.renewalSentAt = now
	a.logger.Info("requested certificate renewal", "expires", cert.NotAfter.UTC().Format(time.RFC3339))
	return nil
}

// handleRenewal validates and stores a renewed certificate. It returns true when the session must reconnect.
func (s *session) handleRenewal(resp *agentv1.RenewCertificateResponse) bool {
	a := s.a
	now := a.opts.Now()
	s.renewalPending = false
	retry := func(msg string, args ...any) bool {
		a.nextRenewal = now.Add(a.opts.RenewalRetry)
		a.logger.Warn(msg, append(args, "retryAt", a.nextRenewal.UTC().Format(time.RFC3339))...)
		return false
	}
	if resp.GetError() != "" {
		return retry("the gateway refused the certificate renewal; keeping the current certificate", "reason", resp.GetError())
	}
	st := a.State()
	ca, err := st.CACertificate()
	if err != nil {
		return retry("certificate renewal: pinned CA unreadable", "error", err)
	}
	current, err := st.Certificate()
	if err != nil {
		return retry("certificate renewal: current certificate unreadable", "error", err)
	}
	cert, err := enroll.ValidateAgentCertificate(resp.GetCertificateDer(), ca, a.key, st.EndpointID, now)
	if err != nil {
		return retry("the renewed certificate is invalid; keeping the current certificate", "error", err)
	}
	if !cert.NotAfter.After(current.NotAfter) {
		return retry("the renewed certificate does not extend the lifetime; keeping the current certificate")
	}
	updated, err := a.store.Update(func(st *state.State) error {
		st.CertificatePEM = state.EncodeCertificatePEM(cert.Raw)
		st.CertificateRenewedAt = now.UTC()
		return nil
	})
	if err != nil {
		return retry("could not store the renewed certificate; keeping the current certificate", "error", err)
	}
	a.mu.Lock()
	a.st = updated
	a.mu.Unlock()
	a.nextRenewal = time.Time{}
	a.logger.Info("certificate renewed", "expires", cert.NotAfter.UTC().Format(time.RFC3339))
	return true
}

func (s *session) startInventory(force bool) {
	s.inventoryForce = s.inventoryForce || force
	if s.inventoryRunning {
		return
	}
	s.inventoryRunning = true
	a := s.a
	go func() {
		var inv *agentv1.Inventory
		defer func() {
			safego.Recover(a.logger, "inventory")
			select {
			case s.inventoryReady <- inv:
			case <-s.ctx.Done():
			}
		}()
		inv = a.opts.Inventory(s.ctx)
	}()
}

func (s *session) inventoryCollected(inv *agentv1.Inventory) error {
	a := s.a
	s.inventoryRunning = false
	force := s.inventoryForce
	s.inventoryForce = false
	if inv == nil {
		return errors.New("inventory collection failed")
	}
	inventory.Normalize(inv)
	var report *agentv1.InventoryReport
	for trial := 0; ; trial++ {
		hash, err := inventory.Hash(inv)
		if err != nil {
			return err
		}
		report = &agentv1.InventoryReport{Hash: hash, Inventory: inv}
		// Leave room for the envelope.
		if proto.Size(report) < MaxMessageBytes-1024 || trial >= maxInventoryTrials || len(inv.Software) == 0 {
			break
		}
		a.logger.Warn("inventory exceeds the message limit; reporting fewer software items", "software", len(inv.Software))
		inv.Software = inv.Software[:len(inv.Software)/2]
	}
	a.mu.Lock()
	last := a.st.InventoryHash
	a.mu.Unlock()
	if !force && report.GetHash() == last {
		return nil
	}
	if err := s.send(&agentv1.AgentMessage{Body: &agentv1.AgentMessage_Inventory{Inventory: report}}); err != nil {
		return err
	}
	st, err := a.store.Update(func(st *state.State) error {
		st.InventoryHash = report.GetHash()
		return nil
	})
	if err != nil {
		a.logger.Warn("could not store the inventory hash", "error", err)
		return nil
	}
	a.mu.Lock()
	a.st = st
	a.mu.Unlock()
	a.logger.Info("inventory reported", "hash", report.GetHash()[:16], "software", len(inv.GetSoftware()))
	return nil
}
