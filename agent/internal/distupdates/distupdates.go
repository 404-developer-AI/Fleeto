// Package distupdates turns the automatic updates of a Linux distribution off while a patch policy of engine Fleeto asks for it
// (0.9.0, AgentConfig.turn_off_distribution_updates, PATCHING.md section 5 and section 14 decision 9), so only the rounds of the
// patch policy install updates. It is what internal/winupdate is on Windows.
//
//   - dpkg (Ubuntu, Debian, Proxmox VE): when unattended-upgrades is installed, the agent writes
//     /etc/apt/apt.conf.d/99fleeto-no-unattended-upgrades with APT::Periodic::Unattended-Upgrade "0". The file is the agent's when its
//     first line is exactly AptMarker; a file of that name without it is somebody else's and is never changed or removed. The agent
//     removes its file when the configuration no longer asks for it and when it is uninstalled.
//   - rpm (AlmaLinux): the agent disables the dnf-automatic timers that are enabled (systemctl disable --now) and records each in a
//     marker file in its state directory, before it disables it. When the configuration no longer asks for it, or at the uninstall,
//     it enables exactly the recorded timers again (systemctl enable --now) and deletes the marker. A timer somebody else disabled or
//     masked is never touched.
//
// Every path is below System.Root and every systemctl call goes through System.Runner, so the tests run in a temporary directory
// with a fake runner.
package distupdates

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
	"slices"
	"strings"
)

// The states reported in Inventory.distribution_updates; the server knows the same values.
const (
	// StateTurnedOff: the agent turned unattended-upgrades or dnf-automatic off.
	StateTurnedOff = "turned_off"
	// StateKept: the configuration asks for it, but somebody else configured them (a file of the agent's name that is not the
	// agent's, a timer somebody else masked), so the agent left them.
	StateKept = "kept"
	// StateNone: the configuration asks for it, but neither unattended-upgrades nor an enabled dnf-automatic timer is there.
	StateNone = "none"
	// StateUntouched: the configuration does not ask for it; the agent undid its own change.
	StateUntouched = "untouched"
)

const (
	// AptFile is the apt configuration file the agent writes.
	AptFile = "/etc/apt/apt.conf.d/99fleeto-no-unattended-upgrades"
	// AptMarker is the first line of AptFile: the file is the agent's only with exactly this line.
	AptMarker = "// Written by the Fleeto agent (fleeto-no-unattended-upgrades): unattended-upgrades is off while a Fleeto patch policy installs the updates."
	aptBody   = AptMarker + "\n" +
		"// The agent removes this file when the patch policy no longer asks for it and when the agent is uninstalled. Do not edit it.\n" +
		"APT::Periodic::Unattended-Upgrade \"0\";\n"

	// MarkerFile is the record of the timers the agent disabled, in its state directory.
	MarkerFile = "distribution-updates.json"

	aptGet                  = "/usr/bin/apt-get"
	dpkgStatus              = "/var/lib/dpkg/status"
	dnf                     = "/usr/bin/dnf"
	rpmDB                   = "/usr/bin/rpm"
	unattendedUpgradeBinary = "/usr/bin/unattended-upgrade"
	unattendedUpgradeConfig = "/etc/apt/apt.conf.d/50unattended-upgrades"
)

// Timers are the dnf-automatic timers of AlmaLinux 8 to 10.
var Timers = []string{
	"dnf-automatic.timer",
	"dnf-automatic-install.timer",
	"dnf-automatic-download.timer",
	"dnf-automatic-notifyonly.timer",
}

// Runner runs systemctl. Output is the standard output; code the exit code. err is set only when the command could not run.
type Runner interface {
	Run(ctx context.Context, name string, args ...string) (output string, code int, err error)
}

// System is the endpoint.
type System struct {
	// Root is prepended to every path; empty is "/".
	Root string
	// StateDir is the state directory of the agent, which holds MarkerFile.
	StateDir string
	Runner   Runner
}

// marker is the content of MarkerFile.
type marker struct {
	Timers []string `json:"timers"`
}

func (s System) path(p string) string {
	if s.Root == "" {
		return p
	}
	return filepath.Join(s.Root, filepath.FromSlash(p))
}

func (s System) exists(p string) bool {
	_, err := os.Stat(s.path(p))
	return err == nil
}

func (s System) dpkg() bool { return s.exists(aptGet) || s.exists(dpkgStatus) }
func (s System) rpm() bool  { return s.exists(dnf) && s.exists(rpmDB) }

// Apply brings the automatic updates of the distribution in line with want (turn_off_distribution_updates of a managed
// configuration of an endpoint Fleeto patches) and returns the state to report and whether it changed anything. It is idempotent,
// so the agent calls it with every inventory.
func (s System) Apply(ctx context.Context, want bool) (string, bool, error) {
	if !want {
		changed, err := s.undo(ctx)
		return StateUntouched, changed, err
	}
	switch {
	case s.dpkg():
		return s.applyApt()
	case s.rpm():
		return s.applyTimers(ctx)
	}
	return StateNone, false, nil
}

