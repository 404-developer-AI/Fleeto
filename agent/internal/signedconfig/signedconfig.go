// Package signedconfig verifies configurations signed with the instance signing key.
//
// This is the gate between the network and everything the agent does on the endpoint: a configuration is accepted
// only when the ed25519 signature over the exact payload bytes verifies against the key pinned at enrollment, it is
// addressed to this instance and endpoint, and it is newer than the applied one.
package signedconfig

import (
	"bytes"
	"crypto/ed25519"
	"errors"
	"fmt"
	"strings"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Context is the domain separation prefix; it must equal SignatureContexts.AgentConfig on the server.
const Context = "fleetify-agent-config-v1"

// MaxPayloadBytes bounds the payload the agent parses. A config is far smaller; the WebSocket limit is 4 MiB.
const MaxPayloadBytes = 1 << 20

// Trust is the pinned material a configuration is checked against.
type Trust struct {
	SigningKey ed25519.PublicKey
	KeyID      string
	InstanceID string
	EndpointID string
}

// ErrAlreadyApplied means the configuration is byte-for-byte the one already applied. It is not a refusal.
var ErrAlreadyApplied = errors.New("the configuration is already applied")

// Verify checks sc against trust and the applied version and returns the parsed configuration.
// appliedPayload is the payload of the currently applied configuration (nil when none); an identical payload
// yields ErrAlreadyApplied together with the parsed configuration, so a resend after a reconnect is harmless.
func Verify(sc *agentv1.SignedConfig, trust Trust, appliedVersion uint64, appliedPayload []byte) (*agentv1.AgentConfig, error) {
	cfg, err := VerifySignature(sc, trust)
	if err != nil {
		return nil, err
	}
	if appliedPayload != nil && cfg.GetVersion() == appliedVersion && bytes.Equal(sc.GetPayload(), appliedPayload) {
		return cfg, ErrAlreadyApplied
	}
	if cfg.GetVersion() <= appliedVersion {
		return nil, fmt.Errorf("configuration version %d is not newer than the applied version %d", cfg.GetVersion(), appliedVersion)
	}
	return cfg, nil
}

// VerifySignature checks signature, key id and addressing, without the version rule. It is used to re-verify the
// stored configuration at start.
func VerifySignature(sc *agentv1.SignedConfig, trust Trust) (*agentv1.AgentConfig, error) {
	if sc == nil {
		return nil, errors.New("the configuration message is empty")
	}
	if len(trust.SigningKey) != ed25519.PublicKeySize || trust.KeyID == "" || trust.InstanceID == "" || trust.EndpointID == "" {
		return nil, errors.New("the agent has no pinned instance signing key: enroll the agent again")
	}
	payload := sc.GetPayload()
	if len(payload) == 0 || len(payload) > MaxPayloadBytes {
		return nil, fmt.Errorf("the configuration payload size %d is invalid", len(payload))
	}
	// Key ids are public identifiers, so a plain comparison is fine; the signature check is what matters.
	if sc.GetKeyId() != trust.KeyID {
		return nil, fmt.Errorf("the configuration is signed with key %q, but the pinned instance signing key is %q", sc.GetKeyId(), trust.KeyID)
	}
	if len(sc.GetSignature()) != ed25519.SignatureSize {
		return nil, errors.New("the configuration signature is invalid")
	}
	message := make([]byte, 0, len(Context)+1+len(payload))
	message = append(message, Context...)
	message = append(message, 0)
	message = append(message, payload...)
	if !ed25519.Verify(trust.SigningKey, message, sc.GetSignature()) {
		return nil, errors.New("the configuration signature does not verify against the pinned instance signing key")
	}

	// Parse only after the signature verified, and from the exact signed bytes.
	var cfg agentv1.AgentConfig
	if err := (proto.UnmarshalOptions{DiscardUnknown: true}).Unmarshal(payload, &cfg); err != nil {
		return nil, fmt.Errorf("the signed configuration cannot be parsed: %w", err)
	}
	if !strings.EqualFold(cfg.GetInstanceId(), trust.InstanceID) {
		return nil, fmt.Errorf("the configuration is for instance %q, not this agent's instance", cfg.GetInstanceId())
	}
	if !strings.EqualFold(cfg.GetEndpointId(), trust.EndpointID) {
		return nil, fmt.Errorf("the configuration is for endpoint %q, not this endpoint", cfg.GetEndpointId())
	}
	if cfg.GetVersion() == 0 {
		return nil, errors.New("the configuration has no version")
	}
	return &cfg, nil
}

// EffectiveChecks returns the checks the agent may run. Only a managed configuration runs checks: agent-only and an
// unspecified tier run none, whatever the configuration contains, so a server bug cannot promote an endpoint.
func EffectiveChecks(cfg *agentv1.AgentConfig) []*agentv1.CheckSpec {
	if cfg == nil || cfg.GetTier() != agentv1.Tier_TIER_MANAGED {
		return nil
	}
	return cfg.GetChecks()
}
