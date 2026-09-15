//go:build windows

package service

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"

	"github.com/404-developer-AI/Fleeto/agent/internal/agent"
	"github.com/404-developer-AI/Fleeto/agent/internal/checks"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
)

// stopTimeout is how long the service waits for the agent to stop before it reports stopped anyway.
const stopTimeout = 20 * time.Second

// IsService reports whether the process was started by the service control manager.
func IsService() bool {
	ok, err := svc.IsWindowsService()
	return err == nil && ok
}

// Run is the service entry point.
func Run() error {
	return svc.Run(Name, &handler{})
}

type handler struct{}

func (h *handler) Execute(_ []string, requests <-chan svc.ChangeRequest, status chan<- svc.Status) (bool, uint32) {
	status <- svc.Status{State: svc.StartPending}
	stateDir := platform.DefaultStateDir()
	logger, closer, err := logging.New(filepath.Join(stateDir, "logs"), false, slog.LevelInfo)
	if err != nil {
		// Without a log directory the agent can still work; log to nowhere rather than fail the service.
		logger = logging.Discard()
	} else {
		defer closer.Close()
	}

	ctx, cancel := context.WithCancel(context.Background())
	var wg sync.WaitGroup
	wg.Add(1)
	go func() {
		defer wg.Done()
		defer safego.Recover(logger, "service")
		RunAgent(ctx, stateDir, platform.AccessSystem, logger, &agent.WatchdogOptions{
			StateDir: platform.WatchdogStateDir(), ProgramDir: platform.ProgramDir(), Controller: svcctl.New(),
		})
	}()

	status <- svc.Status{State: svc.Running, Accepts: svc.AcceptStop | svc.AcceptShutdown}
	logger.Info("service started")
	for req := range requests {
		switch req.Cmd {
		case svc.Interrogate:
			status <- req.CurrentStatus
		case svc.Stop, svc.Shutdown:
			status <- svc.Status{State: svc.StopPending, WaitHint: uint32(stopTimeout / time.Millisecond)}
			cancel()
			stopped := make(chan struct{})
			go func() { wg.Wait(); close(stopped) }()
			select {
			case <-stopped:
			case <-time.After(stopTimeout):
				logger.Warn("the agent did not stop in time")
			}
			logger.Info("service stopped")
			return false, 0
		}
	}
	cancel()
	return false, 0
}

// State returns the service state, e.g. "Running", or "Not installed".
func State() (string, error) {
	_, name, err := checks.QueryService(Name)
	if errors.Is(err, windows.ERROR_SERVICE_DOES_NOT_EXIST) {
		return "Not installed", nil
	}
	return name, err
}

// Install copies the binary, enrolls, and creates and starts the service. Enrollment comes before the service: a
// refused token or an unreachable gateway stops the install with nothing left behind.
func Install(ctx context.Context, opts InstallOptions, out io.Writer) (err error) {
	if !platform.IsElevated() {
		return fmt.Errorf("installing the agent requires administrator rights: %s", platform.ElevationHint)
	}
	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("cannot open the service manager: %w", err)
	}
	defer m.Disconnect()
	if s, err := m.OpenService(Name); err == nil {
		s.Close()
		return errors.New("the Fleeto Agent service is already installed; run 'fleeto-agent uninstall' first to install it again")
	}
	// An agent from before the rename to Fleeto (0.2.1) is taken over with its enrollment.
	if taken, err := takeOverLegacyAgent(ctx, m, opts, out); err != nil || taken {
		return err
	}

	var rollback []func()
	defer func() {
		if err != nil {
			for i := len(rollback) - 1; i >= 0; i-- {
				rollback[i]()
			}
		}
	}()

	// 1. Program files.
	programDir := platform.ProgramDir()
	exePath := filepath.Join(programDir, platform.BinaryName)
	programDirExisted := exists(programDir)
	if err := installBinary(exePath); err != nil {
		return err
	}
	rollback = append(rollback, func() {
		if !programDirExisted {
			_ = os.RemoveAll(programDir)
		} else {
			_ = os.Remove(exePath)
		}
	})
	fmt.Fprintf(out, "Installed %s\n", exePath)

	// 2. State directory, SYSTEM and Administrators only.
	stateDir := platform.DefaultStateDir()
	stateDirExisted := exists(stateDir)
	if err := platform.EnsureProtectedDir(stateDir, platform.AccessSystem); err != nil {
		return err
	}
	logger, closer, err := logging.New(filepath.Join(stateDir, "logs"), false, slog.LevelInfo)
	if err != nil {
		return err
	}
	closeLog := sync.OnceFunc(func() { _ = closer.Close() })
	defer closeLog()
	rollback = append(rollback, func() {
		closeLog()
		if !stateDirExisted {
			_ = os.RemoveAll(stateDir)
		}
	})

	// 3. Enrollment, with a non-exportable machine key (TPM when available).
	fmt.Fprintf(out, "Enrolling with %s\n", opts.Server)
	st, err := agent.Enroll(ctx, agent.EnrollParams{
		StateDir: stateDir, Access: platform.AccessSystem,
		Key:    state.KeyRef{Kind: keystore.KindCNG, Machine: true},
		Server: opts.Server, Token: opts.Token, CAFingerprint: opts.CAFingerprint, Logger: logger,
	})
	if err != nil {
		return fmt.Errorf("enrollment failed, nothing was installed: %w", err)
	}
	rollback = append(rollback, func() {
		_ = keystore.Delete(stateDir, st.Key)
		_ = state.NewStore(stateDir, platform.AccessSystem).Delete()
		fmt.Fprintf(out, "The endpoint %s was enrolled but the install did not finish; delete it in the site before installing again.\n", st.EndpointID)
	})
	fmt.Fprintf(out, "Enrolled as endpoint %s (key store: %s)\n", st.EndpointID, describeKey(st.Key))

	// 4. Service: LocalSystem, automatic start, restart on failure.
	s, err := createAndStartService(m, exePath, &rollback)
	if err != nil {
		return err
	}
	defer s.Close()
	logger.Info("service installed and started", "endpointId", st.EndpointID)
	fmt.Fprintf(out, "The %s service is running.\n", DisplayName)
	return nil
}

