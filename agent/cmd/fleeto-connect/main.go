// Command fleeto-connect is Fleeto Connect (0.8.0, PROXMOX-WEB.md): it opens the web interface of a Proxmox VE host from Fleeto through a
// remote session. The system starts it with a fleeto-connect: link from the Fleeto web UI. On Windows, starting it without a link installs it
// for the current user; on Ubuntu the .deb installs it.
package main

import (
	"context"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/signal"
	"path/filepath"
	"runtime"
	"strings"
	"syscall"

	"github.com/404-developer-AI/Fleeto/agent/internal/connect"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
)

const maxLogBytes = 1 << 20

func main() {
	os.Exit(run(os.Args[1:]))
}

func run(args []string) int {
	ui := connect.SystemUI{}
	logger := openLog()
	switch {
	case len(args) == 1 && (args[0] == "--version" || args[0] == "-v"):
		fmt.Println("Fleeto Connect " + version.Version)
		return 0
	case len(args) == 1 && args[0] == "--uninstall":
		if err := connect.Uninstall(); err != nil {
			ui.Error("Fleeto Connect", "Fleeto Connect could not be removed.\n\n"+err.Error())
			return 1
		}
		ui.Info("Fleeto Connect", "Fleeto Connect was removed.")
		return 0
	case len(args) == 0 || (len(args) == 1 && args[0] == "--install"):
		if runtime.GOOS != "windows" {
			fmt.Println("Fleeto Connect " + version.Version + " opens the Proxmox web interface from Fleeto. Open it from the Fleeto web UI.")
			return 0
		}
		if err := connect.Install(version.Version); err != nil {
			ui.Error("Fleeto Connect", "Fleeto Connect could not be installed.\n\n"+err.Error())
			return 1
		}
		ui.Info("Fleeto Connect", "Fleeto Connect "+version.Version+" is installed for your Windows account.\n\n"+
			"Go back to Fleeto and choose Try again to open the Proxmox web interface.")
		return 0
	case len(args) == 1 && strings.HasPrefix(strings.ToLower(args[0]), connect.Scheme+":"):
	default:
		ui.Error("Fleeto Connect", "Fleeto Connect opens the Proxmox web interface from the Fleeto web UI. Open it from there.")
		return 2
	}

	configDir, err := connect.ConfigDir()
	if err != nil {
		ui.Error("Fleeto Connect", "Fleeto Connect cannot find its settings folder: "+err.Error())
		return 1
	}
	ctx, stop := signal.NotifyContext(context.Background(), os.Interrupt, syscall.SIGTERM)
	defer stop()
	app := &connect.App{
		Version: version.Version, Platform: runtime.GOOS + "/" + runtime.GOARCH, UI: ui, ConfigDir: configDir,
		HTTP: connect.NewHTTPClient(), Logger: logger,
	}
	if err := app.Open(ctx, args[0]); err != nil {
		return 1
	}
	return 0
}

// openLog writes to fleeto-connect.log in the cache folder of the user, started again once it passes 1 MB. Never a secret: the claim of a
// link is logged by its id only.
func openLog() *slog.Logger {
	dir, err := connect.LogDir()
	if err != nil {
		return slog.New(slog.DiscardHandler)
	}
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return slog.New(slog.DiscardHandler)
	}
	path := filepath.Join(dir, "fleeto-connect.log")
	if info, err := os.Stat(path); err == nil && info.Size() > maxLogBytes {
		_ = os.Rename(path, path+".old")
	}
	file, err := os.OpenFile(path, os.O_CREATE|os.O_APPEND|os.O_WRONLY, 0o600)
	if err != nil {
		return slog.New(slog.DiscardHandler)
	}
	var out io.Writer = file
	return slog.New(slog.NewTextHandler(out, nil)).With("pid", os.Getpid())
}
