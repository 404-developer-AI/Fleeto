// Package service installs, removes and runs the agent as an operating system service.
package service

import (
	"context"
	"errors"
	"log/slog"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/agent"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/screen"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

const (
	// Name is the service name.
	Name = "fleeto-agent"
	// DisplayName is shown in the Services console.
	DisplayName = "Fleeto Agent"
	// Description is shown in the Services console.
	Description = "Fleeto Agent: reports the state of this endpoint to the Fleeto instance of its IT team."
)

// ErrUnsupported is returned on platforms without service support yet.
var ErrUnsupported = errors.New("installing the agent as a service is not supported on this platform yet; use 'fleeto-agent run --foreground' for now")

// InstallOptions holds the install command arguments.
type InstallOptions struct {
	Server        string
	Token         string
	CAFingerprint string
}

// retryDelay is how long the service waits before it tries to start the agent again after a start failure.
const retryDelay = 30 * time.Second

// RunAgent runs the enrolled agent from stateDir until ctx is cancelled. Start failures (a key store that is not
// ready yet during boot, a damaged state file) are logged and retried; a revoked agent stays idle so the service
// does not restart in a loop.
func RunAgent(ctx context.Context, stateDir string, access platform.Access, logger *slog.Logger, watchdog *agent.WatchdogOptions) {
	store := state.NewStore(stateDir, access)
	// The Ctrl+Alt+Del button of remote control (0.3.0) needs Windows to accept a Secure Attention Sequence from a service. Set once when
	// missing; a value set by an administrator or a group policy is kept.
	if changed, err := screen.EnableSoftwareSAS(); err != nil {
		logger.Warn("could not allow services to send Ctrl+Alt+Del (SoftwareSASGeneration)", "error", err)
	} else if changed {
		logger.Info("allowed services to send Ctrl+Alt+Del for remote control (SoftwareSASGeneration = 1)")
	}
	for ctx.Err() == nil {
		a, err := agent.New(agent.Options{Store: store, Logger: logger, Watchdog: watchdog})
		if err != nil {
			if errors.Is(err, state.ErrNotEnrolled) {
				logger.Error("the agent is not enrolled; run the install command from the site page again")
			} else {
				logger.Error("the agent could not start; retrying", "error", err, "retryIn", retryDelay.String())
			}
			wait(ctx, retryDelay)
			continue
		}
		st := a.State()
		logger.Info("agent starting", "endpointId", st.EndpointID, "server", st.Server)
		err = a.Run(ctx)
		_ = a.Close()
		if errors.Is(err, agent.ErrRevoked) {
			logger.Error("the agent is revoked and stays idle; uninstall it and run the install command from the site page again")
			<-ctx.Done()
			return
		}
		if err != nil {
			logger.Error("the agent stopped unexpectedly; restarting", "error", err)
			wait(ctx, retryDelay)
		}
	}
}

func wait(ctx context.Context, d time.Duration) {
	select {
	case <-ctx.Done():
	case <-time.After(d):
	}
}
