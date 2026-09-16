// Package remote serves remote sessions (0.3.0, ARCHITECTURE.md §4 Remote session): it verifies the session token fleeto-signer
// issued, performs the key exchange with the browser, encrypts every frame between browser and endpoint, and runs what the session
// offers. The gateway in between relays ciphertext only.
//
// Trust is anchored outside the relay: the token carries the browser's ephemeral key and is signed with the instance signing key the
// endpoint pinned at enrollment; the endpoint signs its own ephemeral key with its certificate key, which the browser learns from the
// instance web UI. A hostile relay can drop a session but cannot read it, change it or take part in it.
package remote

import (
	"crypto/ed25519"
	"crypto/sha256"
	"errors"
	"fmt"
	"strings"
	"sync"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

const (
	// TokenContext is the domain separation prefix of session token signatures; it must equal SignatureContexts.RemoteSession.
	TokenContext = "fleeto-remote-session-v1"
	// MaxTokenBytes bounds the token payload the endpoint parses.
	MaxTokenBytes = 16 * 1024
	// ClockTolerance is how far past valid_until a token is still accepted; the gateway already accepted it on its own clock.
	ClockTolerance = 5 * time.Minute
	// maxTokenLifetime is the longest validity a token may name.
	maxTokenLifetime = 2 * time.Minute
	// MinIdleTimeout and MaxIdleTimeout bound the idle timeout a token may set.
	MinIdleTimeout = 5 * time.Minute
	MaxIdleTimeout = 8 * time.Hour
	// DefaultIdleTimeout applies when a token names none.
	DefaultIdleTimeout = 30 * time.Minute
)

// Token is a verified session token.
type Token struct {
	*agentv1.RemoteSessionToken
	// PayloadHash is the SHA-256 of the exact signed payload. The key exchange binds it.
	PayloadHash [32]byte
}

// IdleTimeout is the idle timeout of the token, held inside the bounds.
func (t *Token) IdleTimeout() time.Duration {
	d := time.Duration(t.GetIdleTimeoutSeconds()) * time.Second
	if d <= 0 {
		return DefaultIdleTimeout
	}
	return min(max(d, MinIdleTimeout), MaxIdleTimeout)
}

// VerifyToken checks a signed token against the pinned trust: the signature, the instance and endpoint, the service it is meant for,
// its validity and the browser key. Whether the endpoint is managed and whether the participant was seen before are checked by the
// caller (Replay).
func VerifyToken(signed *agentv1.SignedRemoteSession, trust signedconfig.Trust, component agentv1.Component, now time.Time) (*Token, error) {
	if signed == nil || len(signed.GetPayload()) == 0 || len(signed.GetPayload()) > MaxTokenBytes {
		return nil, errors.New("the session token cannot be read")
	}
	if len(trust.SigningKey) != ed25519.PublicKeySize || trust.KeyID == "" || trust.InstanceID == "" || trust.EndpointID == "" {
		return nil, errors.New("the endpoint has no pinned instance signing key: enroll the agent again")
	}
	if signed.GetKeyId() != trust.KeyID {
		return nil, fmt.Errorf("the session token is signed with key %q, but the pinned instance signing key is %q", signed.GetKeyId(), trust.KeyID)
	}
	if len(signed.GetSignature()) != ed25519.SignatureSize {
		return nil, errors.New("the session token signature is invalid")
	}
	message := make([]byte, 0, len(TokenContext)+1+len(signed.GetPayload()))
	message = append(message, TokenContext...)
	message = append(message, 0)
	message = append(message, signed.GetPayload()...)
	if !ed25519.Verify(trust.SigningKey, message, signed.GetSignature()) {
		return nil, errors.New("the session token signature does not verify against the pinned instance signing key")
	}

	var token agentv1.RemoteSessionToken
	if err := (proto.UnmarshalOptions{DiscardUnknown: true}).Unmarshal(signed.GetPayload(), &token); err != nil {
		return nil, fmt.Errorf("the session token cannot be parsed: %w", err)
	}
	if !ValidID(token.GetParticipantId()) || !ValidID(token.GetSessionId()) {
		return nil, errors.New("the session token has no valid participant or session id")
	}
	if !strings.EqualFold(token.GetInstanceId(), trust.InstanceID) {
		return nil, fmt.Errorf("the session token is for instance %q, not this endpoint's instance", token.GetInstanceId())
	}
	if !strings.EqualFold(token.GetEndpointId(), trust.EndpointID) {
		return nil, fmt.Errorf("the session token is for endpoint %q, not this endpoint", token.GetEndpointId())
	}
	if token.GetComponent() != component {
		return nil, fmt.Errorf("the session token is for the %s, not the %s", componentName(token.GetComponent()), componentName(component))
	}
	switch {
	case component == agentv1.Component_COMPONENT_WATCHDOG && token.GetKind() == agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_REMOTE_BACKGROUND:
	default:
		return nil, errors.New("this service does not serve this kind of remote session; update the agent")
	}
	if token.GetValidUntil() == nil || token.GetIssuedAt() == nil {
		return nil, errors.New("the session token has no validity")
	}
	validUntil := token.GetValidUntil().AsTime()
	if now.After(validUntil.Add(ClockTolerance)) {
		return nil, errors.New("the session token expired before the endpoint received it")
	}
	if validUntil.Sub(token.GetIssuedAt().AsTime()) > maxTokenLifetime || validUntil.Before(token.GetIssuedAt().AsTime()) {
		return nil, errors.New("the session token names a validity longer than allowed")
	}
	if validUntil.After(now.Add(maxTokenLifetime + ClockTolerance)) {
		return nil, errors.New("the session token is not valid yet by this endpoint's clock")
	}
	if !ValidPublicKey(token.GetBrowserPublicKey()) {
		return nil, errors.New("the session token carries an invalid browser key")
	}
	return &Token{RemoteSessionToken: &token, PayloadHash: sha256.Sum256(signed.GetPayload())}, nil
}

// Replay remembers the participant ids this service accepted, until long after their tokens expired, so a token opens one relay only.
// The gateway enforces single use in its database; this is the endpoint's own check.
type Replay struct {
	mu   sync.Mutex
	seen map[string]time.Time
}

// NewReplay returns an empty replay memory.
func NewReplay() *Replay { return &Replay{seen: map[string]time.Time{}} }

// Accept records the participant id and reports whether it was new.
func (r *Replay) Accept(participantID string, validUntil, now time.Time) bool {
	r.mu.Lock()
	defer r.mu.Unlock()
	for id, until := range r.seen {
		if now.After(until) {
			delete(r.seen, id)
		}
	}
	key := strings.ToLower(participantID)
	if _, ok := r.seen[key]; ok {
		return false
	}
	r.seen[key] = validUntil.Add(2*ClockTolerance + maxTokenLifetime)
	return true
}

// ManagedConfig reports whether a stored signed configuration (a serialized SignedConfig, as the agent keeps it in its state) verifies
// against the trust and is managed. Tier enforcement, layer 4: the endpoint serves remote sessions only while its own verified
// configuration says managed.
func ManagedConfig(storedSignedConfig []byte, trust signedconfig.Trust) bool {
	if len(storedSignedConfig) == 0 {
		return false
	}
	var sc agentv1.SignedConfig
	if err := proto.Unmarshal(storedSignedConfig, &sc); err != nil {
		return false
	}
	cfg, err := signedconfig.VerifySignature(&sc, trust)
	return err == nil && cfg.GetTier() == agentv1.Tier_TIER_MANAGED
}

// ValidID accepts a canonical GUID only.
func ValidID(id string) bool {
	if len(id) != 36 {
		return false
	}
	for i, r := range id {
		switch i {
		case 8, 13, 18, 23:
			if r != '-' {
				return false
			}
		default:
			if !strings.ContainsRune("0123456789abcdefABCDEF", r) {
				return false
			}
		}
	}
	return true
}

func componentName(c agentv1.Component) string {
	if c == agentv1.Component_COMPONENT_WATCHDOG {
		return "watchdog"
	}
	return "agent"
}
