package gatewaynet

import (
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// tcpMaxRT is TCP_MAXRT of ws2ipdef.h: the seconds a segment is retransmitted before Windows ends the connection.
const tcpMaxRT = 5

// setUserTimeout sets TCP_MAXRT, the Windows counterpart of TCP_USER_TIMEOUT.
func setUserTimeout(fd uintptr, timeout time.Duration) error {
	return windows.SetsockoptInt(windows.Handle(fd), windows.IPPROTO_TCP, tcpMaxRT, int(timeout.Seconds()))
}

// userTimeout reads TCP_MAXRT back, for the tests.
func userTimeout(fd uintptr) (time.Duration, error) {
	var seconds int32
	size := int32(4)
	err := windows.Getsockopt(windows.Handle(fd), windows.IPPROTO_TCP, tcpMaxRT, (*byte)(unsafe.Pointer(&seconds)), &size)
	return time.Duration(seconds) * time.Second, err
}
