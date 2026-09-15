package update

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/svcctl"
)

// Outcome is how an installation ended.
type Outcome int

const (
	// Installed: the new version runs and connected.
	Installed Outcome = iota
	// Failed: nothing was replaced, or the replacement was undone before the service started; the old version keeps running.
	Failed
	// RolledBack: the new version was started but did not come up; the previous version was restored.
	RolledBack
)

// JournalFileName records a replacement in progress in the installer's state directory.
const JournalFileName = "update-journal.json"

// Journal is written before a service binary is replaced and removed afterwards. After a crash or power loss in between, Recover puts
// the previous binary back when the new one is missing.
type Journal struct {
	Service   string    `json:"service"`
	Exe       string    `json:"exe"`
	Previous  string    `json:"previous"`
	Version   string    `json:"version"`
	StartedAt time.Time `json:"startedAt"`
}

// InstallRequest describes replacing the binary of a service by a verified staged binary.
type InstallRequest struct {
	Controller svcctl.Controller
	Service    string
	// Exe is the installed binary the service runs.
	Exe string
	// Staged is the downloaded, verified binary.
	Staged string
	SHA256 string
	Size   int64
	// Version the staged binary reports.
	Version string
	// JournalDir is the state directory of the installing service.
	JournalDir string
	Access     platform.Access
	// Healthy blocks until the new version is healthy; since is when the service was started.
	Healthy      func(ctx context.Context, since time.Time) error
	StopTimeout  time.Duration
	StartTimeout time.Duration
	Logger       *slog.Logger
	Now          func() time.Time
}

// Install replaces the binary of a stopped service and starts it. The service is stopped, the installed binary is kept as .previous, the
// staged binary is copied into place and verified again, and the service is started. When it does not start or does not become healthy,
// the previous binary is restored and started. The detail of a failure or rollback states the cause.
func Install(ctx context.Context, r InstallRequest) (Outcome, error) {
	if r.Now == nil {
		r.Now = time.Now
	}
	if r.StopTimeout <= 0 {
		r.StopTimeout = time.Minute
	}
	if r.StartTimeout <= 0 {
		r.StartTimeout = 30 * time.Second
	}
	if err := VerifyFile(r.Staged, r.SHA256, r.Size); err != nil {
		return Failed, err
	}
	if _, err := os.Stat(r.Exe); err != nil {
		return Failed, fmt.Errorf("the installed binary %s is missing: %w", r.Exe, err)
	}

	// The copy goes next to the installed binary first, so the final rename cannot fail halfway across volumes.
	next := r.Exe + ".new"
	previous := r.Exe + ".previous"
	if err := copyFile(r.Staged, next); err != nil {
		return Failed, err
	}
	if err := VerifyFile(next, r.SHA256, r.Size); err != nil {
		_ = os.Remove(next)
		return Failed, err
	}

	journal := Journal{Service: r.Service, Exe: r.Exe, Previous: previous, Version: r.Version, StartedAt: r.Now().UTC()}
	if err := writeJournal(r.JournalDir, r.Access, journal); err != nil {
		_ = os.Remove(next)
		return Failed, err
	}
	defer removeJournal(r.JournalDir)

	if err := r.Controller.Stop(ctx, r.Service, r.StopTimeout); err != nil {
		_ = os.Remove(next)
		// The service may be half stopped: ask for a start so it does not stay down because of this attempt.
		_ = r.Controller.Start(r.Service)
		return Failed, fmt.Errorf("the %s service did not stop: %w", r.Service, err)
	}
	_ = os.Remove(previous)
	if err := os.Rename(r.Exe, previous); err != nil {
		_ = os.Remove(next)
		_ = r.Controller.Start(r.Service)
		return Failed, fmt.Errorf("keep the installed binary as %s: %w", filepath.Base(previous), err)
	}
	if err := os.Rename(next, r.Exe); err != nil {
		restoreErr := os.Rename(previous, r.Exe)
		_ = r.Controller.Start(r.Service)
		return Failed, errors.Join(fmt.Errorf("move the new binary into place: %w", err), restoreErr)
	}

	started := r.Now()
	r.Logger.Info("new binary in place; starting the service", "service", r.Service, "version", r.Version)
	err := r.Controller.Start(r.Service)
	if err == nil {
		err = r.Controller.WaitRunning(ctx, r.Service, r.StartTimeout)
	}
	if err == nil && r.Healthy != nil {
		err = r.Healthy(ctx, started)
	}
	if err == nil {
		return Installed, nil
	}

	r.Logger.Warn("the new version did not come up; restoring the previous version", "service", r.Service, "version", r.Version, "error", err)
	if rollbackErr := rollback(ctx, r, previous); rollbackErr != nil {
		return RolledBack, fmt.Errorf("%w; restoring the previous version also failed: %v", err, rollbackErr)
	}
	return RolledBack, err
}

