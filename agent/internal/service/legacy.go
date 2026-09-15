package service

import (
	"bytes"
	"crypto/sha256"
	"errors"
	"fmt"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/enroll"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

// Names of an agent installed before the internal rename from Fleetify to Fleeto (0.2.1). An install finds such an agent and takes
// over its enrollment (MD-Files/ARCHITECTURE.md §7, Rename to Fleeto); nothing new is ever written under these names.
const (
	LegacyServiceName         = "fleetify-agent"
	LegacyWatchdogServiceName = "fleetify-watchdog"
	LegacyWatchdogKeyName     = "Fleetify Watchdog Identity"
	legacyDirName             = "Fleetify"
)

// errLegacyOtherInstance: the legacy agent belongs to another instance or cannot be taken over.
var errLegacyOtherInstance = errors.New("legacy agent cannot be taken over")

// legacyTakeover decides whether the enrollment of a legacy agent can be taken over by this install: only for the same gateway and the
// same pinned instance CA as the install command, and never for a revoked agent. The install token is then not used, so the endpoint
// keeps its identity and history.
func legacyTakeover(legacy *state.State, opts InstallOptions) error {
	if legacy.Revoked {
		return fmt.Errorf("%w: it was revoked; run 'fleetify-agent uninstall' first and install again", errLegacyOtherInstance)
	}
	if !strings.EqualFold(strings.TrimSpace(legacy.Server), strings.TrimSpace(opts.Server)) {
		return fmt.Errorf("%w: it is enrolled with %s, not %s; run 'fleetify-agent uninstall' first to move this endpoint",
			errLegacyOtherInstance, legacy.Server, opts.Server)
	}
	want, err := enroll.ParseFingerprint(opts.CAFingerprint)
	if err != nil {
		return err
	}
	ca, err := legacy.CACertificate()
	if err != nil {
		return fmt.Errorf("%w: its state is damaged (%v); run 'fleetify-agent uninstall' first", errLegacyOtherInstance, err)
	}
	if sum := sha256.Sum256(ca.Raw); !bytes.Equal(sum[:], want) {
		return fmt.Errorf("%w: it trusts another instance CA; run 'fleetify-agent uninstall' first to move this endpoint", errLegacyOtherInstance)
	}
	return nil
}
