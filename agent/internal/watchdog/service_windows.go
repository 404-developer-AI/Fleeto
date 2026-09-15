//go:build windows

package watchdog

import (
	"context"
	"log/slog"
	"path/filepath"
	"sync"
	"time"

	"golang.org/x/sys/windows/svc"

	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// IsService reports whether the process was started by the service control manager.
func IsService() bool {
	ok, err := svc.IsWindowsService()
	return err == nil && ok
}

// Serve is the service entry point of fleetify-watchdog.
func Serve() error {
	return svc.Run(ServiceName, &handler{})
}

type handler struct{}

func (h *handler) Execute(_ []string, requests <-chan svc.ChangeRequest, status chan<- svc.Status) (bool, uint32) {
	status <- svc.Status{State: svc.StartPending}
	dir := platform.WatchdogStateDir()
	_ = platform.EnsureProtectedDir(dir, platform.AccessSystem)
	logger, closer, err := logging.NewNamed(filepath.Join(dir, "logs"), logging.WatchdogPrefix, false, slog.LevelInfo)
	if err != nil {
		logger = logging.Discard()
	} else {
		defer closer.Close()
	}

	ctx, cancel := context.WithCancel(context.Background())
	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer safego.Recover(logger, "watchdog service")
		RunService(ctx, logger)
	}()

	status <- svc.Status{State: svc.Running, Accepts: svc.AcceptStop | svc.AcceptShutdown}
	logger.Info("watchdog service started")
	for req := range requests {
		switch req.Cmd {
		case svc.Interrogate:
			status <- req.CurrentStatus
		case svc.Stop, svc.Shutdown:
			status <- svc.Status{State: svc.StopPending, WaitHint: 20000}
			cancel()
			stopped := make(chan struct{})
			go func() { wg.Wait(); close(stopped) }()
			select {
			case <-stopped:
			case <-time.After(20 * time.Second):
				logger.Warn("the watchdog did not stop in time")
			}
			logger.Info("watchdog service stopped")
			return false, 0
		}
	}
	cancel()
	return false, 0
}
