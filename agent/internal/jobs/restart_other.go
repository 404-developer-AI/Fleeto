//go:build !windows && !linux

package jobs

import (
	"context"
	"errors"
)

func osRequestRestart(context.Context) error {
	return errors.New("Fleeto restarts only Windows and Linux endpoints")
}

func osCancelRestart() error { return nil }
