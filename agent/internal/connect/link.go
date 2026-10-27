// Package connect is Fleeto Connect (0.8.0, PROXMOX-WEB.md): the program on the technician's PC that opens the web interface of a
// Proxmox VE host through a remote session. Fleeto web starts it with a fleeto-connect: link; it claims the session with an ephemeral key
// of its own, opens the relay as the browser does for a remote session, and serves the browser on a loopback address, carrying every
// connection as a tunnel channel to pveproxy on the host. The TLS of pveproxy runs between Fleeto Connect and the host, pinned to the
// certificate the agent reported, inside the end-to-end encryption of the session.
package connect

import (
	"errors"
	"net"
	"net/url"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/remote"
)

// Scheme is the link scheme Fleeto web opens and the system hands to Fleeto Connect.
const Scheme = "fleeto-connect"

// Link is a parsed fleeto-connect: link: open?instance=<host>&claim=<participant>.<secret>.
type Link struct {
	// Instance is the host (and port, for development) of the instance's web UI.
	Instance string
	// Claim is "<participant>.<secret>" as web issued it; the secret is never logged.
	Claim string
	// ClaimID is the id before the dot, for messages and logs.
	ClaimID string
}

// ParseLink reads a fleeto-connect: link. It accepts only the open action with an instance host and a claim of the expected form.
func ParseLink(raw string) (Link, error) {
	if len(raw) > 2048 {
		return Link{}, errors.New("the link is too long")
	}
	u, err := url.Parse(raw)
	if err != nil || !strings.EqualFold(u.Scheme, Scheme) {
		return Link{}, errors.New("this is not a Fleeto Connect link")
	}
	action := u.Host
	if action == "" {
		action = strings.TrimPrefix(u.Opaque, "//")
		if i := strings.IndexAny(action, "/?"); i >= 0 {
			action = action[:i]
		}
	}
	if !strings.EqualFold(strings.Trim(action, "/"), "open") {
		return Link{}, errors.New("this Fleeto Connect link asks for something Fleeto Connect does not do; update Fleeto Connect")
	}
	query := u.Query()
	instance := strings.ToLower(query.Get("instance"))
	if !ValidInstance(instance) {
		return Link{}, errors.New("the link names no valid Fleeto instance")
	}
	claim := query.Get("claim")
	id, secret, ok := strings.Cut(claim, ".")
	if !ok || !remote.ValidID(id) || len(secret) < 40 || len(secret) > 100 || strings.Trim(secret, "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_") != "" {
		return Link{}, errors.New("the link carries no valid session claim; open the Proxmox web interface from Fleeto again")
	}
	return Link{Instance: instance, Claim: claim, ClaimID: strings.ToLower(id)}, nil
}

// ValidInstance accepts a DNS host name with an optional port, never a path, user or address literal other than localhost.
func ValidInstance(instance string) bool {
	host, port := instance, ""
	if h, p, err := net.SplitHostPort(instance); err == nil {
		host, port = h, p
	}
	if port != "" && (len(port) > 5 || strings.Trim(port, "0123456789") != "") {
		return false
	}
	if host == "localhost" {
		return true
	}
	if len(host) < 3 || len(host) > 253 || !strings.Contains(host, ".") || net.ParseIP(host) != nil {
		return false
	}
	for _, label := range strings.Split(host, ".") {
		if label == "" || len(label) > 63 || strings.HasPrefix(label, "-") || strings.HasSuffix(label, "-") ||
			strings.Trim(label, "abcdefghijklmnopqrstuvwxyz0123456789-") != "" {
			return false
		}
	}
	return true
}

// isLocal reports whether the instance is a development instance on this PC.
func (l Link) isLocal() bool {
	host := l.Instance
	if h, _, err := net.SplitHostPort(host); err == nil {
		host = h
	}
	return host == "localhost"
}