// Restore undoes what the agent changed, for the uninstall of the agent.
func (s System) Restore(ctx context.Context) error {
	_, err := s.undo(ctx)
	return err
}

// undo removes the agent's apt file and enables the timers it disabled.
func (s System) undo(ctx context.Context) (bool, error) {
	var errs []error
	changed := false
	if c, err := s.removeAptFile(); err != nil {
		errs = append(errs, err)
	} else {
		changed = changed || c
	}
	if c, err := s.enableRecorded(ctx); err != nil {
		errs = append(errs, err)
	} else {
		changed = changed || c
	}
	return changed, errors.Join(errs...)
}

// --- dpkg ---

// aptFileState is what is at AptFile.
type aptFileState int

const (
	aptAbsent aptFileState = iota
	aptOurs
	aptForeign
)

func (s System) readAptFile() (aptFileState, string, error) {
	data, err := os.ReadFile(s.path(AptFile))
	if errors.Is(err, fs.ErrNotExist) {
		return aptAbsent, "", nil
	}
	if err != nil {
		return aptAbsent, "", fmt.Errorf("read %s: %w", AptFile, err)
	}
	first, _, _ := strings.Cut(string(data), "\n")
	if strings.TrimSuffix(first, "\r") == AptMarker {
		return aptOurs, string(data), nil
	}
	return aptForeign, string(data), nil
}

func (s System) unattendedUpgradesInstalled() bool {
	return s.exists(unattendedUpgradeBinary) || s.exists(unattendedUpgradeConfig)
}

func (s System) applyApt() (string, bool, error) {
	state, content, err := s.readAptFile()
	if err != nil {
		return "", false, err
	}
	switch state {
	case aptForeign:
		// A file of the agent's name that somebody else wrote: never changed.
		return StateKept, false, nil
	case aptOurs:
		if content == aptBody {
			return StateTurnedOff, false, nil
		}
		// The agent's file with other content (an older agent, or somebody edited it below the marker): written again.
		if err := writeAtomic(s.path(AptFile), []byte(aptBody), 0o644); err != nil {
			return "", false, fmt.Errorf("write %s: %w", AptFile, err)
		}
		return StateTurnedOff, true, nil
	}
	if !s.unattendedUpgradesInstalled() {
		return StateNone, false, nil
	}
	if err := writeAtomic(s.path(AptFile), []byte(aptBody), 0o644); err != nil {
		return "", false, fmt.Errorf("turn unattended-upgrades off: write %s: %w", AptFile, err)
	}
	return StateTurnedOff, true, nil
}

// removeAptFile removes AptFile when it is the agent's.
func (s System) removeAptFile() (bool, error) {
	state, _, err := s.readAptFile()
	if err != nil || state != aptOurs {
		return false, err
	}
	if err := os.Remove(s.path(AptFile)); err != nil && !errors.Is(err, fs.ErrNotExist) {
		return false, fmt.Errorf("turn unattended-upgrades back on: remove %s: %w", AptFile, err)
	}
	return true, nil
}

// --- rpm ---

func (s System) markerPath() (string, error) {
	if s.StateDir == "" {
		return "", errors.New("the agent has no state directory to record the dnf-automatic timers in")
	}
	return filepath.Join(s.StateDir, MarkerFile), nil
}

// readMarker returns the recorded timers; only names of Timers count.
func (s System) readMarker() ([]string, bool, error) {
	if s.StateDir == "" {
		// Without a state directory nothing was ever recorded.
		return nil, false, nil
	}
	p, err := s.markerPath()
	if err != nil {
		return nil, false, err
	}
	data, err := os.ReadFile(p)
	if errors.Is(err, fs.ErrNotExist) {
		return nil, false, nil
	}
	if err != nil {
		return nil, false, fmt.Errorf("read the record of the dnf-automatic timers: %w", err)
	}
	var m marker
	if err := json.Unmarshal(data, &m); err != nil {
		// A record that cannot be read is kept as it is: the agent cannot tell which timers were its, so it enables none.
		return nil, true, fmt.Errorf("the record of the dnf-automatic timers cannot be read: %w", err)
	}
	var timers []string
	for _, t := range m.Timers {
		if slices.Contains(Timers, t) && !slices.Contains(timers, t) {
			timers = append(timers, t)
		}
	}
	return timers, true, nil
}

func (s System) writeMarker(timers []string) error {
	p, err := s.markerPath()
	if err != nil {
		return err
	}
	data, err := json.Marshal(marker{Timers: timers})
	if err != nil {
		return err
	}
	if err := writeAtomic(p, data, 0o600); err != nil {
		return fmt.Errorf("record the dnf-automatic timers: %w", err)
	}
	return nil
}

func (s System) deleteMarker() error {
	p, err := s.markerPath()
	if err != nil {
		return err
	}
	if err := os.Remove(p); err != nil && !errors.Is(err, fs.ErrNotExist) {
		return fmt.Errorf("delete the record of the dnf-automatic timers: %w", err)
	}
	return nil
}

