package agent

import (
	"context"
	"crypto/tls"
	"path/filepath"
	"runtime"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/remote"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/screen"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
	"google.golang.org/protobuf/types/known/timestamppb"
)

// Remote control sessions (0.3.0 step 3) are served by the agent, because showing and using the screen needs the agent's helper in the
// Windows session; remote background stays with the watchdog. Same token, relay and encryption as remote background (internal/remote).
// Several technicians in one session share one helper (0.3.0 step 4, internal/screen.Sessions).

// remoteClipboardDir is the folder under the Fleeto data directory where files pasted into remote control sessions wait while their
// session runs.
const remoteClipboardDir = "RemoteClipboard"

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
		Report: a.reportRemoteAction,
		Logger: a.logger,
		Now:    a.opts.Now,
	}
	if screen.Supported() {
		launch := a.opts.ScreenLauncher
		if launch == nil {
			launch = screen.WindowsLauncher(a.logger)
		}
		clipboard := a.opts.ClipboardLauncher
		if clipboard == nil {
			clipboard = screen.UserLauncher(a.logger)
		}
		staging := filepath.Join(platform.DataDir(), remoteClipboardDir)
		if err := screen.CleanStaging(staging); err != nil {
			a.logger.Warn("could not delete files left from earlier remote control sessions", "folder", staging, "error", err)
		}
		sessions := screen.NewSessions(screen.SessionsOptions{
			Launch: launch, ClipboardLaunch: clipboard, ConsoleSession: screen.ConsoleSession, SessionExists: screen.SessionExists,
			SecureAttention: screen.SecureAttention, SessionUser: screen.SessionUser,
			Consent: screen.AskConsent, StagingRoot: staging, Stage: screen.StageFolder, Logger: a.logger, Now: a.opts.Now,
		})
		server.Screen = func(peer remote.ScreenPeer) remote.ScreenHandler {
			token := peer.Token
			a.logger.Info("remote control session", "participant", token.GetParticipantId(), "session", token.GetSessionId(),
				"technician", token.GetTechnicianName(), "windowsSession", token.GetWindowsSessionId(), "consent", token.GetConsentRequired(),
				"banner", token.GetBannerVisible(), "clipboard", token.GetClipboardEnabled())
			return sessions.Join(screen.JoinOptions{
				SessionID: token.GetSessionId(), ParticipantID: token.GetParticipantId(), Technician: token.GetTechnicianName(),
				WindowsSession: token.GetWindowsSessionId(), ConsentRequired: token.GetConsentRequired(),
				ConsentTimeout: consentTimeout(token.GetConsentTimeoutSeconds()), BannerVisible: token.GetBannerVisible(),
				ClipboardEnabled: token.GetClipboardEnabled(), Send: peer.Send, End: peer.End, Report: peer.Report,
			})
		}
	}
	return server
}

// consentTimeout holds the token's consent timeout inside the bounds of the policy (10 to 300 seconds, default 30).
func consentTimeout(seconds uint32) time.Duration {
	if seconds == 0 {
		return 30 * time.Second
	}
	return time.Duration(min(max(seconds, 10), 300)) * time.Second
}

// reportRemoteAction sends an action of a remote control session (clipboard files, the consent answer) to the gateway for the audit log.
func (a *Agent) reportRemoteAction(participantID, action, target, detail string) {
	report := &agentv1.RemoteSessionActionReport{
		ParticipantId: participantID, Action: action, Target: clipText(target, 1000), Detail: clipText(detail, 1000), Time: timestamppb.New(a.opts.Now()),
	}
	select {
	case a.outbox <- &agentv1.AgentMessage{Body: &agentv1.AgentMessage_RemoteSessionAction{RemoteSessionAction: report}}:
	default:
		a.logger.Warn("could not report a remote session action for the audit log; the outbox is full", "action", action)
	}
}

func clipText(s string, limit int) string {
	if len(s) > limit {
		return s[:limit]
	}
	return s
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