// createAndStartService creates the agent service (LocalSystem, automatic start, restart on failure) and waits until it runs. Removing
// the service is appended to rollback as soon as it exists.
func createAndStartService(m *mgr.Mgr, exePath string, rollback *[]func()) (*mgr.Service, error) {
	s, err := m.CreateService(Name, exePath, mgr.Config{
		DisplayName:      DisplayName,
		Description:      Description,
		StartType:        mgr.StartAutomatic,
		ErrorControl:     mgr.ErrorNormal,
		ServiceStartName: "LocalSystem",
	}, "run")
	if err != nil {
		return nil, fmt.Errorf("create the service: %w", err)
	}
	*rollback = append(*rollback, func() {
		_, _ = s.Control(svc.Stop)
		_ = waitForState(s, svc.Stopped, 30*time.Second)
		_ = s.Delete()
	})
	if err := s.SetRecoveryActions([]mgr.RecoveryAction{
		{Type: mgr.ServiceRestart, Delay: 10 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 30 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 60 * time.Second},
	}, uint32((24 * time.Hour).Seconds())); err != nil {
		return nil, fmt.Errorf("set the service recovery actions: %w", err)
	}
	if err := s.SetRecoveryActionsOnNonCrashFailures(true); err != nil {
		return nil, fmt.Errorf("set the service recovery actions: %w", err)
	}
	if err := s.Start(); err != nil {
		return nil, fmt.Errorf("start the service: %w", err)
	}
	if err := waitForState(s, svc.Running, 30*time.Second); err != nil {
		return nil, err
	}
	return s, nil
}

func describeKey(ref state.KeyRef) string {
	if ref.Provider == keystore.ProviderPlatform {
		return "TPM"
	}
	if ref.Provider != "" {
		return ref.Provider
	}
	return ref.Kind
}

// installBinary copies the running executable to its install location, unless it already runs from there.
func installBinary(target string) error {
	self, err := os.Executable()
	if err != nil {
		return fmt.Errorf("locate the running executable: %w", err)
	}
	if resolved, err := filepath.EvalSymlinks(self); err == nil {
		self = resolved
	}
	if strings.EqualFold(filepath.Clean(self), filepath.Clean(target)) {
		return nil
	}
	if err := os.MkdirAll(filepath.Dir(target), 0o755); err != nil {
		return fmt.Errorf("create %s: %w", filepath.Dir(target), err)
	}
	data, err := os.ReadFile(self)
	if err != nil {
		return fmt.Errorf("read %s: %w", self, err)
	}
	tmp := target + ".new"
	if err := os.WriteFile(tmp, data, 0o755); err != nil {
		return fmt.Errorf("write %s: %w", tmp, err)
	}
	if err := os.Rename(tmp, target); err != nil {
		_ = os.Remove(tmp)
		return fmt.Errorf("install %s: %w", target, err)
	}
	return nil
}

func waitForState(s *mgr.Service, want svc.State, timeout time.Duration) error {
	deadline := time.Now().Add(timeout)
	for {
		st, err := s.Query()
		if err != nil {
			return fmt.Errorf("query the service: %w", err)
		}
		if st.State == want {
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("the service did not reach state %s within %s (current: %s); see the log in %s",
				checks.ServiceStateName(uint32(want)), timeout, checks.ServiceStateName(uint32(st.State)),
				filepath.Join(platform.DefaultStateDir(), "logs"))
		}
		time.Sleep(300 * time.Millisecond)
	}
}

