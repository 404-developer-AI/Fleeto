//go:build !windows

package screen

import (
	"context"
	"io"
	"log/slog"
)

// HelperCommand is the argument of fleeto-agent that runs the helper.
const HelperCommand = "remote-helper"

// SessionIDEnv tells the helper which Windows session it serves.
const SessionIDEnv = "FLEETO_REMOTE_SESSION"

// WindowsLauncher is not available off Windows; remote control on Linux arrives with its own step.
func WindowsLauncher(*slog.Logger) Launcher {
	return func(context.Context, uint32) (Helper, error) { return nil, ErrNotSupported }
}

// RunHelper is not available off Windows.
func RunHelper(context.Context, io.Reader, io.Writer, uint32, *slog.Logger) error {
	return ErrNotSupported
}

// ConsoleSession is not available off Windows.
func ConsoleSession() uint32 { return 0 }

// SessionExists is not available off Windows.
func SessionExists(uint32) bool { return true }

// SecureAttention is not available off Windows.
func SecureAttention() error { return ErrNotSupported }

// EnableSoftwareSAS does nothing off Windows.
func EnableSoftwareSAS() (bool, error) { return false, nil }

// Supported reports whether this platform serves remote control.
func Supported() bool { return false }
