//go:build windows

package remote

import (
	"context"
	"errors"
	"fmt"
	"time"

	"golang.org/x/sys/windows/svc"
	"golang.org/x/sys/windows/svc/mgr"
)

func openService(name string) (*mgr.Mgr, *mgr.Service, error) {
	m, err := mgr.Connect()
	if err != nil {
		return nil, nil, fmt.Errorf("the service manager could not be opened: %w", err)
	}
	s, err := m.OpenService(name)
	if err != nil {
		m.Disconnect()
		return nil, nil, fmt.Errorf("service %s could not be opened: %w", name, err)
	}
	return m, s, nil
}

func startService(ctx context.Context, name string) error {
	m, s, err := openService(name)
	if err != nil {
		return err
	}
	defer m.Disconnect()
	defer s.Close()
	if err := s.Start(); err != nil {
		return fmt.Errorf("%s could not be started: %w", name, err)
	}
	return waitState(ctx, s, svc.Running)
}

func stopService(ctx context.Context, name string) error {
	m, s, err := openService(name)
	if err != nil {
		return err
	}
	defer m.Disconnect()
	defer s.Close()
	status, err := s.Control(svc.Stop)
	if err != nil {
		return fmt.Errorf("%s could not be stopped: %w", name, err)
	}
	if status.State == svc.Stopped {
		return nil
	}
	return waitState(ctx, s, svc.Stopped)
}

func restartService(ctx context.Context, name string) error {
	if err := stopService(ctx, name); err != nil {
		return err
	}
	return startService(ctx, name)
}

func setStartType(_ context.Context, name, startType string) error {
	m, s, err := openService(name)
	if err != nil {
		return err
	}
	defer m.Disconnect()
	defer s.Close()
	config, err := s.Config()
	if err != nil {
		return fmt.Errorf("the configuration of %s could not be read: %w", name, err)
	}
	config.DelayedAutoStart = false
	switch startType {
	case "automatic":
		config.StartType = mgr.StartAutomatic
	case "automatic_delayed":
		config.StartType = mgr.StartAutomatic
		config.DelayedAutoStart = true
	case "manual":
		config.StartType = mgr.StartManual
	case "disabled":
		config.StartType = mgr.StartDisabled
	default:
		return errors.New("choose automatic, automatic (delayed), manual or disabled")
	}
	if err := s.UpdateConfig(config); err != nil {
		return fmt.Errorf("the start type of %s could not be changed: %w", name, err)
	}
	return nil
}

func serviceState(_ context.Context, name string) (string, error) {
	m, s, err := openService(name)
	if err != nil {
		return "", err
	}
	defer m.Disconnect()
	defer s.Close()
	status, err := s.Query()
	if err != nil {
		return "", err
	}
	switch status.State {
	case svc.Running:
		return "running", nil
	case svc.Stopped:
		return "stopped", nil
	case svc.StartPending, svc.ContinuePending:
		return "starting", nil
	case svc.StopPending, svc.PausePending:
		return "stopping", nil
	case svc.Paused:
		return "paused", nil
	default:
		return "", nil
	}
}

func waitState(ctx context.Context, s *mgr.Service, want svc.State) error {
	for {
		status, err := s.Query()
		if err != nil {
			return err
		}
		if status.State == want {
			return nil
		}
		select {
		case <-ctx.Done():
			return errors.New("the service did not reach the wanted state in time")
		case <-time.After(300 * time.Millisecond):
		}
	}
}
