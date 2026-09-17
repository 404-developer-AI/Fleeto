package remote

import (
	"context"
	"crypto"
	"crypto/tls"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"sync/atomic"
	"time"

	"github.com/coder/websocket"
	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

const (
	// RelayPath is the gateway path the endpoint connects its side of a session to, followed by the participant id.
	RelayPath = "/v1/relay/"
	// MaxSessions is how many remote sessions one service serves at a time; the gateway holds the same limit.
	MaxSessions = 8
	dialTimeout = 20 * time.Second
)

// Server accepts remote session offers for one service on the endpoint.
type Server struct {
	// Component is the service this server runs in.
	Component agentv1.Component
	// Trust returns the pinned trust of the endpoint.
	Trust func() (signedconfig.Trust, error)
	// Managed reports whether the endpoint's own verified signed configuration is managed.
	Managed func() bool
	// Signer is the certificate key of the service.
	Signer crypto.Signer
	// CertificatePublicKey returns the SubjectPublicKeyInfo of the service's current certificate, as the certificate carries it.
	CertificatePublicKey func() ([]byte, error)
	// Dial opens the relay WebSocket for a participant.
	Dial func(ctx context.Context, participantID string) (Transport, error)
	// Hello describes what the endpoint offers.
	Hello func() Hello
	Open  TerminalOpener
	// Report records an action a technician took in a session, for the audit log (0.3.0 step 2); nil disables it.
	Report func(participantID, action, target, detail string)
	// Logger and Now are optional.
	Logger *slog.Logger
	Now    func() time.Time

	replay *Replay
	active atomic.Int32
}

// Offer handles one offer from the gateway. It returns an empty string when the session started, otherwise the reason it was refused,
// which the service reports to the gateway so the technician sees it.
func (s *Server) Offer(ctx context.Context, offer *agentv1.RemoteSessionOffer) (participantID, refusal string) {
	now := time.Now
	if s.Now != nil {
		now = s.Now
	}
	logger := s.Logger
	if logger == nil {
		logger = slog.New(slog.DiscardHandler)
	}
	if s.replay == nil {
		s.replay = NewReplay()
	}
	participantID = peekParticipant(offer.GetSession())

	trust, err := s.Trust()
	if err != nil {
		return participantID, "The endpoint cannot read its enrollment: " + err.Error()
	}
	token, err := VerifyToken(offer.GetSession(), trust, s.Component, now())
	if err != nil {
		logger.Warn("refused a remote session", "error", err)
		return participantID, "The endpoint refused the session: " + err.Error()
	}
	participantID = token.GetParticipantId()
	// Tier enforcement, layer 4.
	if !s.Managed() {
		logger.Warn("refused a remote session: the endpoint is agent-only", "participant", participantID)
		return participantID, "The endpoint refused the session: its signed configuration is agent-only."
	}
	if !s.replay.Accept(participantID, token.GetValidUntil().AsTime(), now()) {
		logger.Warn("refused a remote session token that was used before", "participant", participantID)
		return participantID, "The endpoint refused the session: this session token was used before."
	}
	if s.active.Add(1) > MaxSessions {
		s.active.Add(-1)
		return participantID, fmt.Sprintf("The endpoint already serves %d remote sessions. Close one and try again.", MaxSessions)
	}

	handshake, err := NewEndpointHandshake(token, s.Signer)
	if err != nil {
		s.active.Add(-1)
		return participantID, "The endpoint could not start the session: " + err.Error()
	}
	certificateKey, err := s.CertificatePublicKey()
	if err != nil {
		s.active.Add(-1)
		return participantID, "The endpoint cannot read its certificate: " + err.Error()
	}
	dialCtx, cancel := context.WithTimeout(ctx, dialTimeout)
	transport, err := s.Dial(dialCtx, participantID)
	cancel()
	if err != nil {
		s.active.Add(-1)
		logger.Warn("could not connect the remote session relay", "participant", participantID, "error", err)
		return participantID, "The endpoint could not connect to the relay: " + err.Error()
	}
	hello, err := proto.Marshal(&agentv1.RelayEndpointHello{
		ParticipantId: participantID, EndpointPublicKey: handshake.PublicKey, Signature: handshake.Signature, CertificatePublicKey: certificateKey,
	})
	if err == nil {
		writeCtx, cancelWrite := context.WithTimeout(ctx, writeTimeout)
		err = transport.Write(writeCtx, hello)
		cancelWrite()
	}
	if err != nil {
		s.active.Add(-1)
		_ = transport.Close("handshake failed")
		return participantID, "The endpoint could not start the session on the relay: " + err.Error()
	}
	var report func(action, target, detail string)
	if s.Report != nil {
		report = func(action, target, detail string) { s.Report(participantID, action, target, detail) }
	}
	session, err := NewSession(SessionOptions{
		Token: token, Keys: handshake.Keys, Transport: transport, Hello: s.Hello(), Open: s.Open, Report: report, Logger: logger, Now: now,
	})
	if err != nil {
		s.active.Add(-1)
		_ = transport.Close("session failed")
		return participantID, "The endpoint could not start the session: " + err.Error()
	}
	logger.Info("remote session started", "participant", participantID, "session", token.GetSessionId(), "technician", token.GetTechnicianName())
	safego.Go(logger, "remote session", func() {
		defer s.active.Add(-1)
		reason := session.Run(ctx)
		logger.Info("remote session ended", "participant", participantID, "technician", token.GetTechnicianName(), "reason", reason)
	})
	return participantID, ""
}

// peekParticipant reads the participant id of an unverified token, only to address a refusal. Never used to decide anything.
func peekParticipant(signed *agentv1.SignedRemoteSession) string {
	if signed == nil || len(signed.GetPayload()) > MaxTokenBytes {
		return ""
	}
	var token agentv1.RemoteSessionToken
	if (proto.UnmarshalOptions{DiscardUnknown: true}).Unmarshal(signed.GetPayload(), &token) != nil || !ValidID(token.GetParticipantId()) {
		return ""
	}
	return token.GetParticipantId()
}

// WebSocketDialer returns a Dial function that opens wss://<server>/v1/relay/<participant> with the service's client certificate.
func WebSocketDialer(server func() (string, *tls.Config, error)) func(ctx context.Context, participantID string) (Transport, error) {
	return func(ctx context.Context, participantID string) (Transport, error) {
		host, tlsConfig, err := server()
		if err != nil {
			return nil, err
		}
		if !ValidID(participantID) {
			return nil, errors.New("invalid participant id")
		}
		transport := &http.Transport{Proxy: nil, TLSClientConfig: tlsConfig, TLSHandshakeTimeout: dialTimeout}
		conn, resp, err := websocket.Dial(ctx, "wss://"+host+RelayPath+participantID, &websocket.DialOptions{
			HTTPClient: &http.Client{Transport: transport},
		})
		if err != nil {
			transport.CloseIdleConnections()
			if resp != nil {
				return nil, fmt.Errorf("the gateway refused the relay (HTTP %d)", resp.StatusCode)
			}
			return nil, err
		}
		conn.SetReadLimit(MaxFrameBytes + 1024)
		return &wsTransport{conn: conn, closeIdle: transport.CloseIdleConnections}, nil
	}
}

type wsTransport struct {
	conn      *websocket.Conn
	closeIdle func()
}

func (t *wsTransport) Read(ctx context.Context) ([]byte, error) {
	for {
		typ, data, err := t.conn.Read(ctx)
		if err != nil {
			return nil, err
		}
		if typ == websocket.MessageBinary {
			return data, nil
		}
	}
}

func (t *wsTransport) Write(ctx context.Context, frame []byte) error {
	return t.conn.Write(ctx, websocket.MessageBinary, frame)
}

func (t *wsTransport) Close(reason string) error {
	if len(reason) > 120 {
		reason = reason[:120]
	}
	err := t.conn.Close(websocket.StatusNormalClosure, reason)
	t.closeIdle()
	return err
}
