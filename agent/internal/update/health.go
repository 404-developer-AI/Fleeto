// Package update installs agent and watchdog binaries from a verified release (0.2.1): download over mTLS from the gateway, verification
// against the signed manifest, replacement of a service binary with automatic rollback, and the small files the agent and the watchdog
// use to see each other: a health file per service and a journal that survives a crash in the middle of a replacement.
package update

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

// HealthFileName is the health file inside a service's state directory.
const HealthFileName = "health.json"

// Health is what a running service tells the other one about itself. Both services run as SYSTEM (root) and read each other's state
// directory; nothing in it is secret.
type Health struct {
	Component string `json:"component"`
	Version   string `json:"version"`
	PID       int    `json:"pid"`
	Connected bool   `json:"connected"`
	// ConnectedAt is when the current gateway session was accepted (HelloAck); zero while not connected.
	ConnectedAt time.Time `json:"connectedAt,omitzero"`
	UpdatedAt   time.Time `json:"updatedAt"`
}

// WriteHealth replaces the health file atomically.
func WriteHealth(dir string, access platform.Access, h Health) error {
	data, err := json.Marshal(h)
	if err != nil {
		return err
	}
	return platform.WriteFileAtomic(filepath.Join(dir, HealthFileName), data, access)
}

// ReadHealth reads the health file of a service. A missing file returns os.ErrNotExist.
func ReadHealth(dir string) (Health, error) {
	var h Health
	data, err := os.ReadFile(filepath.Join(dir, HealthFileName))
	if err != nil {
		return h, err
	}
	if len(data) > 64*1024 {
		return h, errors.New("the health file is too large")
	}
	if err := json.Unmarshal(data, &h); err != nil {
		return h, fmt.Errorf("the health file is damaged: %w", err)
	}
	return h, nil
}

// HealthWait describes when a newly installed service counts as healthy: it reports the expected version and a gateway session
// accepted after the service was started.
type HealthWait struct {
	Dir     string
	Version string
	Since   time.Time
	// Timeout is how long the service gets while the installer itself is connected to the gateway.
	Timeout time.Duration
	// MaxTimeout bounds the wait when the installer is not connected either: then a missing connection says nothing about the new version.
	MaxTimeout time.Duration
	// InstallerConnected reports whether the installing service has a gateway session now.
	InstallerConnected func() bool
	Interval           time.Duration
	Now                func() time.Time
	Sleep              func(time.Duration)
}

// ErrNotHealthy is returned when the new version did not report a connection in time.
var ErrNotHealthy = errors.New("the new version did not connect to the gateway in time")

// WaitHealthy blocks until the service is healthy, the time is up or stop is closed.
func WaitHealthy(w HealthWait, stop <-chan struct{}) error {
	if w.Interval <= 0 {
		w.Interval = 5 * time.Second
	}
	if w.Now == nil {
		w.Now = time.Now
	}
	start := w.Now()
	var disconnected time.Duration
	last := start
	for {
		if h, err := ReadHealth(w.Dir); err == nil && h.Version == w.Version && h.Connected && !h.ConnectedAt.Before(w.Since) {
			return nil
		}
		now := w.Now()
		if w.InstallerConnected != nil && !w.InstallerConnected() {
			disconnected += now.Sub(last)
		}
		last = now
		elapsed := now.Sub(start)
		// Time the installer spent without a connection does not count against the new version, up to MaxTimeout in total.
		if elapsed-disconnected >= w.Timeout || elapsed >= w.MaxTimeout {
			return ErrNotHealthy
		}
		if w.Sleep != nil {
			w.Sleep(w.Interval)
			continue
		}
		select {
		case <-stop:
			return errors.New("stopped while waiting for the new version")
		case <-time.After(w.Interval):
		}
	}
}
