//go:build !windows && !linux

package remote

import (
	"context"
	"errors"
)

var errServicesUnsupported = errors.New("services are not available on this operating system")

func startService(context.Context, string) error           { return errServicesUnsupported }
func stopService(context.Context, string) error            { return errServicesUnsupported }
func restartService(context.Context, string) error         { return errServicesUnsupported }
func setStartType(context.Context, string, string) error   { return errServicesUnsupported }
func serviceState(context.Context, string) (string, error) { return "", errServicesUnsupported }
