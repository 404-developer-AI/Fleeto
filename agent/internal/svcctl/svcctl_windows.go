//go:build windows

package svcctl

import (
	"context"
	"errors"
	"fmt"
	"time"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
)

// Windows is the Controller of the Windows service control manager.
type Windows struct{}

// New returns the controller of this platform.
func New() Controller { return Windows{} }

// Query returns the state, NotInstalled when the service does not exist and Disabled when its start type is disabled.
func (Windows) Query(name string) (State, error) {
	m, err := mgr.Connect()
	if err != nil {
		return StateUnknown, fmt.Errorf("open the service manager: %w", err)
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err != nil {
		if errors.Is(err, windows.ERROR_SERVICE_DOES_NOT_EXIST) {
			return StateNotInstalled, nil
		}
		return StateUnknown, fmt.Errorf("open service %s: %w", name, err)
	}
	defer s.Close()
	status, err := s.Query()
	if err != nil {
		return StateUnknown, fmt.Errorf("query service %s: %w", name, err)
	}
	if status.State == svc.Stopped {
		if config, err := s.Config(); err == nil && config.StartType == mgr.StartDisabled {
			return StateDisabled, nil
		}
	}
	switch status.State {
	case svc.Running:
		return StateRunning, nil
	case svc.Stopped:
		return StateStopped, nil
	case svc.StartPending, svc.ContinuePending:
		return StateStarting, nil
	case svc.StopPending, svc.PausePending, svc.Paused:
		return StateStopping, nil
	default:
		return StateUnknown, nil
	}
}

// Start asks the service manager to start the service.
func (Windows) Start(name string) error {
	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("open the service manager: %w", err)
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err != nil {
		return fmt.Errorf("open service %s: %w", name, err)
	}
	defer s.Close()
	if err := s.Start("run"); err != nil && !errors.Is(err, windows.ERROR_SERVICE_ALREADY_RUNNING) {
		return fmt.Errorf("start service %s: %w", name, err)
	}
	return nil
}

// Stop stops the service and waits until it is stopped.
func (w Windows) Stop(ctx context.Context, name string, timeout time.Duration) error {
	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("open the service manager: %w", err)
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err != nil {
		return fmt.Errorf("open service %s: %w", name, err)
	}
	defer s.Close()
	status, err := s.Query()
	if err != nil {
		return fmt.Errorf("query service %s: %w", name, err)
	}
	if status.State != svc.Stopped {
		if _, err := s.Control(svc.Stop); err != nil && !errors.Is(err, windows.ERROR_SERVICE_NOT_ACTIVE) {
			return fmt.Errorf("stop service %s: %w", name, err)
		}
	}
	return waitFor(ctx, s, svc.Stopped, timeout)
}

// WaitRunning waits until the service is running.
func (Windows) WaitRunning(ctx context.Context, name string, timeout time.Duration) error {
	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("open the service manager: %w", err)
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err != nil {
		return fmt.Errorf("open service %s: %w", name, err)
	}
	defer s.Close()
	return waitFor(ctx, s, svc.Running, timeout)
}

func waitFor(ctx context.Context, s *mgr.Service, want svc.State, timeout time.Duration) error {
	started := time.Now()
	deadline := started.Add(timeout)
	for {
		status, err := s.Query()
		if err != nil {
			return fmt.Errorf("query service %s: %w", s.Name, err)
		}
		if status.State == want {
			return nil
		}
		if want == svc.Running && status.State == svc.Stopped && time.Since(started) > 3*time.Second {
			// Started and stopped again: the service failed during start.
			return fmt.Errorf("service %s stopped right after it started (exit code %d)", s.Name, status.Win32ExitCode)
		}
		if time.Now().After(deadline) {
			return fmt.Errorf("service %s did not reach the expected state within %s", s.Name, timeout)
		}
		select {
		case <-ctx.Done():
			return ctx.Err()
		case <-time.After(300 * time.Millisecond):
		}
	}
}

// Create creates and starts an automatic LocalSystem service that the service manager restarts after a failure.
func Create(def Definition) error {
	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("open the service manager: %w", err)
	}
	defer m.Disconnect()
	s, err := m.CreateService(def.Name, def.Executable, mgr.Config{
		DisplayName:      def.DisplayName,
		Description:      def.Description,
		StartType:        mgr.StartAutomatic,
		ErrorControl:     mgr.ErrorNormal,
		ServiceStartName: "LocalSystem",
	}, def.Args...)
	if err != nil {
		return fmt.Errorf("create service %s: %w", def.Name, err)
	}
	defer s.Close()
	if err := s.SetRecoveryActions([]mgr.RecoveryAction{
		{Type: mgr.ServiceRestart, Delay: 10 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 30 * time.Second},
		{Type: mgr.ServiceRestart, Delay: 60 * time.Second},
	}, uint32((24 * time.Hour).Seconds())); err != nil {
		_ = s.Delete()
		return fmt.Errorf("set the recovery actions of %s: %w", def.Name, err)
	}
	if err := s.SetRecoveryActionsOnNonCrashFailures(true); err != nil {
		_ = s.Delete()
		return fmt.Errorf("set the recovery actions of %s: %w", def.Name, err)
	}
	return nil
}

// Delete stops the service when it runs and deletes it. A missing service is not an error.
func Delete(ctx context.Context, name string, timeout time.Duration) error {
	state, err := Windows{}.Query(name)
	if err != nil {
		return err
	}
	if state == StateNotInstalled {
		return nil
	}
	if state != StateStopped && state != StateDisabled {
		if err := (Windows{}).Stop(ctx, name, timeout); err != nil {
			return err
		}
	}
	m, err := mgr.Connect()
	if err != nil {
		return fmt.Errorf("open the service manager: %w", err)
	}
	defer m.Disconnect()
	s, err := m.OpenService(name)
	if err != nil {
		if errors.Is(err, windows.ERROR_SERVICE_DOES_NOT_EXIST) {
			return nil
		}
		return fmt.Errorf("open service %s: %w", name, err)
	}
	defer s.Close()
	if err := s.Delete(); err != nil && !errors.Is(err, windows.ERROR_SERVICE_MARKED_FOR_DELETE) {
		return fmt.Errorf("delete service %s: %w", name, err)
	}
	return nil
}
