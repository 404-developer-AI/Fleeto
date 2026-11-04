package patch

import (
	"bytes"
	"context"
	"fmt"
	"io"
	"slices"
	"strings"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// fakeInstallSystem is a fakeSystem that also runs transactions: it records them and answers the reads after a transaction from
// after.
type fakeInstallSystem struct {
	fakeSystem
	after    map[string]cmdResult
	output   string
	code     int
	err      error
	installs [][]string
	envs     [][]string
}

func (f *fakeInstallSystem) Run(ctx context.Context, env []string, name string, args ...string) cmdResult {
	if len(f.installs) > 0 {
		call := strings.Join(append([]string{name}, args...), " ")
		for prefix, res := range f.after {
			if strings.HasPrefix(call, prefix) {
				f.calls = append(f.calls, call)
				return res
			}
		}
	}
	return f.fakeSystem.Run(ctx, env, name, args...)
}

func (f *fakeInstallSystem) Install(env []string, out io.Writer, name string, args ...string) (int, error) {
	f.installs = append(f.installs, append([]string{name}, args...))
	f.envs = append(f.envs, env)
	_, _ = io.WriteString(out, f.output)
	return f.code, f.err
}

const (
	aptUpdateCall = "apt-get -q -o DPkg::Lock::Timeout=300 update"
	aptSimCall    = "apt-get -s -q -o Debug::NoLocking=1 dist-upgrade"
)

// aptSimulation is a dist-upgrade simulation with a security update, a regular update, a foreign architecture and a removal.
func aptSimulationOutput() string {
	native := debianArch()
	return fmt.Sprintf(`Reading package lists...
The following packages will be REMOVED:
  oldpkg
Inst libssl3 [3.0.2-0ubuntu1.10] (3.0.2-0ubuntu1.12 Ubuntu:22.04/jammy-security [%[1]s])
Inst curl [7.81.0-1ubuntu1.14] (7.81.0-1ubuntu1.15 Ubuntu:22.04/jammy-updates [%[1]s])
Inst libc6:i386 [2.35-0ubuntu3.4] (2.35-0ubuntu3.5 Ubuntu:22.04/jammy-security [i386])
Remv oldpkg:%[1]s [1.0]
`, native)
}

func aptSystem() *fakeInstallSystem {
	native := debianArch()
	return &fakeInstallSystem{
		fakeSystem: fakeSystem{
			tools: []string{"apt-get", "dpkg-query"},
			answers: map[string]cmdResult{
				aptUpdateCall:       {},
				aptSimCall:          {Stdout: aptSimulationOutput()},
				"apt-mark showhold": {},
			},
		},
		output: "Setting up libssl3 (3.0.2-0ubuntu1.12) ...",
		after: map[string]cmdResult{
			// curl stayed at its old version; dpkg-query ends with 1 for a package it does not know and still lists the rest.
			"dpkg-query -W": {Code: 1, Stdout: fmt.Sprintf("libssl3:%[1]s\tlibssl3\t%[1]s\t3.0.2-0ubuntu1.12\ncurl:%[1]s\tcurl\t%[1]s\t7.81.0-1ubuntu1.14\n"+
				"libc6:i386\tlibc6\ti386\t2.35-0ubuntu3.5\n", native)},
		},
	}
}

func runInstall(t *testing.T, sys installSystem, job *agentv1.PatchInstallJob) (InstallResult, string) {
	t.Helper()
	var buf bytes.Buffer
	result := installLinux(context.Background(), sys, job, newInstallOutput(&buf, &buf))
	return result, buf.String()
}

func TestAptInstallsEverythingMissingWithADistUpgrade(t *testing.T) {
	sys := aptSystem()
	result, out := runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	want := []string{"apt-get", "-y", "-q", "-o", "Dpkg::Options::=--force-confdef", "-o", "Dpkg::Options::=--force-confold",
		"-o", "DPkg::Lock::Timeout=600", "dist-upgrade"}
	if len(sys.installs) != 1 || !slices.Equal(sys.installs[0], want) {
		t.Fatalf("installs %q, want %q", sys.installs, want)
	}
	for _, v := range []string{"DEBIAN_FRONTEND=noninteractive", "LC_ALL=C"} {
		if !slices.Contains(sys.envs[0], v) {
			t.Errorf("env %v lacks %s", sys.envs[0], v)
		}
	}
	native := debianArch()
	for _, line := range []string{
		"Setting up libssl3 (3.0.2-0ubuntu1.12) ...\nfleeto-patch: installed libssl3:" + native + " libssl3\n",
		"fleeto-patch: failed curl:" + native + " curl\n",
		"fleeto-patch: installed libc6:i386 libc6\n",
	} {
		if !strings.Contains(out, line) {
			t.Errorf("output lacks %q:\n%s", line, out)
		}
	}
	if strings.Contains(out, "oldpkg") && strings.Contains(out, "fleeto-patch: installed oldpkg") {
		t.Error("a removal is not reported as an installed update")
	}
	if result.ExitCode != 1 || result.Installed != 2 || result.RestartRequired {
		t.Fatalf("result %+v", result)
	}
	query := sys.calls[len(sys.calls)-1]
	if !strings.HasPrefix(query, "dpkg-query -W -f=") || !strings.Contains(query, " -- libssl3 curl libc6:i386") {
		t.Fatalf("one dpkg-query for every target, with -- before the names: %q", query)
	}
}

func TestAptInstallsExactlyTheSelectionWhenAFilterLeavesSomethingOut(t *testing.T) {
	sys := aptSystem()
	sys.files = []string{rebootRequiredFile}
	result, out := runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING,
		Filter: &agentv1.PatchInstallFilter{SecurityOnly: true}})
	want := append(append([]string{"apt-get"}, aptInstallOptions...), "install", "--only-upgrade", "--",
		"libssl3=3.0.2-0ubuntu1.12", "libc6:i386=2.35-0ubuntu3.5")
	if len(sys.installs) != 1 || !slices.Equal(sys.installs[0], want) {
		t.Fatalf("installs %q, want %q", sys.installs, want)
	}
	if result.ExitCode != 0 || result.Installed != 2 || !result.RestartRequired {
		t.Fatalf("result %+v\n%s", result, out)
	}
	if strings.Contains(out, "fleeto-patch: failed") || strings.Contains(out, "curl") && strings.Contains(out, "fleeto-patch: excluded curl") {
		t.Fatalf("an update outside the filter is not part of the installation:\n%s", out)
	}
}

