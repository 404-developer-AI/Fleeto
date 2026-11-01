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

	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/remote"
)

// The web interfaces of the endpoint (0.8.0, PROXMOX-WEB.md). Every target is fixed here, in the agent, and chosen by the kind of the signed
// token: pveproxy on the loopback interface of a Proxmox VE host, and the default HTTPS port of DSM on a Synology NAS. Nothing the server,
// the gateway, Fleeto Connect or the technician sends can name another host or port.
var (
	proxmoxTargets  = []string{"127.0.0.1:8006", "[::1]:8006"}
	synologyTargets = []string{"127.0.0.1:5001", "[::1]:5001"}
)

// proxmoxProxy is installed on every Proxmox VE host (package pve-manager).
const proxmoxProxy = "/usr/bin/pveproxy"

// webTunnel gives the tunnel of the web interface a kind of session asks for.
func webTunnel(ctx context.Context, kind agentv1.RemoteSessionKind) (*remote.Tunnel, error) {
	switch kind {
	case agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_PROXMOX_WEB:
		return proxmoxTunnel(ctx)
	case agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_SYNOLOGY_WEB:
		return synologyTunnel(ctx)
	default:
		return nil, errors.New("this kind of session has no web interface")
	}
}

// proxmoxTunnel gives the tunnel of a Proxmox VE host, with the SHA-256 of the certificate pveproxy serves right now, read from a TLS
// handshake with it: Fleeto Connect pins the TLS of the session to that certificate.
func proxmoxTunnel(ctx context.Context) (*remote.Tunnel, error) {
	if runtime.GOOS != "linux" {
		return nil, remote.ErrNoTunnel
	}
	if _, err := os.Stat(proxmoxProxy); err != nil {
		return nil, remote.ErrNoTunnel
	}
	dial := fixedDialer(proxmoxTargets,
		"pveproxy does not listen on 127.0.0.1:8006 or [::1]:8006 (check LISTEN_IP in /etc/default/pveproxy)")
	certificate, err := targetCertificate(ctx, dial, "pveproxy", "8006")
	if err != nil {
		return nil, err
	}
	return &remote.Tunnel{Dial: dial, Certificate: certificate}, nil
}

// synologyTunnel gives the tunnel of a Synology NAS: the web interface of DSM on its default HTTPS port, 5001, and only there (decided
// 2026-11-01). A NAS whose admin moved DSM to another port, or switched HTTPS off, gets a message that says so.
func synologyTunnel(ctx context.Context) (*remote.Tunnel, error) {
	if runtime.GOOS != "linux" {
		return nil, remote.ErrNoSynologyTunnel
	}
	content, err := os.ReadFile(inventory.DSMVersionFile)
	if err != nil || inventory.DSMName(string(content)) == "" {
		return nil, remote.ErrNoSynologyTunnel
	}
	dial := fixedDialer(synologyTargets, "the Synology web interface only works on the default HTTPS port of DSM, 5001, and nothing "+
		"answers on 127.0.0.1:5001 or [::1]:5001 (check Control Panel, Login Portal, DSM: HTTPS port 5001)")
	certificate, err := targetCertificate(ctx, dial, "DSM", "5001")
	if err != nil {
		return nil, err
	}
	return &remote.Tunnel{Dial: dial, Certificate: certificate}, nil
}

// targetCertificate reads the SHA-256 of the certificate the target serves right now from a TLS handshake with it.
func targetCertificate(ctx context.Context, dial func(context.Context) (net.Conn, error), name, port string) (string, error) {
	conn, err := dial(ctx)
	if err != nil {
		return "", err
	}
	defer conn.Close()
	// Only to read the certificate; Fleeto Connect verifies it against what this returns. Nothing is sent over this connection.
	client := tls.Client(conn, &tls.Config{InsecureSkipVerify: true, MinVersion: tls.VersionTLS12}) //nolint:gosec // see above
	if err := client.HandshakeContext(ctx); err != nil {
		return "", errors.New(name + " did not complete a TLS handshake on port " + port + ": " + err.Error())
	}
	certificates := client.ConnectionState().PeerCertificates
	if len(certificates) == 0 {
		return "", errors.New(name + " presented no certificate on port " + port)
	}
	sum := sha256.Sum256(certificates[0].Raw)
	return hex.EncodeToString(sum[:]), nil
}

// fixedDialer connects to the first of the fixed targets that answers.
func fixedDialer(targets []string, problem string) func(context.Context) (net.Conn, error) {
	return func(ctx context.Context) (net.Conn, error) {
		var dialer net.Dialer
		var last error
		for _, target := range targets {
			conn, err := dialer.DialContext(ctx, "tcp", target)
			if err == nil {
				return conn, nil
			}
			last = err
		}
		return nil, errors.New(problem + ": " + last.Error())
	}
}
