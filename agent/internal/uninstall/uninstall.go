// Package uninstall carries out the uninstall command the instance sends when an endpoint was deleted with "Also uninstall the
// Fleeto agent" (0.6.0).
//
// The command is signed by fleeto-signer with the instance signing key pinned at enrollment, so a gateway or anyone between can
// neither forge nor redirect it: it is accepted only when the signature verifies, it names this instance and endpoint and it is
// still valid. It can do one thing only: start 'fleeto-agent uninstall' in a process of its own, which removes the watchdog and
// the agent with their keys and files and outlives the services it stops.
package uninstall

import (
	"crypto/ed25519"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"strings"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
)

const (
	// Context is the domain separation prefix; it must equal SignatureContexts.Uninstall on the server.
	Context = "fleeto-uninstall-v1"
	// MaxPayloadBytes bounds what the agent parses; a command is a few ids and a name.
	MaxPayloadBytes = 4 * 1024
	// ClockTolerance is how far past valid_until a command is still accepted.
	ClockTolerance = 5 * time.Minute
	// MaxLifetime is the longest validity a command may name: 30 days of waiting and a day of margin.
	MaxLifetime = 31 * 24 * time.Hour
)

// Verify checks a signed uninstall command against the pinned trust and returns it.
func Verify(signed *agentv1.SignedUninstall, trust signedconfig.Trust, now time.Time) (*agentv1.UninstallCommand, error) {
	if signed == nil || len(signed.GetPayload()) == 0 || len(signed.GetPayload()) > MaxPayloadBytes {
		return nil, errors.New("the uninstall command cannot be read")
	}
	if len(trust.SigningKey) != ed25519.PublicKeySize || trust.KeyID == "" || trust.InstanceID == "" || trust.EndpointID == "" {
		return nil, errors.New("there is no pinned instance signing key to check the uninstall command against")
	}
	if signed.GetKeyId() != trust.KeyID {
		return nil, fmt.Errorf("the uninstall command is signed with key %q, but the pinned instance signing key is %q", signed.GetKeyId(), trust.KeyID)
	}
	if len(signed.GetSignature()) != ed25519.SignatureSize {
		return nil, errors.New("the uninstall command signature is invalid")
	}
	message := make([]byte, 0, len(Context)+1+len(signed.GetPayload()))
	message = append(message, Context...)
	message = append(message, 0)
	message = append(message, signed.GetPayload()...)
	if !ed25519.Verify(trust.SigningKey, message, signed.GetSignature()) {
		return nil, errors.New("the uninstall command signature does not verify against the pinned instance signing key")
	}

	var command agentv1.UninstallCommand
	if err := (proto.UnmarshalOptions{DiscardUnknown: true}).Unmarshal(signed.GetPayload(), &command); err != nil {
		return nil, fmt.Errorf("the uninstall command cannot be parsed: %w", err)
	}
	if command.GetUninstallId() == "" {
		return nil, errors.New("the uninstall command has no id")
	}
	if !strings.EqualFold(command.GetInstanceId(), trust.InstanceID) {
		return nil, fmt.Errorf("the uninstall command is for instance %q, not this endpoint's instance", command.GetInstanceId())
	}
	if !strings.EqualFold(command.GetEndpointId(), trust.EndpointID) {
		return nil, fmt.Errorf("the uninstall command is for endpoint %q, not this endpoint", command.GetEndpointId())
	}
	if command.GetValidUntil() == nil || now.After(command.GetValidUntil().AsTime().Add(ClockTolerance)) {
		return nil, errors.New("the uninstall command has expired")
	}
	if command.GetValidUntil().AsTime().After(now.Add(MaxLifetime + ClockTolerance)) {
		return nil, errors.New("the uninstall command is valid for longer than 31 days")
	}
	return &command, nil
}

// Running reports whether an uninstall already runs on this endpoint: the agent and the watchdog may both receive the command.
func Running() bool {
	_, err := os.Stat(filepath.Join(platform.DataDir(), update.UninstallMarkerName))
	return err == nil
}

// Start runs '<agentExe> uninstall' in a process of its own that survives the services it stops, unless one runs already.
func Start(agentExe string) error {
	if Running() {
		return nil
	}
	if _, err := os.Stat(agentExe); err != nil {
		return fmt.Errorf("the agent program %s is not there: %w", agentExe, err)
	}
	return startDetached(agentExe)
}
