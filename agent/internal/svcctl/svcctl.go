// Package svcctl queries, starts, stops, creates and deletes operating system services (0.2.1). The agent and the watchdog use it to
// keep each other running and to install each other; it has no dependency on either, so both binaries can link it.
package svcctl

import (
	"context"
	"errors"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// State is the state of a service as far as keeping it running is concerned.
type State int

const (
	StateUnknown State = iota
	StateRunning
	StateStopped
	StateStarting
	StateStopping
	// StateDisabled: an administrator set the start type to disabled. Never started by the other service.
	StateDisabled
	StateNotInstalled
)

// ErrUnsupported is returned on platforms without service support yet.
var ErrUnsupported = errors.New("services are not supported on this platform yet")

// Controller is what the supervisor and the installer need from the service manager. Tests use a fake.
type Controller interface {
	Query(name string) (State, error)
	// Start asks the service manager to start the service; it does not wait.
	Start(name string) error
	// Stop stops the service and waits until it is stopped.
	Stop(ctx context.Context, name string, timeout time.Duration) error
	// WaitRunning waits until the service is running.
	WaitRunning(ctx context.Context, name string, timeout time.Duration) error
}

// Definition describes a service to create.
type Definition struct {
	Name        string
	DisplayName string
	Description string
	Executable  string
	Args        []string
}

// Proto maps a state to the protocol value reported in heartbeats.
func (s State) Proto() agentv1.ServiceState {
	switch s {
	case StateRunning:
		return agentv1.ServiceState_SERVICE_STATE_RUNNING
	case StateStopped:
		return agentv1.ServiceState_SERVICE_STATE_STOPPED
	case StateStarting:
		return agentv1.ServiceState_SERVICE_STATE_STARTING
	case StateStopping:
		return agentv1.ServiceState_SERVICE_STATE_STOPPING
	case StateDisabled:
		return agentv1.ServiceState_SERVICE_STATE_DISABLED
	case StateNotInstalled:
		return agentv1.ServiceState_SERVICE_STATE_NOT_INSTALLED
	default:
		return agentv1.ServiceState_SERVICE_STATE_UNSPECIFIED
	}
}

// String names a state for logs.
func (s State) String() string {
	switch s {
	case StateRunning:
		return "running"
	case StateStopped:
		return "stopped"
	case StateStarting:
		return "starting"
	case StateStopping:
		return "stopping"
	case StateDisabled:
		return "disabled"
	case StateNotInstalled:
		return "not installed"
	default:
		return "unknown"
	}
}
