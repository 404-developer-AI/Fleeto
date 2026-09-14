// Package safego runs functions so that a panic is logged instead of crashing the agent.
package safego

import (
	"fmt"
	"log/slog"
	"runtime/debug"
)

// Go starts fn in a goroutine. A panic is recovered and logged with its stack.
func Go(logger *slog.Logger, name string, fn func()) {
	go func() {
		defer Recover(logger, name)
		fn()
	}()
}

// Recover must be deferred directly. It logs a recovered panic with its stack trace.
func Recover(logger *slog.Logger, name string) {
	if r := recover(); r != nil {
		logger.Error("recovered from a panic", "component", name, "panic", fmt.Sprint(r), "stack", string(debug.Stack()))
	}
}

// Call runs fn and converts a panic into an error, so a single bad message or check cannot stop a loop.
func Call(logger *slog.Logger, name string, fn func() error) (err error) {
	defer func() {
		if r := recover(); r != nil {
			logger.Error("recovered from a panic", "component", name, "panic", fmt.Sprint(r), "stack", string(debug.Stack()))
			err = fmt.Errorf("%s: internal error: %v", name, r)
		}
	}()
	return fn()
}
