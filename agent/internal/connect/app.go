package connect

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"net/http"
	"os"
	"path/filepath"
	"slices"
	"time"
)

// UI is what Fleeto Connect shows: one question, errors, and the browser. A Windows message box or zenity on Ubuntu.
type UI interface {
	Confirm(title, message string) bool
	Error(title, message string)
	OpenBrowser(url string) error
}

// ErrDeclined is returned when the technician did not allow an instance.
var ErrDeclined = errors.New("the technician did not allow the instance")

// App opens the Proxmox web interface for one link.
type App struct {
	Version  string
	Platform string
	UI       UI
	// ConfigDir holds the instances the technician allowed.
	ConfigDir string
	HTTP      *http.Client
	Logger    *slog.Logger
}

// Open runs one session: from the link to the end of the session. Every failure is shown to the technician with its cause.
func (a *App) Open(ctx context.Context, raw string) error {
	err := a.open(ctx, raw)
	if err != nil && !errors.Is(err, ErrDeclined) {
		a.Logger.Warn("the Proxmox web interface could not be opened", "error", err)
		a.UI.Error("Fleeto Connect", "The Proxmox web interface could not be opened.\n\n"+err.Error())
	}
	return err
}

func (a *App) open(ctx context.Context, raw string) error {
	link, err := ParseLink(raw)
	if err != nil {
		return err
	}
	// Any web page can open a fleeto-connect: link; only an instance the technician allowed may take Fleeto Connect anywhere.
	allowed, err := a.allowed(link.Instance)
	if err != nil {
		return err
	}
	if !allowed {
		if !a.UI.Confirm("Fleeto Connect", fmt.Sprintf("%s wants to open the web interface of a Proxmox VE host through Fleeto Connect.\n\n"+
			"Allow Fleeto Connect to connect to %s? Allow only the Fleeto instance of your organisation.", link.Instance, link.Instance)) {
			return ErrDeclined
		}
		if err := a.allow(link.Instance); err != nil {
			a.Logger.Warn("could not remember the allowed instance", "error", err)
		}
	}

	a.Logger.Info("claiming a session", "instance", link.Instance, "participant", link.ClaimID)
	ticket, private, err := Claim(ctx, a.HTTP, link, a.Version, a.Platform)
	if err != nil {
		return err
	}
	transport, err := DialRelay(ctx, a.HTTP, link, ticket)
	if err != nil {
		return err
	}
	session, err := Open(ctx, transport, ticket, private)
	if err != nil {
		_ = transport.Close("handshake failed")
		return err
	}
	proxy, err := NewProxy(session.Dial, session.Hello.TunnelCertificate, HostLabel(ticket.Endpoint, ticket.EndpointID), a.Logger)
	if err != nil {
		session.End("Fleeto Connect could not open a local address.")
		return err
	}
	go func() {
		if err := proxy.Serve(); err != nil {
			a.Logger.Warn("the local address stopped", "error", err)
		}
	}()
	runCtx, cancel := context.WithCancel(ctx)
	defer cancel()
	ended := make(chan string, 1)
	go func() { ended <- session.Run(runCtx) }()
	a.Logger.Info("session started", "endpoint", ticket.Endpoint, "address", proxy.Base)
	if err := a.UI.OpenBrowser(proxy.URL); err != nil {
		a.Logger.Warn("could not open the browser", "error", err)
	}

	var reason string
	select {
	case reason = <-ended:
	case <-ctx.Done():
		session.End("Fleeto Connect was stopped on the technician's PC.")
		reason = "stopped"
	}
	_ = proxy.Close()
	a.Logger.Info("session ended", "endpoint", ticket.Endpoint, "reason", reason)
	return nil
}

type instances struct {
	Allowed []string `json:"allowed"`
}

func (a *App) instancesFile() string { return filepath.Join(a.ConfigDir, "instances.json") }

func (a *App) allowed(instance string) (bool, error) {
	data, err := os.ReadFile(a.instancesFile())
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	if err != nil {
		return false, fmt.Errorf("Fleeto Connect cannot read its settings: %w", err)
	}
	var list instances
	if json.Unmarshal(data, &list) != nil {
		return false, nil
	}
	return slices.Contains(list.Allowed, instance), nil
}

func (a *App) allow(instance string) error {
	var list instances
	if data, err := os.ReadFile(a.instancesFile()); err == nil {
		_ = json.Unmarshal(data, &list)
	}
	if !slices.Contains(list.Allowed, instance) {
		list.Allowed = append(list.Allowed, instance)
	}
	if err := os.MkdirAll(a.ConfigDir, 0o700); err != nil {
		return err
	}
	data, _ := json.MarshalIndent(list, "", "  ")
	temp := a.instancesFile() + ".tmp"
	if err := os.WriteFile(temp, data, 0o600); err != nil {
		return err
	}
	return os.Rename(temp, a.instancesFile())
}

// NewHTTPClient is the client for the claim and the relay: the system's trust store, the proxy of the environment, and timeouts.
func NewHTTPClient() *http.Client {
	transport := http.DefaultTransport.(*http.Transport).Clone()
	transport.TLSHandshakeTimeout = 20 * time.Second
	transport.ResponseHeaderTimeout = claimWait
	return &http.Client{Transport: transport}
}
