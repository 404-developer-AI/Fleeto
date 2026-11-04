//go:build linux

package patch

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"os"
	"os/exec"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// InstallEngine is the engine that installs updates on this endpoint: apt where dpkg is the package database, dnf on the RHEL
// family; empty without either.
func InstallEngine() string {
	sys := linuxSystem{}
	switch {
	case sys.LookPath("apt-get") && sys.LookPath("dpkg-query"):
		return EngineApt
	case sys.LookPath("dnf") && sys.LookPath("rpm"):
		return EngineDnf
	}
	return ""
}

// Install scans, then installs the selection with apt or dnf. ctx stops the scan before it (a scan is safe to stop) and keeps an
// installation from starting after the agent stops; the package manager itself is started without a context and never stopped by
// the agent. When the agent service stops while it runs, systemd still ends the processes of the service (KillMode=mixed): the
// agent cannot keep a package manager alive past its own service, so it never chooses to stop one.
func Install(ctx context.Context, job *agentv1.PatchInstallJob, tool, lines io.Writer, logger *slog.Logger) InstallResult {
	return installLinux(ctx, linuxSystem{logger: logger}, job, newInstallOutput(tool, lines))
}

// Install runs a package manager transaction. It is an exec.Command without a context, so no timeout and no cancellation ever
// sends it a signal; it runs at the normal priority of the agent.
func (s linuxSystem) Install(env []string, out io.Writer, name string, args ...string) (int, error) {
	path, err := exec.LookPath(name)
	if err != nil {
		return -1, err
	}
	cmd := exec.Command(path, args...) // #nosec G204 -- fixed commands; arguments are validated package names and versions.
	cmd.Env = append(os.Environ(), env...)
	cmd.Stdin = nil
	cmd.Stdout, cmd.Stderr = out, out
	err = cmd.Run()
	var exit *exec.ExitError
	switch {
	case err == nil:
		return 0, nil
	case errors.As(err, &exit):
		return exit.ExitCode(), nil
	}
	return -1, err
}
