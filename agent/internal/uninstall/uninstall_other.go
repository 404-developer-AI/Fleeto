//go:build !windows && !linux

package uninstall

import "errors"

// startDetached: the agent runs as a service on Windows and Linux only, so there is nothing installed to remove here.
func startDetached(string) error {
	return errors.New("removing the agent is not supported on this platform")
}
