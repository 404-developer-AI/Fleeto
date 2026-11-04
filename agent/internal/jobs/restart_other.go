//go:build !windows && !linux

package jobs

import (
	"context"
	"errors"
	"time"
)

func osRequestRestart(context.Context) error {
	return errors.New("Fleeto restarts only Windows and Linux endpoints")
}

func osCancelRestart() error { return nil }

func osRequestRestartAfter(context.Context, time.Duration, string) error {
	return errors.New("Fleeto restarts only Windows and Linux endpoints")
}
