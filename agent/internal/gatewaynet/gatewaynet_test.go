package gatewaynet

import (
	"context"
	"net"
	"runtime"
	"testing"
	"time"
)

// The connections to the gateway carry the user timeout, so a dead path is given up within it.
func TestDialerSetsTheUserTimeout(t *testing.T) {
	if runtime.GOOS != "linux" && runtime.GOOS != "windows" {
		t.Skip("the agent runs on Linux and Windows only")
	}
	listener, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer listener.Close()
	go func() {
		if conn, err := listener.Accept(); err == nil {
			defer conn.Close()
			time.Sleep(time.Second)
		}
	}()

	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	conn, err := Dialer().DialContext(ctx, "tcp", listener.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	defer conn.Close()
	raw, err := conn.(*net.TCPConn).SyscallConn()
	if err != nil {
		t.Fatal(err)
	}
	var got time.Duration
	var getErr error
	if err := raw.Control(func(fd uintptr) { got, getErr = userTimeout(fd) }); err != nil {
		t.Fatal(err)
	}
	if getErr != nil {
		t.Fatal(getErr)
	}
	if got != UserTimeout {
		t.Fatalf("user timeout = %v, want %v", got, UserTimeout)
	}
}

// The transport dials with the dialer of this package and never through a proxy.
func TestTransportDialsWithTheUserTimeout(t *testing.T) {
	transport := Transport(nil, 30*time.Second)
	if transport.DialContext == nil || transport.Proxy != nil || transport.TLSHandshakeTimeout != 30*time.Second {
		t.Fatal("the transport must dial with the user timeout, without proxy and with the handshake timeout")
	}
}