func TestAptInstallsNamedUpdatesAndReportsTheRest(t *testing.T) {
	sys := aptSystem()
	native := debianArch()
	result, out := runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED,
		Updates: []*agentv1.PatchInstallUpdate{
			{Id: "libssl3:" + native, Title: "libssl3", Version: "3.0.2-0ubuntu1.12"},
			{Id: "vim:" + native, Title: "vim", Version: "2:8.2.3995-1ubuntu2.20"},
		}})
	want := append(append([]string{"apt-get"}, aptInstallOptions...), "install", "--only-upgrade", "--", "libssl3=3.0.2-0ubuntu1.12")
	if len(sys.installs) != 1 || !slices.Equal(sys.installs[0], want) {
		t.Fatalf("installs %q, want %q", sys.installs, want)
	}
	if !strings.Contains(out, "fleeto-patch: installed libssl3:"+native+" libssl3\n") ||
		!strings.Contains(out, "fleeto-patch: not-missing vim:"+native+" vim\n") {
		t.Fatalf("output:\n%s", out)
	}
	if result.ExitCode != 0 {
		t.Fatalf("an update that is no longer missing is no failure: %+v", result)
	}
}

func TestNothingMissingInstallsNothingAndSucceeds(t *testing.T) {
	sys := aptSystem()
	sys.answers[aptSimCall] = cmdResult{Stdout: "Reading package lists...\n0 upgraded, 0 newly installed, 0 to remove and 0 not upgraded.\n"}
	result, out := runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if len(sys.installs) != 0 || result.ExitCode != 0 || !strings.Contains(out, "Nothing to install") {
		t.Fatalf("installs %v, result %+v:\n%s", sys.installs, result, out)
	}
}

func TestAFailedScanInstallsNothingAndFails(t *testing.T) {
	sys := aptSystem()
	sys.answers[aptSimCall] = cmdResult{Code: 100, Stderr: "E: dpkg was interrupted, you must manually run 'dpkg --configure -a' to correct the problem."}
	result, out := runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED,
		Updates: []*agentv1.PatchInstallUpdate{{Id: "libssl3:amd64", Title: "libssl3", Version: "1"}}})
	if len(sys.installs) != 0 || result.ExitCode != 1 || !strings.Contains(result.Error, "dpkg --configure -a") {
		t.Fatalf("an empty list of a failed scan is not 'nothing missing': installs %v, result %+v", sys.installs, result)
	}
	if !strings.Contains(out, "fleeto-patch: failed libssl3:amd64 libssl3\n") {
		t.Fatalf("every named update is reported as failed:\n%s", out)
	}
}

func TestAPackageManagerThatFailsFailsTheJob(t *testing.T) {
	sys := aptSystem()
	sys.code = 100
	sys.output = "E: Could not get lock /var/lib/dpkg/lock-frontend"
	result, out := runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if result.ExitCode != 1 || !strings.Contains(result.Error, "exit code 100") {
		t.Fatalf("result %+v", result)
	}
	// The tool's output does not end in a newline; the lines of the agent still start on their own line.
	if !strings.Contains(out, "lock-frontend\napt-get ended with exit code 100.\n") {
		t.Fatalf("output:\n%s", out)
	}
}

