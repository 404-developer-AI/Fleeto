//go:build !windows && !linux

package screen

import (
	"context"
	"time"
)

// SessionUser is not available off Windows.
func SessionUser(uint32) string { return "" }

// AskConsent is not available off Windows; remote control on Linux arrives with its own step.
func AskConsent(context.Context, uint32, string, time.Duration) (ConsentAnswer, error) {
	return ConsentRefused, ErrNotSupported
}

// StageFolder is not available off Windows.
func StageFolder(string, uint32) error { return ErrNotSupported }
