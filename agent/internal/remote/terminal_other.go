//go:build !windows && !linux

package remote

import "errors"

// Shells lists the shells this endpoint offers: none, remote terminals run on Windows and Linux only.
func Shells() []string { return nil }

// PTYAvailable reports whether terminals run in a pseudo terminal.
func PTYAvailable() bool { return false }

// OpenTerminal is not supported on this platform.
func OpenTerminal(string, int, int) (Terminal, error) {
	return nil, errors.New("remote terminals are not supported on this operating system")
}
