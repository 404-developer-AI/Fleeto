//go:build !windows

package checks

import (
	"context"
	"fmt"
	"net"
	"os"
	"time"

	"golang.org/x/net/icmp"
	"golang.org/x/net/ipv4"
	"golang.org/x/net/ipv6"
)

var pingPayload = []byte("fleeto-agent-ping")

// pingOnce sends one ICMP echo request. As root it uses a raw ICMP socket; otherwise an unprivileged ICMP datagram socket
// (macOS, and Linux when net.ipv4.ping_group_range allows it).
func pingOnce(ctx context.Context, ip net.IP, seq uint16, timeout time.Duration) (time.Duration, error) {
	v4 := ip.To4() != nil
	network, address := "udp6", "::"
	proto := 58
	var echoType icmp.Type = ipv6.ICMPTypeEchoRequest
	var replyType icmp.Type = ipv6.ICMPTypeEchoReply
	if v4 {
		network, address, proto = "udp4", "0.0.0.0", 1
		echoType, replyType = ipv4.ICMPTypeEcho, ipv4.ICMPTypeEchoReply
	}
	privileged := os.Geteuid() == 0
	if privileged {
		if v4 {
			network = "ip4:icmp"
		} else {
			network = "ip6:ipv6-icmp"
		}
	}
	conn, err := icmp.ListenPacket(network, address)
	if err != nil {
		return 0, fmt.Errorf("open an ICMP socket: %w", err)
	}
	defer conn.Close()

	id := os.Getpid() & 0xffff
	msg := icmp.Message{Type: echoType, Body: &icmp.Echo{ID: id, Seq: int(seq), Data: pingPayload}}
	data, err := msg.Marshal(nil)
	if err != nil {
		return 0, err
	}
	var dst net.Addr = &net.IPAddr{IP: ip}
	if !privileged {
		dst = &net.UDPAddr{IP: ip}
	}
	deadline := time.Now().Add(timeout)
	if d, ok := ctx.Deadline(); ok && d.Before(deadline) {
		deadline = d
	}
	_ = conn.SetDeadline(deadline)
	start := time.Now()
	if _, err := conn.WriteTo(data, dst); err != nil {
		return 0, fmt.Errorf("send the echo request: %w", err)
	}
	buf := make([]byte, 1500)
	for {
		n, peer, err := conn.ReadFrom(buf)
		if err != nil {
			return 0, errNoReply
		}
		reply, err := icmp.ParseMessage(proto, buf[:n])
		if err != nil || reply.Type != replyType {
			continue
		}
		echo, ok := reply.Body.(*icmp.Echo)
		// An unprivileged socket gets a kernel-chosen id, so only the sequence and the peer are compared then.
		if !ok || echo.Seq != int(seq) || (privileged && echo.ID != id) || !samePeer(peer, ip) {
			continue
		}
		return time.Since(start), nil
	}
}

func samePeer(peer net.Addr, ip net.IP) bool {
	switch p := peer.(type) {
	case *net.IPAddr:
		return p.IP.Equal(ip)
	case *net.UDPAddr:
		return p.IP.Equal(ip)
	default:
		return false
	}
}
