//go:build windows

package service

import (
	"context"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"time"

	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"

	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
)

// legacyDataDir is C:\ProgramData\Fleetify, where an agent from before the rename kept its state.
func legacyDataDir() string {
	return filepath.Join(filepath.Dir(platform.DataDir()), legacyDirName)
}

// legacyProgramDir is C:\Program Files\Fleetify\Agent.
func legacyProgramDir() string {
	return filepath.Join(filepath.Dir(filepath.Dir(platform.ProgramDir())), legacyDirName, "Agent")
}

// takeOverLegacyAgent replaces an agent installed before the rename to Fleeto (0.2.1) by this one without enrolling again: the state
// directory with the certificate, the pinned trust and the buffered results moves to the Fleeto location, the identity key stays where
// it is (the state names it), and the Fleeto Agent service replaces the legacy service. Until the new service runs, every step is
// undone on failure and the legacy agent is started again. Returns false when no legacy agent is installed.
func takeOverLegacyAgent(ctx context.Context, m *mgr.Mgr, opts InstallOptions, out io.Writer) (taken bool, err error) {
	legacyStateDir := filepath.Join(legacyDataDir(), "Agent")
	legacyService, serviceErr := m.OpenService(LegacyServiceName)
	if serviceErr == nil {
		defer legacyService.Close()
	}
	legacy, loadErr := state.NewStore(legacyStateDir, platform.AccessSystem).Load()
	if serviceErr != nil && loadErr != nil {
		return false, nil
	}
	if loadErr != nil {
		return false, fmt.Errorf("a Fleetify agent is installed but its state in %s cannot be read (%v); run 'fleetify-agent uninstall' first", legacyStateDir, loadErr)
	}
	if err := legacyTakeover(legacy, opts); err != nil {
		return false, fmt.Errorf("a Fleetify agent is installed on this endpoint, but this install cannot take it over: %w", err)
	}
	stateDir := platform.DefaultStateDir()
	if exists(stateDir) {
		if err := removeIfEmpty(stateDir); err != nil || exists(stateDir) {
			return false, fmt.Errorf("both a Fleetify agent and %s exist; run 'fleetify-agent uninstall' and 'fleeto-agent uninstall', then install again", stateDir)
		}
	}
	fmt.Fprintf(out, "Found the Fleetify agent of endpoint %s: taking it over as the Fleeto Agent, without enrolling again\n", legacy.EndpointID)

	// The legacy services must not restart or reinstall each other while they are replaced.
	marker := filepath.Join(legacyDataDir(), update.UninstallMarkerName)
	_ = os.WriteFile(marker, []byte(time.Now().UTC().Format(time.RFC3339)), 0o600)
	defer os.Remove(marker)

	var rollback []func()
	defer func() {
		if err != nil {
			for i := len(rollback) - 1; i >= 0; i-- {
				rollback[i]()
			}
		}
	}()

	// 1. The legacy agent stops; on failure it is started again.
	if serviceErr == nil {
		if status, qerr := legacyService.Query(); qerr == nil && status.State != svc.Stopped {
			if _, err := legacyService.Control(svc.Stop); err != nil {
				return false, fmt.Errorf("stop the Fleetify agent service: %w", err)
			}
			if err := waitForState(legacyService, svc.Stopped, 60*time.Second); err != nil {
				return false, err
			}
		}
		rollback = append(rollback, func() {
			_ = os.Remove(marker)
			_ = legacyService.Start()
			fmt.Fprintln(out, "The Fleetify agent was started again; nothing was changed.")
		})
	}

	// 2. The binary.
	programDir := platform.ProgramDir()
	exePath := filepath.Join(programDir, platform.BinaryName)
	programDirExisted := exists(programDir)
	if err := installBinary(exePath); err != nil {
		return false, err
	}
	rollback = append(rollback, func() {
		if !programDirExisted {
			_ = os.RemoveAll(programDir)
		}
	})

	// 3. The state directory moves (same volume, so its protection moves with it).
	if err := os.MkdirAll(filepath.Dir(stateDir), 0o755); err != nil {
		return false, fmt.Errorf("create %s: %w", filepath.Dir(stateDir), err)
	}
	if err := os.Rename(legacyStateDir, stateDir); err != nil {
		return false, fmt.Errorf("move %s to %s: %w", legacyStateDir, stateDir, err)
	}
	rollback = append(rollback, func() { _ = os.Rename(stateDir, legacyStateDir) })
	fmt.Fprintf(out, "Moved the agent state to %s\n", stateDir)

	// 4. The Fleeto Agent service.
	s, err := createAndStartService(m, exePath, &rollback)
	if err != nil {
		return false, err
	}
	s.Close()
	fmt.Fprintf(out, "The %s service is running as endpoint %s.\n", DisplayName, legacy.EndpointID)

	// 5. From here the new agent runs: removing what is left of the legacy agent is best effort. A legacy watchdog has its own
	// certificate for this endpoint; the Fleeto Agent installs a new watchdog with the next update offer.
	var cleanup []error
	if err := svcctl.Delete(ctx, LegacyWatchdogServiceName, time.Minute); err != nil {
		cleanup = append(cleanup, err)
	}
	legacyWatchdogDir := filepath.Join(legacyDataDir(), "Watchdog")
	watchdogKey := state.KeyRef{Kind: keystore.KindCNG, Name: LegacyWatchdogKeyName, Machine: true}
	if st, err := state.NewStore(legacyWatchdogDir, platform.AccessSystem).Load(); err == nil {
		watchdogKey = st.Key
	}
	_ = keystore.Delete(legacyWatchdogDir, watchdogKey)
	if err := os.RemoveAll(legacyWatchdogDir); err != nil {
		cleanup = append(cleanup, err)
	}
	if serviceErr == nil {
		if err := legacyService.Delete(); err != nil {
			cleanup = append(cleanup, fmt.Errorf("delete the Fleetify agent service: %w", err))
		}
	}
	if err := removeProgramDir(legacyProgramDir(), out); err != nil {
		cleanup = append(cleanup, err)
	}
	_ = removeIfEmpty(filepath.Dir(legacyProgramDir()))
	_ = os.Remove(marker)
	_ = removeIfEmpty(legacyDataDir())
	if err := errors.Join(cleanup...); err != nil {
		fmt.Fprintf(out, "The Fleeto Agent runs, but parts of the Fleetify agent could not be removed: %v\n", err)
	}
	return true, nil
}
