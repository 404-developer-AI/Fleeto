//go:build !windows && !linux

package svcctl

import (
	"context"
	"time"
)

// unsupported is the controller of platforms without service support: everything but Windows and Linux.
type unsupported struct{}

// New returns the controller of this platform.
func New() Controller { return unsupported{} }

func (unsupported) Query(string) (State, error)                              { return StateUnknown, ErrUnsupported }
func (unsupported) Start(string) error                                       { return ErrUnsupported }
func (unsupported) Stop(context.Context, string, time.Duration) error        { return ErrUnsupported }
func (unsupported) WaitRunning(context.Context, string, time.Duration) error { return ErrUnsupported }

// Create creates a service.
func Create(Definition) error { return ErrUnsupported }

// Delete deletes a service.
func Delete(context.Context, string, time.Duration) error { return ErrUnsupported }
