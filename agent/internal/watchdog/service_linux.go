//go:build linux

package watchdog

import (
	"context"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"

	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

// IsService reports whether systemd started this process. INVOCATION_ID is inherited by every child, so the parent must be the
// service manager itself.
func IsService() bool {
	return os.Getenv("INVOCATION_ID") != "" && os.Getppid() == 1
}

// Serve is the service entry point of fleeto-watchdog: it supervises the agent until systemd stops the unit.
func Serve() error {
	dir := platform.WatchdogStateDir()
	_ = platform.EnsureProtectedDir(dir, platform.AccessSystem)
	logger, closer, err := logging.NewNamed(filepath.Join(dir, "logs"), logging.WatchdogPrefix, true, slog.LevelInfo)
	if err != nil {
		logger = logging.Discard()
	} else {
		defer closer.Close()
	}
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()
	logger.Info("watchdog service started", "stateDir", dir)
	RunService(ctx, logger)
	logger.Info("watchdog service stopped")
	return nil
}
