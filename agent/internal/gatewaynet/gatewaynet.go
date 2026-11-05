// Package gatewaynet dials the TCP connections of the agent and the watchdog to the gateway, so a connection whose path died is
// given up within UserTimeout instead of after the retransmissions of the operating system.
//
// A live migration of a VM to a host behind another NAT, a failover to another internet line or a new public address keeps the
// connection open on the endpoint while nothing reaches the gateway any more, and no reset comes back. Linux then retransmits for
// about 15 minutes (tcp_retries2) before the read fails (found 2026-11-05 on a VM moved between Proxmox nodes). Data that stays
// unacknowledged for UserTimeout now ends the connection, and the agent connects again at once.
package gatewaynet

import (
	"crypto/tls"
	"net"
	"net/http"
	"syscall"
	"time"
)

// UserTimeout is how long sent data may stay unacknowledged before the connection is given up. It equals the time the gateway
// waits for an agent with the default heartbeat of 30 seconds (three heartbeats and 30 seconds) before it drops the session, so
// the agent does not give up a connection the gateway still counts as alive, and adds no reconnects that would not happen anyway.
// The heartbeat keeps data in flight, so a dead path is noticed within a heartbeat interval plus UserTimeout.
const UserTimeout = 2 * time.Minute

// Dialer returns a dialer whose connections carry UserTimeout.
func Dialer() *net.Dialer {
	return &net.Dialer{Control: control}
}

// Transport returns the HTTP transport of a WebSocket to the gateway: no proxy, the given TLS configuration and handshake
// timeout, and connections dialled by Dialer.
func Transport(tlsConfig *tls.Config, handshakeTimeout time.Duration) *http.Transport {
	return &http.Transport{
		Proxy:               nil,
		DialContext:         Dialer().DialContext,
		TLSClientConfig:     tlsConfig,
		TLSHandshakeTimeout: handshakeTimeout,
	}
}

func control(_, _ string, raw syscall.RawConn) error {
	// A system that refuses the option keeps the connection as it was before; the dial never fails for it.
	return raw.Control(func(fd uintptr) { _ = setUserTimeout(fd, UserTimeout) })
}