func rollback(ctx context.Context, r InstallRequest, previous string) error {
	stopErr := r.Controller.Stop(ctx, r.Service, r.StopTimeout)
	failed := r.Exe + ".failed"
	_ = os.Remove(failed)
	var errs []error
	if err := os.Rename(r.Exe, failed); err != nil && !errors.Is(err, os.ErrNotExist) {
		errs = append(errs, fmt.Errorf("set the new binary aside: %w", err))
	}
	if err := os.Rename(previous, r.Exe); err != nil {
		errs = append(errs, fmt.Errorf("restore %s: %w", filepath.Base(r.Exe), err))
	}
	if err := r.Controller.Start(r.Service); err != nil {
		errs = append(errs, fmt.Errorf("start the previous version: %w (stop: %v)", err, stopErr))
	}
	_ = os.Remove(failed)
	return errors.Join(errs...)
}

// Recover finishes a replacement that a crash interrupted: when the installed binary is missing but .previous exists, the previous binary
// is put back. The journal is removed either way; the other service or the service manager starts the service again.
func Recover(dir string, logger *slog.Logger) {
	path := filepath.Join(dir, JournalFileName)
	data, err := os.ReadFile(path)
	if err != nil {
		return
	}
	defer removeJournal(dir)
	var j Journal
	if err := json.Unmarshal(data, &j); err != nil || j.Exe == "" || j.Previous != j.Exe+".previous" {
		logger.Warn("an unreadable update journal was removed")
		return
	}
	if _, err := os.Stat(j.Exe); errors.Is(err, os.ErrNotExist) {
		if err := os.Rename(j.Previous, j.Exe); err != nil {
			logger.Error("an interrupted update left no binary and the previous one could not be restored", "exe", j.Exe, "error", err)
			return
		}
		logger.Warn("an interrupted update was undone: the previous binary is back", "exe", j.Exe, "version", j.Version)
	}
	_ = os.Remove(j.Exe + ".new")
}

func writeJournal(dir string, access platform.Access, j Journal) error {
	data, err := json.Marshal(j)
	if err != nil {
		return err
	}
	return platform.WriteFileAtomic(filepath.Join(dir, JournalFileName), data, access)
}

func removeJournal(dir string) { _ = os.Remove(filepath.Join(dir, JournalFileName)) }

func copyFile(from, to string) error {
	in, err := os.Open(from)
	if err != nil {
		return err
	}
	defer in.Close()
	out, err := os.OpenFile(to, os.O_CREATE|os.O_TRUNC|os.O_WRONLY, 0o755)
	if err != nil {
		return fmt.Errorf("create %s: %w", to, err)
	}
	_, copyErr := io.Copy(out, in)
	syncErr := out.Sync()
	closeErr := out.Close()
	if err := errors.Join(copyErr, syncErr, closeErr); err != nil {
		_ = os.Remove(to)
		return fmt.Errorf("write %s: %w", to, err)
	}
	return nil
}
