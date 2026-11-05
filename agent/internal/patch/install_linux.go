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
// the agent.
//
// When the agent runs under systemd, the package manager runs in a transient scope of its own (systemd-run --scope), outside the
// cgroup of the agent service: a stop of the service (KillMode=mixed, at an agent update for one) then no longer ends apt or dnf
// mid-transaction (PATCHING.md section 15). Its output goes to a file in the job's directory that the agent reads, never to a pipe
// that would break when the agent stops.
func Install(ctx context.Context, job *agentv1.PatchInstallJob, opts InstallOptions, tool, lines io.Writer, logger *slog.Logger) InstallResult {
	sys := linuxSystem{logger: logger, jobID: opts.JobID, outputDir: opts.OutputDir}
	return installLinux(ctx, sys, job, newInstallOutput(tool, lines))
}

// systemdRun returns the path of systemd-run when the agent runs under systemd and it is installed.
func systemdRun() (string, bool) {
	_, err := os.Stat("/run/systemd/system")
	if !underSystemd(os.Getenv("INVOCATION_ID"), err == nil) {
		return "", false
	}
	path, err := exec.LookPath("systemd-run")
	return path, err == nil
}

// Install runs a package manager transaction. It is an exec.Command without a context, so no timeout and no cancellation ever
// sends it a signal; it runs at the normal priority of the agent. With systemd-run the scope execs the package manager in the same
// process, so the process the agent waits for is the package manager itself and the guard of the installation lasts as long as it.
func (s linuxSystem) Install(env []string, out io.Writer, name string, args ...string) (int, error) {
	path, err := exec.LookPath(name)
	if err != nil {
		return -1, err
	}
	argv0, argv := path, args
	if run, ok := systemdRun(); ok {
		argv0, argv = run, scopeArgv(scopeUnit(s.jobID), path, args)
	}
	cmd := exec.Command(argv0, argv...) // #nosec G204 -- fixed commands; arguments are validated package names and versions.
	cmd.Env = append(os.Environ(), env...)
	cmd.Stdin = nil
	err = s.run(cmd, out)
	var exit *exec.ExitError
	switch {
	case err == nil:
		return 0, nil
	case errors.As(err, &exit):
		return exit.ExitCode(), nil
	}
	return -1, err
}

// run starts cmd with its output in a file of the job's directory, which it follows into out until the process ended; without a
// directory, or when the file cannot be made, through a pipe.
func (s linuxSystem) run(cmd *exec.Cmd, out io.Writer) error {
	if s.outputDir == "" {
		cmd.Stdout, cmd.Stderr = out, out
		return cmd.Run()
	}
	f, err := os.CreateTemp(s.outputDir, "package-manager-*.log")
	if err != nil {
		s.logger.Warn("the output file of the package manager could not be made; using a pipe", "error", err)
		cmd.Stdout, cmd.Stderr = out, out
		return cmd.Run()
	}
	name := f.Name()
	defer os.Remove(name)
	reader, err := os.Open(name)
	if err != nil {
		_ = f.Close()
		cmd.Stdout, cmd.Stderr = out, out
		return cmd.Run()
	}
	defer reader.Close()
	cmd.Stdout, cmd.Stderr = f, f
	err = cmd.Start()
	// The child holds its own descriptor of the file.
	_ = f.Close()
	if err != nil {
		return err
	}
	done := make(chan struct{})
	followed := make(chan struct{})
	go func() {
		defer close(followed)
		followFile(reader, out, done, followPoll)
	}()
	err = cmd.Wait()
	close(done)
	<-followed
	return err
}
