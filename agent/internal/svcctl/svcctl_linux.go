//go:build linux

package svcctl

import (
	"bytes"
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"time"
)

// UnitDir holds the unit files Fleeto installs.
const UnitDir = "/etc/systemd/system"

// Systemd is the Controller of systemd, used on Linux (0.2.1). It drives systemctl rather than the D-Bus API: the agent needs no
// extra dependency, and systemctl is present on every supported distribution.
type Systemd struct{}

// New returns the controller of this platform.
func New() Controller { return Systemd{} }

// unitName is the unit of a service name, e.g. fleeto-agent.service.
func unitName(name string) string { return name + ".service" }

// systemctl runs systemctl with the given arguments and returns its standard output.
func systemctl(ctx context.Context, args ...string) (string, error) {
	path, err := exec.LookPath("systemctl")
	if err != nil {
		return "", fmt.Errorf("%w: systemctl was not found, so Fleeto cannot manage services on this system", ErrUnsupported)
	}
	if _, ok := ctx.Deadline(); !ok {
		var cancel context.CancelFunc
		ctx, cancel = context.WithTimeout(ctx, 2*time.Minute)
		defer cancel()
	}
	cmd := exec.CommandContext(ctx, path, args...) // #nosec G204 -- systemctl with arguments this package builds itself.
	var stdout, stderr bytes.Buffer
	cmd.Stdout = &stdout
	cmd.Stderr = &stderr
	if err := cmd.Run(); err != nil {
		detail := strings.TrimSpace(stderr.String())
		if detail == "" {
			detail = strings.TrimSpace(stdout.String())
		}
		if detail != "" {
			return stdout.String(), fmt.Errorf("systemctl %s: %w: %s", strings.Join(args, " "), err, detail)
		}
		return stdout.String(), fmt.Errorf("systemctl %s: %w", strings.Join(args, " "), err)
	}
	return stdout.String(), nil
}

// Query returns the state of the unit. A unit file that does not exist is NotInstalled; a disabled or masked unit that is not
// running is Disabled, so the other service leaves it alone.
func (Systemd) Query(name string) (State, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	// show never fails on an unknown unit: it answers LoadState=not-found.
	out, err := systemctl(ctx, "show", unitName(name), "--property=LoadState", "--property=ActiveState", "--property=UnitFileState")
	if err != nil {
		return StateUnknown, err
	}
	properties := map[string]string{}
	for _, line := range strings.Split(out, "\n") {
		if key, value, ok := strings.Cut(strings.TrimSpace(line), "="); ok {
			properties[key] = value
		}
	}
	switch properties["LoadState"] {
	case "not-found", "bad-setting", "error":
		return StateNotInstalled, nil
	case "masked":
		return StateDisabled, nil
	}
	switch properties["ActiveState"] {
	case "active", "reloading":
		return StateRunning, nil
	case "activating":
		return StateStarting, nil
	case "deactivating":
		return StateStopping, nil
	}
	// Not running: an administrator who disabled or masked the unit stopped it on purpose.
	switch properties["UnitFileState"] {
	case "disabled", "masked", "masked-runtime":
		return StateDisabled, nil
	}
	return StateStopped, nil
}

// Start asks systemd to start the unit without waiting for it.
func (Systemd) Start(name string) error {
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	_, err := systemctl(ctx, "start", "--no-block", unitName(name))
	return err
}

// Stop stops the unit and waits until it is stopped.
func (s Systemd) Stop(ctx context.Context, name string, timeout time.Duration) error {
	state, err := s.Query(name)
	if err != nil {
		return err
	}
	if state == StateNotInstalled {
		return nil
	}
	stopCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	if _, err := systemctl(stopCtx, "stop", unitName(name)); err != nil && stopCtx.Err() == nil {
		return err
	}
	return s.wait(ctx, name, timeout, func(state State) (bool, error) {
		return state == StateStopped || state == StateDisabled || state == StateNotInstalled, nil
	})
}

// WaitRunning waits until the unit runs. A unit that fails while starting ends the wait with its status.
func (s Systemd) WaitRunning(ctx context.Context, name string, timeout time.Duration) error {
	started := time.Now()
	return s.wait(ctx, name, timeout, func(state State) (bool, error) {
		if state == StateRunning {
			return true, nil
		}
		if (state == StateStopped || state == StateDisabled) && time.Since(started) > 3*time.Second {
			return false, fmt.Errorf("the %s service stopped right after it started: %s", name, s.failureDetail(ctx, name))
		}
		return false, nil
	})
}

