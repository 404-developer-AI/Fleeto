// Package svcctl queries, starts, stops, creates and deletes operating system services (0.2.1). The agent and the watchdog use it to
// keep each other running and to install each other; it has no dependency on either, so both binaries can link it.
package svcctl

import (
	"context"
	"errors"
	"strings"
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

// InUnitCgroup reports whether /proc/self/cgroup content places the process in the control group of the unit of the service name,
// in the systemd hierarchy (cgroup v1, name=systemd) or the unified one (cgroup v2). Exported for tests.
func InUnitCgroup(content, name string) bool {
	suffix := "/" + name + ".service"
	for _, line := range strings.Split(content, "\n") {
		parts := strings.SplitN(strings.TrimSpace(line), ":", 3)
		if len(parts) != 3 || (parts[1] != "name=systemd" && !(parts[0] == "0" && parts[1] == "")) {
			continue
		}
		if strings.HasSuffix(parts[2], suffix) {
			return true
		}
	}
	return false
}
