package patch

import (
	"context"
	"fmt"
	"io"
	"strings"
	"sync"
	"sync/atomic"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Installing updates (0.9.0 step 3, PATCHING.md section 5). The agent scans first, selects (select.go), installs with apt, dnf or
// the Windows Update Agent and reads per update whether it was installed. The package manager is never stopped once it runs: an
// interrupted dpkg, rpm or msiexec transaction leaves an endpoint that no longer updates (decision 8). The jobs package bounds the
// job with its timeout and frees the job slot; the installation itself runs on until it ends.

// InstallResult is the outcome of one installation.
type InstallResult struct {
	// ExitCode is 0 when every selected update was installed (also when nothing was missing), 1 when one failed or the tool failed.
	ExitCode int32
	// Installed counts the updates installed.
	Installed int
	// RestartRequired: the endpoint needs a restart to finish the updates.
	RestartRequired bool
	// Error says why the tool or the scan failed; empty otherwise.
	Error string
}

// InstallOptions is what an installation needs besides its job.
type InstallOptions struct {
	// JobID names the job; on Linux it names the systemd scope of the package manager.
	JobID string
	// Drivers is patch_include_drivers of the applied configuration (Windows): the search before the installation includes drivers,
	// as the scans do.
	Drivers bool
	// OutputDir is a directory of the job (Linux) where the output of the package manager is written to a file the agent reads, so
	// the package manager never writes to a pipe that ends with the agent. Empty uses a pipe.
	OutputDir string
}

// installing guards the one installation that may run on the endpoint; it stays set while the package manager runs, also after
// the job timed out.
var installing atomic.Bool

// BeginInstall claims the installation guard. ok is false while an earlier installation still runs. release frees it once.
func BeginInstall() (release func(), ok bool) {
	if !installing.CompareAndSwap(false, true) {
		return nil, false
	}
	var once sync.Once
	return func() { once.Do(func() { installing.Store(false) }) }, true
}

// InstallRunning reports whether an installation runs; scans wait for it to end.
func InstallRunning() bool { return installing.Load() }

// installOutput writes the output of an installation: the tool's own output, and the lines of the agent, which always start on a
// line of their own.
type installOutput struct {
	mu      sync.Mutex
	tool    io.Writer
	lines   io.Writer
	lastNL  bool
	written bool
}

func newInstallOutput(tool, lines io.Writer) *installOutput {
	return &installOutput{tool: tool, lines: lines, lastNL: true}
}

// Write is the tool's output.
func (o *installOutput) Write(p []byte) (int, error) {
	o.mu.Lock()
	defer o.mu.Unlock()
	if len(p) == 0 {
		return 0, nil
	}
	_, _ = o.tool.Write(p)
	o.lastNL = p[len(p)-1] == '\n'
	o.written = true
	return len(p), nil
}

// say writes a line of the agent.
func (o *installOutput) say(format string, args ...any) {
	o.line(fmt.Sprintf(format, args...) + "\n")
}

// line writes text that ends in a newline, on a line of its own.
func (o *installOutput) line(text string) {
	o.mu.Lock()
	defer o.mu.Unlock()
	if !o.lastNL {
		_, _ = io.WriteString(o.lines, "\n")
	}
	_, _ = io.WriteString(o.lines, text)
	o.lastNL = true
}

// report writes the "fleeto-patch:" lines: the outcome of every target, then the updates that were not installed.
func (o *installOutput) report(targets []Target, installed map[string]bool, others []Outcome) {
	var b strings.Builder
	for _, t := range targets {
		status := StatusFailed
		if installed[t.ID] {
			status = StatusInstalled
		}
		b.WriteString(Line(status, t.ID, t.Title))
	}
	for _, x := range others {
		b.WriteString(Line(x.Status, x.ID, x.Title))
	}
	if b.Len() > 0 {
		o.line(b.String())
	}
}

// finish turns the outcome into a result.
func finish(targets []Target, installed map[string]bool, toolError string, restart bool) InstallResult {
	result := InstallResult{RestartRequired: restart, Error: toolError}
	for _, t := range targets {
		if installed[t.ID] {
			result.Installed++
		}
	}
	if toolError != "" || result.Installed < len(targets) {
		result.ExitCode = 1
	}
	return result
}

// failAll reports every named update of a job that could not even start as failed.
func failAll(out *installOutput, job *agentv1.PatchInstallJob, reason string) InstallResult {
	out.say("%s", reason)
	var targets []Target
	for _, u := range job.GetUpdates() {
		targets = append(targets, Target{ID: u.GetId(), Title: u.GetTitle()})
	}
	out.report(targets, nil, nil)
	return InstallResult{ExitCode: 1, Error: reason}
}

// The commands of an installation, as argument vectors.

// installSystem is what an installation needs of a Linux endpoint, on top of what a scan needs.
type installSystem interface {
	system
	// Install runs a package manager transaction with an argument vector, never through a shell, with its output to out. It
	// takes no context on purpose: nothing stops it once it started.
	Install(env []string, out io.Writer, name string, args ...string) (code int, err error)
}

const (
	// installScanTimeout bounds the scan before an installation; a scan is safe to stop.
	installScanTimeout = ScanTimeout
	// installReadTimeout bounds the read of the installed versions afterwards.
	installReadTimeout = 2 * time.Minute
)

// aptInstallEnv makes apt and dpkg answer in English and never ask anything.
var aptInstallEnv = []string{"DEBIAN_FRONTEND=noninteractive", "LC_ALL=C", "LANG=C", "APT_LISTCHANGES_FRONTEND=none"}

// aptInstallOptions keep the configuration files the administrator changed and wait up to 10 minutes for the package lock.
var aptInstallOptions = []string{"-y", "-q", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold",
	"-o", "DPkg::Lock::Timeout=600"}

// aptCommand is the apt-get command line of a selection: dist-upgrade for everything missing without a filter that left something
// out, which also installs the new dependencies a kernel or pve-manager needs; otherwise exactly the targets.
func aptCommand(job *agentv1.PatchInstallJob, sel Selection) []string {
	args := append([]string(nil), aptInstallOptions...)
	if job.GetScope() == agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING && !sel.Narrowed {
		return append(args, "dist-upgrade")
	}
	args = append(args, "install", "--only-upgrade", "--")
	for _, t := range sel.Targets {
		args = append(args, aptPackage(t)+"="+t.Version)
	}
	return args
}

// aptPackage names a package for apt and dpkg-query: with its architecture unless it is the native one or "all".
func aptPackage(t Target) string {
	if t.Arch == "" || t.Arch == "all" || t.Arch == debianArch() {
		return t.Name
	}
	return t.Name + ":" + t.Arch
}

// dnfCommand is the dnf command line of a selection: exactly the targets for SPECIFIED, otherwise an upgrade of everything (security
// updates only on request) without the excluded names. Version locks are respected by dnf itself.
func dnfCommand(job *agentv1.PatchInstallJob, sel Selection) []string {
	args := []string{"-y", "-q", "upgrade"}
	if job.GetScope() == agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING {
		if job.GetFilter().GetSecurityOnly() {
			args = append(args, "--security")
		}
		for _, name := range job.GetFilter().GetExcludedNames() {
			if validPackageName(EngineDnf, name) {
				args = append(args, "--exclude="+name)
			}
		}
		return args
	}
	args = append(args, "--")
	for _, t := range sel.Targets {
		args = append(args, t.Name+"-"+t.Version+"."+t.Arch)
	}
	return args
}

// validTargets keeps the targets whose name, architecture and version have the form of the engine; the others fail before any
// command runs.
func validTargets(engine string, targets []Target) (valid, invalid []Target) {
	for _, t := range targets {
		if validUpdateID(engine, t.ID) && t.Name != "" && validVersion(t.Version) {
			valid = append(valid, t)
		} else {
			invalid = append(invalid, t)
		}
	}
	return valid, invalid
}

// installLinux runs one installation with apt or dnf.
func installLinux(ctx context.Context, sys installSystem, job *agentv1.PatchInstallJob, out *installOutput) InstallResult {
	var engine string
	switch {
	case sys.LookPath("apt-get") && sys.LookPath("dpkg-query"):
		engine = EngineApt
	case sys.LookPath("dnf") && sys.LookPath("rpm"):
		engine = EngineDnf
	default:
		return failAll(out, job, "No supported package manager (apt or dnf) was found on this endpoint.")
	}

	out.say("Scanning for missing updates with %s.", engine)
	scanCtx, cancel := context.WithTimeout(ctx, installScanTimeout)
	var report *agentv1.PatchScanReport
	var failed bool
	if engine == EngineApt {
		report, failed = scanAptChecked(scanCtx, sys)
	} else {
		report, failed = scanDnfChecked(scanCtx, sys)
	}
	cancel()
	if ctx.Err() != nil {
		return failAll(out, job, "The agent stopped before the installation started; nothing was installed.")
	}
	if report.GetProblem() != "" {
		out.say("The scan reported: %s", report.GetProblem())
	}
	if failed {
		return failAll(out, job, "The scan before the installation failed, so nothing was installed: "+
			firstNonEmpty(report.GetProblem(), "the package manager did not answer."))
	}

	sel := Select(job, report)
	targets, invalid := validTargets(engine, sel.Targets)
	if len(targets) == 0 {
		if len(invalid) == 0 {
			out.say("Nothing to install: no selected update is missing.")
		}
		out.report(invalid, nil, sel.Outcomes)
		return finish(invalid, nil, "", report.GetRestartRequired())
	}
	sel.Targets = targets

	name, env, args := "apt-get", aptInstallEnv, aptCommand(job, sel)
	if engine == EngineDnf {
		name, env, args = "dnf", dnfEnv, dnfCommand(job, sel)
	}
	out.say("Installing %d updates: %s %s", len(targets), name, strings.Join(args, " "))
	code, err := sys.Install(env, out, name, args...)
	var toolError string
	switch {
	case err != nil:
		toolError = fmt.Sprintf("%s could not run: %v.", name, err)
	case code != 0:
		toolError = fmt.Sprintf("%s ended with exit code %d.", name, code)
	}
	if toolError != "" {
		out.say("%s", toolError)
	}

	readCtx, cancelRead := context.WithTimeout(context.Background(), installReadTimeout)
	defer cancelRead()
	var installed map[string]bool
	var restart bool
	if engine == EngineApt {
		installed = aptInstalled(readCtx, sys, targets)
		restart = sys.Exists(rebootRequiredFile)
	} else {
		installed = rpmInstalled(readCtx, sys, targets)
		restart = needsRestarting(readCtx, sys)
	}
	all := append(targets, invalid...)
	out.report(all, installed, sel.Outcomes)
	return finish(all, installed, toolError, restart)
}

// aptInstalled reads the installed versions in one dpkg-query call: a target is installed when its version is.
func aptInstalled(ctx context.Context, sys system, targets []Target) map[string]bool {
	args := []string{"-W", "-f=${binary:Package}\\t${Package}\\t${Architecture}\\t${Version}\\n", "--"}
	for _, t := range targets {
		args = append(args, aptPackage(t))
	}
	// dpkg-query ends with 1 when a package is not installed and still lists the others.
	res := sys.Run(ctx, aptEnv, "dpkg-query", args...)
	found := parseDpkgQueryInstalled(res.Stdout)
	result := map[string]bool{}
	for _, t := range targets {
		p, ok := found[aptPackage(t)]
		if !ok {
			p, ok = found[t.Name+":"+t.Arch]
		}
		result[t.ID] = ok && p.Version == t.Version
	}
	return result
}

// rpmInstalled reads the installed versions in one rpm call: a target is installed when one of the installed versions of the
// package is its version (a kernel is installed next to the running one).
func rpmInstalled(ctx context.Context, sys system, targets []Target) map[string]bool {
	args := []string{"-q", "--qf", "%{NAME}.%{ARCH} %|EPOCH?{%{EPOCH}:}:{}|%{VERSION}-%{RELEASE}\\n", "--"}
	for _, t := range targets {
		args = append(args, t.Name+"."+t.Arch)
	}
	res := sys.Run(ctx, dnfEnv, "rpm", args...)
	versions := map[string][]string{}
	for line := range strings.SplitSeq(res.Stdout, "\n") {
		fields := strings.Fields(line)
		if len(fields) == 2 && !strings.Contains(line, "is not installed") {
			versions[fields[0]] = append(versions[fields[0]], strings.TrimPrefix(fields[1], "0:"))
		}
	}
	result := map[string]bool{}
	for _, t := range targets {
		want := strings.TrimPrefix(t.Version, "0:")
		for _, v := range versions[t.Name+"."+t.Arch] {
			if v == want {
				result[t.ID] = true
			}
		}
	}
	return result
}

func firstNonEmpty(values ...string) string {
	for _, v := range values {
		if v != "" {
			return v
		}
	}
	return ""
}

// followPoll is how often the output file of a package manager is read while it runs.
const followPoll = 250 * time.Millisecond

// followFile copies what is written to r into out until done is closed, then what is left. r is a file the package manager writes
// to; an end of file only means it wrote nothing new yet.
func followFile(r io.Reader, out io.Writer, done <-chan struct{}, poll time.Duration) {
	buf := make([]byte, 32*1024)
	drain := func() bool {
		wrote := false
		for {
			n, err := r.Read(buf)
			if n > 0 {
				_, _ = out.Write(buf[:n])
				wrote = true
			}
			if err != nil || n == 0 {
				return wrote
			}
		}
	}
	for {
		drain()
		select {
		case <-done:
			drain()
			return
		case <-time.After(poll):
		}
	}
}

// The systemd scope of a package manager on Linux (PATCHING.md section 15).

// underSystemd: the agent was started by systemd (INVOCATION_ID is set for every unit it starts) or systemd is the init system
// (/run/systemd/system exists).
func underSystemd(invocationID string, runSystemdExists bool) bool {
	return invocationID != "" || runSystemdExists
}

// scopeUnit names the transient scope of an installation after its job: "fleeto-patch-" and the first 12 letters and digits of the
// job id; empty (systemd then picks a name) when the id has none.
func scopeUnit(jobID string) string {
	var b strings.Builder
	for _, r := range jobID {
		if b.Len() == 12 {
			break
		}
		if r >= 'a' && r <= 'z' || r >= 'A' && r <= 'Z' || r >= '0' && r <= '9' {
			b.WriteRune(r)
		}
	}
	if b.Len() == 0 {
		return ""
	}
	return "fleeto-patch-" + strings.ToLower(b.String())
}

// scopeArgv is the argument vector of systemd-run that runs the package manager at path with args in a transient scope: never a
// shell, and "--" so nothing of the command is read as an option of systemd-run. --collect unloads the scope also after a failure.
func scopeArgv(unit, path string, args []string) []string {
	argv := []string{"--scope", "--quiet", "--collect"}
	if unit != "" {
		argv = append(argv, "--unit="+unit)
	}
	argv = append(argv, "--", path)
	return append(argv, args...)
}