// timerState is the answer of systemctl is-enabled.
type timerState int

const (
	timerAbsent timerState = iota
	timerEnabled
	timerDisabled
	timerMasked
	// timerOther: static, indirect, generated, linked and the like: not something the agent turns off.
	timerOther
)

func (s System) timerState(ctx context.Context, timer string) (timerState, error) {
	if s.Runner == nil {
		return timerAbsent, errors.New("systemctl is not available")
	}
	out, _, err := s.Runner.Run(ctx, "systemctl", "is-enabled", timer)
	if err != nil {
		return timerAbsent, fmt.Errorf("systemctl is-enabled %s: %w", timer, err)
	}
	word, _, _ := strings.Cut(strings.TrimSpace(out), "\n")
	switch strings.TrimSpace(word) {
	case "enabled", "enabled-runtime":
		return timerEnabled, nil
	case "disabled":
		return timerDisabled, nil
	case "masked", "masked-runtime":
		return timerMasked, nil
	case "static", "indirect", "generated", "transient", "linked", "linked-runtime", "alias":
		return timerOther, nil
	}
	// "not-found", or nothing on standard output with an error on standard error: the timer does not exist.
	return timerAbsent, nil
}

func (s System) systemctl(ctx context.Context, args ...string) error {
	out, code, err := s.Runner.Run(ctx, "systemctl", args...)
	if err != nil {
		return fmt.Errorf("systemctl %s: %w", strings.Join(args, " "), err)
	}
	if code != 0 {
		return fmt.Errorf("systemctl %s ended with exit code %d: %s", strings.Join(args, " "), code, strings.TrimSpace(out))
	}
	return nil
}

func (s System) applyTimers(ctx context.Context) (string, bool, error) {
	recorded, markerExists, err := s.readMarker()
	if err != nil {
		if markerExists {
			// Never disable more while the record of what the agent disabled is unreadable.
			return StateKept, false, err
		}
		return "", false, err
	}
	changed := false
	maskedByOthers := false
	for _, timer := range Timers {
		state, err := s.timerState(ctx, timer)
		if err != nil {
			return "", changed, err
		}
		switch state {
		case timerEnabled:
			// Recorded first: when the disable fails after it, the next run tries again, and an undo enables a timer that is
			// enabled already, which changes nothing.
			if !slices.Contains(recorded, timer) {
				recorded = append(recorded, timer)
				if err := s.writeMarker(recorded); err != nil {
					return "", changed, err
				}
			}
			if err := s.systemctl(ctx, "disable", "--now", timer); err != nil {
				return "", changed, fmt.Errorf("turn dnf-automatic off: %w", err)
			}
			changed = true
		case timerMasked:
			if !slices.Contains(recorded, timer) {
				maskedByOthers = true
			}
		}
	}
	switch {
	case len(recorded) > 0:
		return StateTurnedOff, changed, nil
	case maskedByOthers:
		return StateKept, changed, nil
	}
	return StateNone, changed, nil
}

// enableRecorded enables the timers the agent disabled and deletes the record. A recorded timer that is enabled again, masked or
// gone since is left as it is: somebody else decided about it.
func (s System) enableRecorded(ctx context.Context) (bool, error) {
	recorded, markerExists, err := s.readMarker()
	if err != nil || !markerExists {
		return false, err
	}
	changed := false
	for i, timer := range recorded {
		state, err := s.timerState(ctx, timer)
		if err == nil && state == timerDisabled {
			err = s.systemctl(ctx, "enable", "--now", timer)
			if err == nil {
				changed = true
			}
		}
		if err != nil {
			// Keep what is left, so the next run tries again.
			if werr := s.writeMarker(recorded[i:]); werr != nil {
				err = errors.Join(err, werr)
			}
			return changed, fmt.Errorf("turn dnf-automatic back on: %w", err)
		}
	}
	if err := s.deleteMarker(); err != nil {
		return changed, err
	}
	return true, nil
}

// writeAtomic writes a file through a temporary file in the same directory and a rename, so a reader never sees half of it.
func writeAtomic(path string, data []byte, mode os.FileMode) error {
	dir := filepath.Dir(path)
	f, err := os.CreateTemp(dir, "."+filepath.Base(path)+".*")
	if err != nil {
		return err
	}
	tmp := f.Name()
	defer os.Remove(tmp)
	w := bufio.NewWriter(f)
	if _, err := w.Write(data); err != nil {
		_ = f.Close()
		return err
	}
	if err := w.Flush(); err != nil {
		_ = f.Close()
		return err
	}
	if err := f.Sync(); err != nil {
		_ = f.Close()
		return err
	}
	if err := f.Close(); err != nil {
		return err
	}
	if err := os.Chmod(tmp, mode); err != nil {
		return err
	}
	return os.Rename(tmp, path)
}
