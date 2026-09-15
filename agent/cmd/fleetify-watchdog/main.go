// Command fleetify-watchdog is the Fleeto watchdog (0.2.1): the second service on an endpoint that keeps the agent running and installs
// its updates. The agent installs and provisions it; it is never installed by hand.
package main

import (
	"errors"
	"fmt"
	"io"
	"os"
	"runtime"
	"strings"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/update"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
	"github.com/404-developer-AI/Fleeto/agent/internal/watchdog"
)

const usage = `Fleeto Watchdog

Usage:
  fleetify-watchdog version [--short]
  fleetify-watchdog status
  fleetify-watchdog run                   (started by the service manager)

The Fleeto Agent installs, updates and removes the watchdog. Uninstall both with 'fleetify-agent uninstall'.
`

func main() {
	os.Exit(run(os.Args[1:], os.Stdout, os.Stderr))
}

func run(args []string, stdout, stderr io.Writer) int {
	if len(args) == 0 {
		if watchdog.IsService() {
			return serve(stderr)
		}
		fmt.Fprint(stderr, usage)
		return 2
	}
	switch args[0] {
	case "version", "--version", "-v":
		if len(args) > 1 && args[1] == "--short" {
			fmt.Fprintln(stdout, version.Version)
			return 0
		}
		fmt.Fprintf(stdout, "fleetify-watchdog %s (%s/%s)\n", version.Version, runtime.GOOS, runtime.GOARCH)
		return 0
	case "status":
		return status(stdout, stderr)
	case "run":
		if !watchdog.IsService() {
			fmt.Fprintln(stderr, "Error: 'run' is started by the service manager.")
			return 2
		}
		return serve(stderr)
	case "help", "--help", "-h", "/?":
		fmt.Fprint(stdout, usage)
		return 0
	default:
		fmt.Fprintf(stderr, "Unknown command %q.\n\n%s", args[0], usage)
		return 2
	}
}

func serve(stderr io.Writer) int {
	if err := watchdog.Serve(); err != nil {
		fmt.Fprintf(stderr, "Error: %v\n", err)
		return 1
	}
	return 0
}

func status(stdout, stderr io.Writer) int {
	dir := platform.WatchdogStateDir()
	fmt.Fprintf(stdout, "Fleeto Watchdog %s\n", version.Version)
	row(stdout, "State directory", dir)
	st, err := state.NewStore(dir, platform.AccessCurrentUser).Load()
	switch {
	case errors.Is(err, state.ErrNotEnrolled):
		row(stdout, "Identity", "not provisioned yet: the agent provisions it")
		return 0
	case errors.Is(err, os.ErrPermission):
		fmt.Fprintf(stderr, "Error: the watchdog state is readable by administrators only: %s\n", platform.ElevationHint)
		return 1
	case err != nil:
		fmt.Fprintf(stderr, "Error: %v\n", err)
		return 1
	}
	if st.Revoked {
		row(stdout, "Identity", "revoked: the agent provisions a new one")
	} else {
		row(stdout, "Identity", "provisioned")
	}
	row(stdout, "Endpoint id", st.EndpointID)
	if cert, err := st.Certificate(); err == nil {
		row(stdout, "Certificate expires", cert.NotAfter.UTC().Format(time.RFC3339))
	}
	if h, err := update.ReadHealth(dir); err == nil {
		connection := "not connected"
		if h.Connected {
			connection = "connected since " + h.ConnectedAt.UTC().Format(time.RFC3339)
		}
		row(stdout, "Gateway", connection)
	}
	if h, err := update.ReadHealth(platform.DefaultStateDir()); err == nil {
		row(stdout, "Agent", strings.TrimSpace(h.Version+" "+map[bool]string{true: "connected", false: "not connected"}[h.Connected]))
	}
	return 0
}

func row(w io.Writer, label, value string) {
	fmt.Fprintf(w, "  %-20s %s\n", label+":", value)
}