// Uninstall stops and deletes the watchdog and the agent service, deletes both identity keys, the state and the program files. A marker
// in the data directory tells both services not to restart or reinstall each other meanwhile.
func Uninstall(out io.Writer) error {
	if !platform.IsElevated() {
		return fmt.Errorf("uninstalling the agent requires administrator rights: %s", platform.ElevationHint)
	}
	var errs []error
	marker := filepath.Join(platform.DataDir(), update.UninstallMarkerName)
	if err := os.MkdirAll(platform.DataDir(), 0o755); err == nil {
		_ = os.WriteFile(marker, []byte(time.Now().UTC().Format(time.RFC3339)), 0o600)
	}
	defer os.Remove(marker)

	// The watchdog first: while it runs it would start the agent again.
	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()
	if err := svcctl.Delete(ctx, agent.WatchdogServiceName, time.Minute); err != nil {
		errs = append(errs, fmt.Errorf("remove the watchdog service: %w", err))
	} else {
		fmt.Fprintln(out, "Watchdog service removed")
	}
	watchdogDir := platform.WatchdogStateDir()
	watchdogKey := state.KeyRef{Kind: keystore.KindCNG, Name: agent.WatchdogKeyName, Machine: true}
	if st, err := state.NewStore(watchdogDir, platform.AccessSystem).Load(); err == nil {
		watchdogKey = st.Key
	}
	if err := keystore.Delete(watchdogDir, watchdogKey); err != nil {
		errs = append(errs, fmt.Errorf("delete the watchdog key: %w", err))
	}
	if err := os.RemoveAll(watchdogDir); err != nil {
		errs = append(errs, fmt.Errorf("delete %s: %w", watchdogDir, err))
	}

	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("cannot open the service manager: %w", err)
	}
	defer m.Disconnect()
	if s, err := m.OpenService(Name); err == nil {
		if st, err := s.Query(); err == nil && st.State != svc.Stopped {
			fmt.Fprintln(out, "Stopping the service")
			if _, err := s.Control(svc.Stop); err != nil {
				errs = append(errs, fmt.Errorf("stop the service: %w", err))
			} else if err := waitForState(s, svc.Stopped, 60*time.Second); err != nil {
				errs = append(errs, err)
			}
		}
		if err := s.Delete(); err != nil {
			errs = append(errs, fmt.Errorf("delete the service: %w", err))
		} else {
			fmt.Fprintln(out, "Service removed")
		}
		s.Close()
	} else {
		fmt.Fprintln(out, "The service is not installed")
	}

	stateDir := platform.DefaultStateDir()
	ref := state.KeyRef{Kind: keystore.KindCNG, Name: keystore.DefaultCNGName, Machine: true}
	if st, err := state.NewStore(stateDir, platform.AccessSystem).Load(); err == nil {
		ref = st.Key
	}
	if err := keystore.Delete(stateDir, ref); err != nil {
		errs = append(errs, fmt.Errorf("delete the identity key: %w", err))
	} else {
		fmt.Fprintln(out, "Identity key deleted")
	}
	if err := os.RemoveAll(stateDir); err != nil {
		errs = append(errs, fmt.Errorf("delete %s: %w", stateDir, err))
	} else {
		fmt.Fprintf(out, "Deleted %s\n", stateDir)
	}
	_ = removeIfEmpty(filepath.Dir(stateDir))

	programDir := platform.ProgramDir()
	if err := removeProgramDir(programDir, out); err != nil {
		errs = append(errs, err)
	}
	_ = removeIfEmpty(filepath.Dir(programDir))
	return errors.Join(errs...)
}

func removeProgramDir(dir string, out io.Writer) error {
	if !exists(dir) {
		return nil
	}
	if err := os.RemoveAll(dir); err == nil {
		fmt.Fprintf(out, "Deleted %s\n", dir)
		return nil
	}
	// The running executable (uninstall started from the installed binary) cannot delete itself: remove it at the
	// next restart instead.
	entries, _ := os.ReadDir(dir)
	for _, e := range entries {
		path := filepath.Join(dir, e.Name())
		if err := os.RemoveAll(path); err != nil {
			if err := moveOnReboot(path); err != nil {
				return fmt.Errorf("delete %s: %w", path, err)
			}
		}
	}
	if err := os.Remove(dir); err != nil {
		if err := moveOnReboot(dir); err != nil {
			return fmt.Errorf("delete %s: %w", dir, err)
		}
		fmt.Fprintf(out, "%s will be deleted at the next restart\n", dir)
		return nil
	}
	fmt.Fprintf(out, "Deleted %s\n", dir)
	return nil
}

func moveOnReboot(path string) error {
	p, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return err
	}
	return windows.MoveFileEx(p, nil, windows.MOVEFILE_DELAY_UNTIL_REBOOT)
}

func removeIfEmpty(dir string) error {
	entries, err := os.ReadDir(dir)
	if err != nil || len(entries) > 0 {
		return err
	}
	return os.Remove(dir)
}

func exists(path string) bool {
	_, err := os.Stat(path)
	return err == nil
}
