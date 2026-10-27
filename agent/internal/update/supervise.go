package update

import (
	"context"
	"errors"
	"log/slog"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
)

// UninstallMarkerName is written in platform.DataDir while fleeto-agent uninstall runs: the agent and the watchdog then stop starting
// and installing each other.
const UninstallMarkerName = "uninstalling"

// UninstallInProgress reports whether an uninstall started less than 15 minutes ago.
func UninstallInProgress() bool {
	info, err := os.Stat(filepath.Join(platform.DataDir(), UninstallMarkerName))
	return err == nil && time.Since(info.ModTime()) < 15*time.Minute
}

// Supervisor keeps the other service running (the watchdog supervises the agent and the agent the watchdog) and describes its state
// for the heartbeat. A disabled start type is respected: an administrator stopped it on purpose.
type Supervisor struct {
	Controller svcctl.Controller
	Service    string
	// Exe is the binary of the supervised service, to report its version.
	Exe    string
	Logger *slog.Logger
	Now    func() time.Time
	// Probe returns the version of a binary; default ProbeVersion.
	Probe func(ctx context.Context, exe string) (string, error)

	mu        sync.Mutex
	paused    bool
	failures  int
	nextStart time.Time
	lastError string
	version   string
	versionAt time.Time
}

// Pause stops starting the service, while its binary is replaced.
func (s *Supervisor) Pause(paused bool) {
	s.mu.Lock()
	defer s.mu.Unlock()
	s.paused = paused
}

// restorePrevious puts the previous binary back when the binary of the service is missing and the previous one is there (it was deleted
// outside an update, for example by a deletion an uninstall left for the next restart). The service then runs the version before, and the
// update ring installs the current one again. Never during a replacement: Check does not start the service while paused.
func (s *Supervisor) restorePrevious() {
	if s.Exe == "" {
		return
	}
	previous := s.Exe + ".previous"
	if _, err := os.Stat(s.Exe); !errors.Is(err, os.ErrNotExist) {
		return
	}
	if _, err := os.Stat(previous); err != nil {
		return
	}
	if err := os.Rename(previous, s.Exe); err != nil {
		s.Logger.Error("the binary of the service is missing and the previous one could not be put back", "service", s.Service, "error", err)
		return
	}
	s.version = ""
	s.Logger.Warn("the binary of the service was missing; the previous version is back and the update ring installs the current one again",
		"service", s.Service, "exe", s.Exe)
}

// Check queries the service, starts it when it is stopped, and returns its status for the heartbeat.
func (s *Supervisor) Check(ctx context.Context) *agentv1.PeerStatus {
	now := time.Now()
	if s.Now != nil {
		now = s.Now()
	}
	state, err := s.Controller.Query(s.Service)
	s.mu.Lock()
	defer s.mu.Unlock()
	if err != nil {
		if errors.Is(err, svcctl.ErrUnsupported) {
			return nil
		}
		return &agentv1.PeerStatus{Version: s.version, State: agentv1.ServiceState_SERVICE_STATE_UNSPECIFIED, Detail: truncate(err.Error(), 500)}
	}

	if state == svcctl.StateNotInstalled {
		s.version = ""
		return &agentv1.PeerStatus{State: state.Proto()}
	}
	if now.Sub(s.versionAt) > 10*time.Minute || s.version == "" {
		probe := s.Probe
		if probe == nil {
			probe = ProbeVersion
		}
		if v, err := probe(ctx, s.Exe); err == nil {
			s.version = v
		}
		s.versionAt = now
	}

	switch state {
	case svcctl.StateRunning:
		if s.failures > 0 {
			s.Logger.Info("the service runs again", "service", s.Service)
		}
		s.failures = 0
		s.lastError = ""
	case svcctl.StateStopped:
		if !s.paused && !UninstallInProgress() && !now.Before(s.nextStart) {
			s.restorePrevious()
			s.Logger.Warn("the service is stopped; starting it", "service", s.Service)
			if err := s.Controller.Start(s.Service); err != nil {
				s.failures++
				s.lastError = err.Error()
				delay := min(30*time.Second<<min(s.failures, 5), 10*time.Minute)
				s.nextStart = now.Add(delay)
				s.Logger.Error("the service could not be started", "service", s.Service, "error", err, "retryIn", delay.String())
			} else {
				s.nextStart = now.Add(30 * time.Second)
			}
		}
	}
	detail := ""
	if state != svcctl.StateRunning {
		detail = s.lastError
	}
	return &agentv1.PeerStatus{Version: s.version, State: state.Proto(), Detail: truncate(detail, 500)}
}
