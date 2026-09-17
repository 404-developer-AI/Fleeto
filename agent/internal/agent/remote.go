package agent

import (
	"context"
	"crypto/tls"
	"runtime"

	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/remote"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/screen"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
)

// Remote control sessions (0.3.0 step 3) are served by the agent, because showing and using the screen needs the agent's helper in the
// Windows session; remote background stays with the watchdog. Same token, relay and encryption as remote background (internal/remote).

// newRemoteServer prepares the remote control server of the agent. Where the platform does not serve remote control, the server
// refuses every offer with the reason.
func (a *Agent) newRemoteServer() *remote.Server {
	server := &remote.Server{
		Component: agentv1.Component_COMPONENT_AGENT,
		Trust: func() (signedconfig.Trust, error) {
			a.mu.Lock()
			defer a.mu.Unlock()
			return a.trust, nil
		},
		Managed: func() bool {
			a.mu.Lock()
			defer a.mu.Unlock()
			return a.config.GetTier() == agentv1.Tier_TIER_MANAGED
		},
		Signer: a.key,
		CertificatePublicKey: func() ([]byte, error) {
			st := a.State()
			cert, err := st.Certificate()
			if err != nil {
				return nil, err
			}
			return cert.RawSubjectPublicKeyInfo, nil
		},
		Dial: remote.WebSocketDialer(func() (string, *tls.Config, error) {
			st := a.State()
			conf, _, err := a.tlsConfig(&st)
			return st.Server, conf, err
		}),
		Hello: func() remote.Hello {
			return remote.Hello{Hostname: inventory.Hostname(), Platform: runtime.GOOS, Version: version.Version}
		},
		Logger: a.logger,
		Now:    a.opts.Now,
	}
	if screen.Supported() {
		launch := a.opts.ScreenLauncher
		if launch == nil {
			launch = screen.WindowsLauncher(a.logger)
		}
		server.Screen = func(token *remote.Token, send func([]byte) error) remote.ScreenHandler {
			a.logger.Info("remote control session", "participant", token.GetParticipantId(), "technician", token.GetTechnicianName(),
				"windowsSession", token.GetWindowsSessionId())
			return screen.NewController(screen.ControllerOptions{
				Send: send, Launch: launch, Session: token.GetWindowsSessionId(), ConsoleSession: screen.ConsoleSession,
				SecureAttention: screen.SecureAttention, Logger: a.logger, Now: a.opts.Now,
			})
		}
	}
	return server
}

// remoteSession starts a remote control session for an offer, or reports why it was refused. It runs with the agent's own context, so
// a session outlives a reconnect of the control connection.
func (a *Agent) remoteSession(offer *agentv1.RemoteSessionOffer) {
	a.mu.Lock()
	ctx := a.runCtx
	a.mu.Unlock()
	if ctx == nil {
		ctx = context.Background()
	}
	participantID, refusal := a.remote.Offer(ctx, offer)
	if refusal == "" {
		return
	}
	if len(refusal) > 500 {
		refusal = refusal[:500]
	}
	select {
	case a.outbox <- &agentv1.AgentMessage{Body: &agentv1.AgentMessage_RemoteSessionRefused{
		RemoteSessionRefused: &agentv1.RemoteSessionRefused{ParticipantId: participantID, Error: refusal},
	}}:
	default:
		a.logger.Warn("could not report a refused remote session; the outbox is full", "participant", participantID)
	}
}

func (a *Agent) acceptRemoteOffer(offer *agentv1.RemoteSessionOffer) {
	safego.Go(a.logger, "remote session offer", func() { a.remoteSession(offer) })
}
