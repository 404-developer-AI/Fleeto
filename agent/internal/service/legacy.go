package service

import (
	"bytes"
	"crypto/sha256"
	"errors"
	"fmt"
	"path/filepath"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/enroll"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
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

// loadLegacyState reads the state of a legacy agent from stateDir, below dataDir, only when both folders and the state file belong to
// the system. On Windows a local user may create folders in C:\ProgramData: a legacy state that user wrote would otherwise become the
// pinned trust (signing key, key and server) of the agent that takes it over (security audit of 0.9.0).
func loadLegacyState(dataDir, stateDir string) (*state.State, error) {
	for _, path := range []string{dataDir, stateDir, filepath.Join(stateDir, state.FileName)} {
		if err := platform.CheckSystemOwned(path); err != nil {
			return nil, err
		}
	}
	return state.NewStore(stateDir, platform.AccessSystem).Load()
}

// legacyTakeover decides whether the enrollment of a legacy agent can be taken over by this install: only for the same gateway and the
// same pinned instance CA as the install command, and never for a revoked agent. The install token is then not used, so the endpoint
// keeps its identity and history. A refusal ends with uninstall, the command that removes the legacy agent: its program folder is not
// on the PATH, so the command carries the full path and is last in the message, ready to copy.
func legacyTakeover(legacy *state.State, opts InstallOptions, uninstall string) error {
	if legacy.Revoked {
		return fmt.Errorf("%w: it was revoked. Uninstall it, then install again: %s", errLegacyOtherInstance, uninstall)
	}
	if !strings.EqualFold(strings.TrimSpace(legacy.Server), strings.TrimSpace(opts.Server)) {
		return fmt.Errorf("%w: it is enrolled with %s, not %s. To move this endpoint, uninstall it first: %s",
			errLegacyOtherInstance, legacy.Server, opts.Server, uninstall)
	}
	want, err := enroll.ParseFingerprint(opts.CAFingerprint)
	if err != nil {
		return err
	}
	ca, err := legacy.CACertificate()
	if err != nil {
		return fmt.Errorf("%w: its state is damaged (%v). Uninstall it first: %s", errLegacyOtherInstance, err, uninstall)
	}
	if sum := sha256.Sum256(ca.Raw); !bytes.Equal(sum[:], want) {
		return fmt.Errorf("%w: it trusts another instance CA. To move this endpoint, uninstall it first: %s", errLegacyOtherInstance, uninstall)
	}
	return nil
}
