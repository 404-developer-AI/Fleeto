package gatewaynet

import (
	"time"

	"golang.org/x/sys/unix"
)

// setUserTimeout sets TCP_USER_TIMEOUT: the milliseconds sent data may stay unacknowledged before the kernel ends the connection.
func setUserTimeout(fd uintptr, timeout time.Duration) error {
	return unix.SetsockoptInt(int(fd), unix.IPPROTO_TCP, unix.TCP_USER_TIMEOUT, int(timeout.Milliseconds()))
}

// userTimeout reads TCP_USER_TIMEOUT back, for the tests.
func userTimeout(fd uintptr) (time.Duration, error) {
	ms, err := unix.GetsockoptInt(int(fd), unix.IPPROTO_TCP, unix.TCP_USER_TIMEOUT)
	return time.Duration(ms) * time.Millisecond, err
}
