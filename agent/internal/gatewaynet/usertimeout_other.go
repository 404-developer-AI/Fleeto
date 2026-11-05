//go:build !linux && !windows

package gatewaynet

import (
	"errors"
	"time"
)

var errUnsupported = errors.New("not supported on this platform")

// setUserTimeout does nothing where the agent does not run; the connection keeps the timeouts of the operating system.
func setUserTimeout(uintptr, time.Duration) error { return errUnsupported }

func userTimeout(uintptr) (time.Duration, error) { return 0, errUnsupported }