func (s Systemd) wait(ctx context.Context, name string, timeout time.Duration, done func(State) (bool, error)) error {
	deadline := time.Now().Add(timeout)
	for {
		state, err := s.Query(name)
		if err != nil {
			return err
		}
		ok, err := done(state)
		if err != nil {
			return err
		}
		if ok {
			return nil
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("the %s service did not reach the expected state within %s (current: %s)", name, timeout, state)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(300 * time.Millisecond):
		}
	}
}

// failureDetail is the Result and the last log lines of a unit, to explain a failed start.
func (Systemd) failureDetail(ctx context.Context, name string) string {
	detailCtx, cancel := context.WithTimeout(ctx, 15*time.Second)
	defer cancel()
	out, err := systemctl(detailCtx, "show", unitName(name), "--property=Result", "--property=ExecMainStatus")
	if err != nil {
		return "see journalctl -u " + unitName(name)
	}
	fields := strings.Join(strings.Fields(out), " ")
	return strings.TrimSpace(fields) + "; see journalctl -u " + unitName(name)
}

// Create writes the unit file, enables it so it starts at boot, and lets systemd restart it after a failure. It does not start the
// service; the caller does that.
func Create(def Definition) error {
	if err := os.MkdirAll(UnitDir, 0o755); err != nil {
		return fmt.Errorf("create %s: %w", UnitDir, err)
	}
	path := filepath.Join(UnitDir, unitName(def.Name))
	if err := os.WriteFile(path, []byte(UnitFile(def)), 0o644); err != nil { // #nosec G306 -- a unit file is world-readable by design.
		return fmt.Errorf("write %s: %w", path, err)
	}
	ctx, cancel := context.WithTimeout(context.Background(), time.Minute)
	defer cancel()
	if _, err := systemctl(ctx, "daemon-reload"); err != nil {
		_ = os.Remove(path)
		return err
	}
	if _, err := systemctl(ctx, "enable", unitName(def.Name)); err != nil {
		_ = os.Remove(path)
		_, _ = systemctl(ctx, "daemon-reload")
		return err
	}
	return nil
}

// UnitFile is the systemd unit of a service definition. Exported for tests.
func UnitFile(def Definition) string {
	command := def.Executable
	for _, arg := range def.Args {
		command += " " + arg
	}
	var b strings.Builder
	b.WriteString("# Written by fleeto-agent. Changes are overwritten when the agent is installed again.\n")
	b.WriteString("[Unit]\n")
	fmt.Fprintf(&b, "Description=%s\n", def.Description)
	b.WriteString("Documentation=https://github.com/404-developer-AI/Fleeto\n")
	b.WriteString("After=network-online.target\n")
	b.WriteString("Wants=network-online.target\n\n")
	b.WriteString("[Service]\n")
	b.WriteString("Type=simple\n")
	fmt.Fprintf(&b, "ExecStart=%s\n", command)
	// The agent runs checks, scripts and installers as root, so it gets no sandbox. Restart=always keeps it running; the other
	// Fleeto service starts it again when systemd gives up.
	b.WriteString("Restart=always\n")
	b.WriteString("RestartSec=10s\n")
	b.WriteString("TimeoutStopSec=30s\n")
	b.WriteString("KillMode=mixed\n")
	b.WriteString("LimitNOFILE=65535\n\n")
	b.WriteString("[Install]\n")
	b.WriteString("WantedBy=multi-user.target\n")
	return b.String()
}

// Delete stops and disables the service and removes its unit file. A service that does not exist is not an error.
func Delete(ctx context.Context, name string, timeout time.Duration) error {
	state, err := Systemd{}.Query(name)
	if err != nil && !errors.Is(err, ErrUnsupported) {
		return err
	}
	if errors.Is(err, ErrUnsupported) || state == StateNotInstalled {
		return removeUnitFile(ctx, name)
	}
	if state != StateStopped && state != StateDisabled {
		if err := (Systemd{}).Stop(ctx, name, timeout); err != nil {
			return err
		}
	}
	disableCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	// A unit without [Install] symlinks is "not enabled"; systemctl reports that as an error the removal does not care about.
	_, _ = systemctl(disableCtx, "disable", unitName(name))
	return removeUnitFile(ctx, name)
}

func removeUnitFile(ctx context.Context, name string) error {
	path := filepath.Join(UnitDir, unitName(name))
	if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return fmt.Errorf("remove %s: %w", path, err)
	} else if err == nil {
		reloadCtx, cancel := context.WithTimeout(ctx, time.Minute)
		defer cancel()
		if _, err := systemctl(reloadCtx, "daemon-reload"); err != nil {
			return err
		}
	}
	return nil
}
