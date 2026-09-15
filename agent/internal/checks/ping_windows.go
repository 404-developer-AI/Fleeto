//go:build windows

package checks

import (
	"context"
	"encoding/binary"
	"fmt"
	"net"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// ICMP echo through the IP Helper API, which needs no administrator rights and no raw sockets.
var (
	iphlpapi            = windows.NewLazySystemDLL("iphlpapi.dll")
	procIcmpCreateFile  = iphlpapi.NewProc("IcmpCreateFile")
	procIcmp6CreateFile = iphlpapi.NewProc("Icmp6CreateFile")
	procIcmpCloseHandle = iphlpapi.NewProc("IcmpCloseHandle")
	procIcmpSendEcho    = iphlpapi.NewProc("IcmpSendEcho")
	procIcmp6SendEcho2  = iphlpapi.NewProc("Icmp6SendEcho2")
)

const ipSuccess = 0

var pingPayload = []byte("fleeto-agent-ping")

func pingOnce(_ context.Context, ip net.IP, _ uint16, timeout time.Duration) (time.Duration, error) {
	if ip4 := ip.To4(); ip4 != nil {
		return ping4(ip4, timeout)
	}
	return ping6(ip, timeout)
}

func ping4(ip net.IP, timeout time.Duration) (time.Duration, error) {
	handle, _, err := procIcmpCreateFile.Call()
	if windows.Handle(handle) == windows.InvalidHandle {
		return 0, fmt.Errorf("IcmpCreateFile: %w", err)
	}
	defer procIcmpCloseHandle.Call(handle)

	// ICMP_ECHO_REPLY: Address (4), Status (4), RoundTripTime (4), DataSize (2), Reserved (2), Data (pointer), Options (8 + pointer).
	reply := make([]byte, 128+len(pingPayload))
	address := binary.LittleEndian.Uint32(ip) // IPAddr is the address in network byte order as it lies in memory.
	n, _, _ := procIcmpSendEcho.Call(handle, uintptr(address),
		uintptr(unsafe.Pointer(&pingPayload[0])), uintptr(len(pingPayload)), 0,
		uintptr(unsafe.Pointer(&reply[0])), uintptr(len(reply)), uintptr(timeout.Milliseconds()))
	if n == 0 {
		// IP_REQ_TIMED_OUT, or the destination is unreachable: no reply either way.
		return 0, errNoReply
	}
	status := binary.LittleEndian.Uint32(reply[4:8])
	if status != ipSuccess {
		return 0, fmt.Errorf("%w (status %d)", errNoReply, status)
	}
	return time.Duration(binary.LittleEndian.Uint32(reply[8:12])) * time.Millisecond, nil
}

// sockaddrIn6 is SOCKADDR_IN6.
type sockaddrIn6 struct {
	family   uint16
	port     uint16
	flowInfo uint32
	addr     [16]byte
	scopeID  uint32
}

func ping6(ip net.IP, timeout time.Duration) (time.Duration, error) {
	handle, _, err := procIcmp6CreateFile.Call()
	if windows.Handle(handle) == windows.InvalidHandle {
		return 0, fmt.Errorf("Icmp6CreateFile: %w", err)
	}
	defer procIcmpCloseHandle.Call(handle)

	source := sockaddrIn6{family: windows.AF_INET6}
	destination := sockaddrIn6{family: windows.AF_INET6}
	copy(destination.addr[:], ip.To16())
	// ICMPV6_ECHO_REPLY: IPV6_ADDRESS_EX (packed, 26 bytes), 2 bytes of alignment, Status (4), RoundTripTime (4).
	reply := make([]byte, 128+len(pingPayload))
	n, _, _ := procIcmp6SendEcho2.Call(handle, 0, 0, 0,
		uintptr(unsafe.Pointer(&source)), uintptr(unsafe.Pointer(&destination)),
		uintptr(unsafe.Pointer(&pingPayload[0])), uintptr(len(pingPayload)), 0,
		uintptr(unsafe.Pointer(&reply[0])), uintptr(len(reply)), uintptr(timeout.Milliseconds()))
	if n == 0 {
		return 0, errNoReply
	}
	status := binary.LittleEndian.Uint32(reply[28:32])
	if status != ipSuccess {
		return 0, fmt.Errorf("%w (status %d)", errNoReply, status)
	}
	return time.Duration(binary.LittleEndian.Uint32(reply[32:36])) * time.Millisecond, nil
}
