package watchdog

import (
	"context"
	"errors"
	"log/slog"
	"path/filepath"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
)

const retryDelay = 30 * time.Second

// RunService runs the watchdog of the installed agent until ctx is cancelled. Without an identity yet (the agent provisions it right
// after installing the watchdog) it already keeps the agent service running, and loads the identity as soon as it exists.
func RunService(ctx context.Context, logger *slog.Logger) {
	controller := svcctl.New()
	store := state.NewStore(platform.WatchdogStateDir(), platform.AccessSystem)
	fallback := &update.Supervisor{Controller: controller, Service: AgentServiceName, Exe: agentExe(), Logger: logger}
	for ctx.Err() == nil {
		w, err := New(Options{
			Store: store, AgentStateDir: platform.DefaultStateDir(), ProgramDir: platform.ProgramDir(), Controller: controller, Logger: logger,
		})
		if err != nil {
			if errors.Is(err, ErrNotProvisioned) {
				logger.Info("waiting for the agent to provision the watchdog identity; keeping the agent running meanwhile")
			} else {
				logger.Error("the watchdog could not start; keeping the agent running and retrying", "error", err, "retryIn", retryDelay.String())
			}
			fallback.Check(ctx)
			wait(ctx, retryDelay)
			continue
		}
		logger.Info("watchdog starting", "endpointId", w.st.Load().EndpointID)
		err = w.Run(ctx)
		_ = w.Close()
		if err != nil {
			logger.Error("the watchdog stopped unexpectedly; restarting", "error", err)
			wait(ctx, retryDelay)
		}
	}
}

func agentExe() string {
	return filepath.Join(platform.ProgramDir(), platform.BinaryName)
}

func wait(ctx context.Context, d time.Duration) {
	select {
	case <-ctx.Done():
	case <-time.After(d):
	}
}