func dnfInstallSystem() *fakeInstallSystem {
	return &fakeInstallSystem{
		fakeSystem: fakeSystem{
			tools: []string{"dnf", "rpm"},
			answers: map[string]cmdResult{
				"dnf -q --refresh --setopt=skip_if_unavailable=True check-update": {Code: 100, Stdout: "\n" +
					"openssl.x86_64            1:3.0.7-27.el9          baseos\n" +
					"kernel.x86_64             5.14.0-503.15.1.el9_5   baseos\n" +
					"vim-minimal.x86_64        2:8.2.2637-21.el9       baseos\n"},
				"rpm -q": {Stdout: "openssl.x86_64 1:3.0.7-25.el9\nkernel.x86_64 5.14.0-503.11.1.el9_5\nvim-minimal.x86_64 2:8.2.2637-20.el9\n"},
				"dnf -q updateinfo list --updates": {Stdout: "ALSA-2024:1 Important/Sec. openssl-1:3.0.7-27.el9.x86_64\n" +
					"ALSA-2024:2 Important/Sec. kernel-5.14.0-503.15.1.el9_5.x86_64\n"},
				"dnf -q versionlock list": {},
			},
		},
		after: map[string]cmdResult{
			// The new kernel is installed next to the running one; vim stayed.
			"rpm -q": {Stdout: "openssl.x86_64 1:3.0.7-27.el9\nkernel.x86_64 5.14.0-503.11.1.el9_5\nkernel.x86_64 5.14.0-503.15.1.el9_5\n" +
				"vim-minimal.x86_64 2:8.2.2637-20.el9\n"},
			"dnf needs-restarting -r": {Code: 1},
		},
	}
}

func TestDnfCommandLines(t *testing.T) {
	cases := []struct {
		name string
		job  *agentv1.PatchInstallJob
		want []string
	}{
		{"everything", &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING},
			[]string{"dnf", "-y", "-q", "upgrade"}},
		{"security without vim", &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING,
			Filter: &agentv1.PatchInstallFilter{SecurityOnly: true, ExcludedNames: []string{"vim-minimal"}}},
			[]string{"dnf", "-y", "-q", "upgrade", "--security", "--exclude=vim-minimal"}},
		{"named", &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED, Updates: []*agentv1.PatchInstallUpdate{
			{Id: "openssl:x86_64", Version: "1:3.0.7-27.el9"}, {Id: "kernel:x86_64", Version: "5.14.0-503.15.1.el9_5"}}},
			[]string{"dnf", "-y", "-q", "upgrade", "--", "openssl-1:3.0.7-27.el9.x86_64", "kernel-5.14.0-503.15.1.el9_5.x86_64"}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			sys := dnfInstallSystem()
			result, out := runInstall(t, sys, c.job)
			if len(sys.installs) != 1 || !slices.Equal(sys.installs[0], c.want) {
				t.Fatalf("installs %q, want %q", sys.installs, c.want)
			}
			if !slices.Contains(sys.envs[0], "LC_ALL=C") {
				t.Errorf("env %v", sys.envs[0])
			}
			if !strings.Contains(out, "fleeto-patch: installed openssl:x86_64") || !strings.Contains(out, "fleeto-patch: installed kernel:x86_64") {
				t.Fatalf("output:\n%s", out)
			}
			if !result.RestartRequired {
				t.Error("needs-restarting -r ending with 1 means a restart")
			}
		})
	}
}

func TestDnfReportsAnUpdateThatStayedAsFailed(t *testing.T) {
	sys := dnfInstallSystem()
	result, out := runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if !strings.Contains(out, "fleeto-patch: failed vim-minimal:x86_64 vim-minimal\n") || result.ExitCode != 1 || result.Installed != 2 {
		t.Fatalf("result %+v:\n%s", result, out)
	}
}

func TestNoInstallationGoesThroughAShell(t *testing.T) {
	for _, sys := range []*fakeInstallSystem{aptSystem(), dnfInstallSystem()} {
		_, _ = runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
		for _, call := range append(sys.calls, func() []string {
			var c []string
			for _, i := range sys.installs {
				c = append(c, strings.Join(i, " "))
			}
			return c
		}()...) {
			name := strings.Fields(call)[0]
			if name == "sh" || name == "bash" || name == "/bin/sh" || strings.Contains(name, "/") {
				t.Fatalf("%q runs through a shell or a path", call)
			}
		}
	}
}

func TestInvalidTargetsFromTheScanAreNeverPassedOn(t *testing.T) {
	sys := aptSystem()
	sys.answers[aptSimCall] = cmdResult{Stdout: fmt.Sprintf("Inst libssl3 [1] (1:2;x Ubuntu:22.04/jammy-security [%s])\n", debianArch())}
	result, out := runInstall(t, sys, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if len(sys.installs) != 0 || result.ExitCode != 1 || !strings.Contains(out, "fleeto-patch: failed libssl3:") {
		t.Fatalf("installs %v, result %+v:\n%s", sys.installs, result, out)
	}
}

func TestTheInstallGuardAllowsOneInstallationAtATime(t *testing.T) {
	release, ok := BeginInstall()
	if !ok || !InstallRunning() {
		t.Fatal("the first installation takes the guard")
	}
	if _, again := BeginInstall(); again {
		t.Fatal("a second installation is refused while the first runs")
	}
	release()
	release()
	if InstallRunning() {
		t.Fatal("release frees the guard")
	}
	next, ok := BeginInstall()
	if !ok {
		t.Fatal("after the release a new installation may start")
	}
	next()
}
