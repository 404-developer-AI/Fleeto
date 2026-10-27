package agent

import (
	"context"
	"crypto/sha256"
	"crypto/tls"
	"encoding/hex"
	"errors"
	"net"
	"os"
	"runtime"

	"github.com/404-developer-AI/Fleeto/agent/internal/remote"
)

// The Proxmox web interface (0.8.0, PROXMOX-WEB.md). The target is fixed here, in the agent: pveproxy on the loopback interface of a
// Proxmox VE host. Nothing the server, the gateway, Fleeto Connect or the technician sends can name another host or port.
var proxmoxTargets = []string{"127.0.0.1:8006", "[::1]:8006"}

// proxmoxProxy is installed on every Proxmox VE host (package pve-manager).
const proxmoxProxy = "/usr/bin/pveproxy"

// proxmoxTunnel gives the tunnel of a Proxmox VE host, with the SHA-256 of the certificate pveproxy serves right now, read from a TLS
// handshake with it: Fleeto Connect pins the TLS of the session to that certificate.
func proxmoxTunnel(ctx context.Context) (*remote.Tunnel, error) {
	if runtime.GOOS != "linux" {
		return nil, remote.ErrNoTunnel
	}
	if _, err := os.Stat(proxmoxProxy); err != nil {
		return nil, remote.ErrNoTunnel
	}
	conn, err := dialProxmox(ctx)
	if err != nil {
		return nil, err
	}
	defer conn.Close()
	// Only to read the certificate; Fleeto Connect verifies it against what this returns. Nothing is sent over this connection.
	client := tls.Client(conn, &tls.Config{InsecureSkipVerify: true, MinVersion: tls.VersionTLS12}) //nolint:gosec // see above
	if err := client.HandshakeContext(ctx); err != nil {
		return nil, errors.New("pveproxy did not complete a TLS handshake on port 8006: " + err.Error())
	}
	certificates := client.ConnectionState().PeerCertificates
	if len(certificates) == 0 {
		return nil, errors.New("pveproxy presented no certificate on port 8006")
	}
	sum := sha256.Sum256(certificates[0].Raw)
	return &remote.Tunnel{Dial: dialProxmox, Certificate: hex.EncodeToString(sum[:])}, nil
}

func dialProxmox(ctx context.Context) (net.Conn, error) {
	var dialer net.Dialer
	var last error
	for _, target := range proxmoxTargets {
		conn, err := dialer.DialContext(ctx, "tcp", target)
		if err == nil {
			return conn, nil
		}
		last = err
	}
	return nil, errors.New("pveproxy does not listen on 127.0.0.1:8006 or [::1]:8006 (check LISTEN_IP in /etc/default/pveproxy): " + last.Error())
}
