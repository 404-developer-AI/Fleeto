//go:build !windows

package service

import (
	"context"
	"io"
)

// IsService reports whether the process was started by the service manager.
func IsService() bool { return false }

// Run runs the service entry point.
func Run() error { return ErrUnsupported }

// Install installs the service.
func Install(context.Context, InstallOptions, io.Writer) error { return ErrUnsupported }

// Uninstall removes the service.
func Uninstall(io.Writer) error { return ErrUnsupported }

// State returns the service state.
func State() (string, error) { return "", ErrUnsupported }
