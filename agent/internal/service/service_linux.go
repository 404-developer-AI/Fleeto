//go:build linux

package service

import (
	"context"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/exec"
	"os/signal"
	"path/filepath"
	"sync"
	"syscall"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/agent"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/logging"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
)

// IsService reports whether systemd started this process. INVOCATION_ID is inherited by every child, so the parent must be the
// service manager itself: a script the agent runs never counts as the service.
func IsService() bool {
	return os.Getenv("INVOCATION_ID") != "" && os.Getppid() == 1
}

// Run is the service entry point: it runs the agent until systemd stops the unit.
func Run() error {
	stateDir := platform.DefaultStateDir()
	// The unit writes to the journal, so the log goes to both: journalctl -u fleeto-agent, and the files in the state directory.
	logger, closer, err := logging.New(filepath.Join(stateDir, "logs"), true, slog.LevelInfo)
	if err != nil {
		logger = logging.Discard()
	} else {
		defer closer.Close()
	}
	ctx, stop := signal.NotifyContext(context.Background(), syscall.SIGTERM, syscall.SIGINT)
	defer stop()
	logger.Info("service started", "stateDir", stateDir)
	RunAgent(ctx, stateDir, platform.AccessSystem, logger, &agent.WatchdogOptions{
		StateDir: platform.WatchdogStateDir(), ProgramDir: platform.ProgramDir(), Controller: svcctl.New(),
	})
	logger.Info("service stopped")
	return nil
}

// State returns the service state, e.g. "Running", or "Not installed".
func State() (string, error) {
	current, err := svcctl.New().Query(Name)
	if err != nil {
		return "", err
	}
	return stateName(current), nil
}

func stateName(s svcctl.State) string {
	switch s {
	case svcctl.StateRunning:
		return "Running"
	case svcctl.StateStopped:
		return "Stopped"
	case svcctl.StateStarting:
		return "Starting"
	case svcctl.StateStopping:
		return "Stopping"
	case svcctl.StateDisabled:
		return "Disabled"
	case svcctl.StateNotInstalled:
		return "Not installed"
	default:
		return "Unknown"
	}
}

// Install installs the binary, enrolls, and creates and starts the systemd units. Enrollment comes before the service: a refused
// token or an unreachable gateway stops the install with nothing left behind.
func Install(ctx context.Context, opts InstallOptions, out io.Writer) (err error) {
	if !platform.IsElevated() {
		return fmt.Errorf("installing the agent requires root: %s", platform.ElevationHint)
	}
	controller := svcctl.New()
	current, err := controller.Query(Name)
	if err != nil {
		return err
	}
	if current != svcctl.StateNotInstalled {
		return errors.New("the fleeto-agent service is already installed; run 'fleeto-agent uninstall' first to install it again")
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

	// 2. State directory, root only.
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

	// 3. Enrollment, with a key in the TPM when the endpoint has one and a root-only key file otherwise.
	fmt.Fprintf(out, "Enrolling with %s\n", opts.Server)
	st, err := agent.Enroll(ctx, agent.EnrollParams{
		StateDir: stateDir, Access: platform.AccessSystem,
		Key:    state.KeyRef{Kind: identityKeyKind()},
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

	// 4. The systemd unit: root, started at boot, restarted after a failure.
	if err := svcctl.Create(svcctl.Definition{
		Name: Name, DisplayName: DisplayName, Description: Description, Executable: exePath, Args: []string{"run"},
	}); err != nil {
		return err
	}
	rollback = append(rollback, func() {
		deleteCtx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
		defer cancel()
		_ = svcctl.Delete(deleteCtx, Name, time.Minute)
	})
	if err := controller.Start(Name); err != nil {
		return err
	}
	if err := controller.WaitRunning(ctx, Name, 30*time.Second); err != nil {
		return fmt.Errorf("%w; see the log in %s", err, filepath.Join(stateDir, "logs"))
	}
	logger.Info("service installed and started", "endpointId", st.EndpointID)
	fmt.Fprintf(out, "The %s service is running.\n", DisplayName)
	return nil
}

// identityKeyKind is the key store of a Linux service install: the TPM when the endpoint has one, a root-only key file otherwise.
func identityKeyKind() string {
	if keystore.TPMAvailable() {
		return keystore.KindTPM
	}
	return keystore.KindFile
}

func describeKey(ref state.KeyRef) string {
	switch ref.Kind {
	case keystore.KindTPM:
		return "TPM 2.0"
	case keystore.KindFile:
		return "root-only key file"
	default:
		return ref.Kind
	}
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
	if filepath.Clean(self) == filepath.Clean(target) {
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
	if err := os.WriteFile(tmp, data, 0o755); err != nil { // #nosec G306 -- a program file has to be executable.
		return fmt.Errorf("write %s: %w", tmp, err)
	}
	if err := os.Rename(tmp, target); err != nil {
		_ = os.Remove(tmp)
		return fmt.Errorf("install %s: %w", target, err)
	}
	restoreSELinuxContext(target)
	return nil
}

// restoreSELinuxContext gives a newly written program file the SELinux context of its directory, so systemd may execute it on the
// RHEL family. Best effort: systems without SELinux have no restorecon and need none.
func restoreSELinuxContext(path string) {
	tool, err := exec.LookPath("restorecon")
	if err != nil {
		return
	}
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	_ = exec.CommandContext(ctx, tool, "-F", path).Run() // #nosec G204 -- fixed tool, path built by the installer.
}

// Uninstall stops and removes the watchdog and the agent unit, deletes both identity keys, the state and the program files. A marker
// in the data directory tells both services not to restart or reinstall each other meanwhile.
func Uninstall(out io.Writer) error {
	if !platform.IsElevated() {
		return fmt.Errorf("uninstalling the agent requires root: %s", platform.ElevationHint)
	}
	var errs []error
	marker := filepath.Join(platform.DataDir(), update.UninstallMarkerName)
	if err := os.MkdirAll(platform.DataDir(), 0o755); err == nil {
		_ = os.WriteFile(marker, []byte(time.Now().UTC().Format(time.RFC3339)), 0o600)
	}
	defer os.Remove(marker)

	ctx, cancel := context.WithTimeout(context.Background(), 2*time.Minute)
	defer cancel()

	// The watchdog first: while it runs it would start the agent again.
	if err := svcctl.Delete(ctx, agent.WatchdogServiceName, time.Minute); err != nil {
		errs = append(errs, fmt.Errorf("remove the watchdog service: %w", err))
	} else {
		fmt.Fprintln(out, "Watchdog service removed")
	}
	watchdogDir := platform.WatchdogStateDir()
	if st, err := state.NewStore(watchdogDir, platform.AccessSystem).Load(); err == nil {
		if err := keystore.Delete(watchdogDir, st.Key); err != nil {
			errs = append(errs, fmt.Errorf("delete the watchdog key: %w", err))
		}
	}
	if err := os.RemoveAll(watchdogDir); err != nil {
		errs = append(errs, fmt.Errorf("delete %s: %w", watchdogDir, err))
	}

	// The agent unit.
	switch current, err := svcctl.New().Query(Name); {
	case err != nil:
		errs = append(errs, err)
	case current == svcctl.StateNotInstalled:
		fmt.Fprintln(out, "The service is not installed")
	default:
		if err := svcctl.Delete(ctx, Name, time.Minute); err != nil {
			errs = append(errs, fmt.Errorf("remove the service: %w", err))
		} else {
			fmt.Fprintln(out, "Service removed")
		}
	}

	stateDir := platform.DefaultStateDir()
	if st, err := state.NewStore(stateDir, platform.AccessSystem).Load(); err == nil {
		if err := keystore.Delete(stateDir, st.Key); err != nil {
			errs = append(errs, fmt.Errorf("delete the identity key: %w", err))
		} else {
			fmt.Fprintln(out, "Identity key deleted")
		}
	}
	if err := os.RemoveAll(stateDir); err != nil {
		errs = append(errs, fmt.Errorf("delete %s: %w", stateDir, err))
	} else {
		fmt.Fprintf(out, "Deleted %s\n", stateDir)
	}

	programDir := platform.ProgramDir()
	if exists(programDir) {
		// A running program file can be removed on Linux, so the uninstall started from the installed binary finishes on its own.
		if err := os.RemoveAll(programDir); err != nil {
			errs = append(errs, fmt.Errorf("delete %s: %w", programDir, err))
		} else {
			fmt.Fprintf(out, "Deleted %s\n", programDir)
		}
	}
	_ = os.Remove(marker)
	_ = removeIfEmpty(platform.DataDir())
	return errors.Join(errs...)
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
