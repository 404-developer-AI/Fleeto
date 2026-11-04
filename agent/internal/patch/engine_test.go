package patch

import (
	"context"
	"slices"
	"strings"
	"testing"
)

// fakeSystem answers commands from captured output, keyed by the command and its first arguments.
type fakeSystem struct {
	t        *testing.T
	tools    []string
	files    []string
	dirs     map[string][]string
	answers  map[string]cmdResult
	calls    []string
	envs     map[string][]string
	blockCtx bool
}

func (f *fakeSystem) Run(ctx context.Context, env []string, name string, args ...string) cmdResult {
	call := strings.Join(append([]string{name}, args...), " ")
	f.calls = append(f.calls, call)
	if f.envs == nil {
		f.envs = map[string][]string{}
	}
	f.envs[name] = env
	if f.blockCtx {
		<-ctx.Done()
		return cmdResult{Code: -1, Err: ctx.Err()}
	}
	for prefix, res := range f.answers {
		if strings.HasPrefix(call, prefix) {
			return res
		}
	}
	return cmdResult{Code: 127, Stderr: name + ": not answered"}
}

func (f *fakeSystem) LookPath(name string) bool         { return slices.Contains(f.tools, name) }
func (f *fakeSystem) Exists(path string) bool           { return slices.Contains(f.files, path) }
func (f *fakeSystem) ReadDirNames(path string) []string { return f.dirs[path] }

func TestScanWithPicksTheEngine(t *testing.T) {
	if r := scanWith(context.Background(), &fakeSystem{t: t}); r != nil {
		t.Fatal("without apt or dnf there is no report")
	}
	apt := &fakeSystem{t: t, tools: []string{"apt-get", "dpkg-query"}, answers: map[string]cmdResult{"apt-get": {}}}
	if r := scanWith(context.Background(), apt); r.GetEngine() != EngineApt {
		t.Fatalf("engine %q", r.GetEngine())
	}
	dnf := &fakeSystem{t: t, tools: []string{"dnf", "rpm"}, answers: map[string]cmdResult{"dnf": {}}}
	if r := scanWith(context.Background(), dnf); r.GetEngine() != EngineDnf {
		t.Fatalf("engine %q", r.GetEngine())
	}
}

func TestAptScanOfAProxmoxHostWithoutSubscription(t *testing.T) {
	sys := &fakeSystem{
		t:     t,
		tools: []string{"apt-get", "dpkg-query"},
		files: []string{rebootRequiredFile},
		dirs:  map[string][]string{dpkgUpdatesDir: {"0001"}},
		answers: map[string]cmdResult{
			"apt-get -q -o DPkg::Lock::Timeout=300 update":     {Stdout: readTestdata(t, "proxmox-ve-8/apt-get-update.txt"), Code: 100},
			"apt-get -s -q -o Debug::NoLocking=1 dist-upgrade": {Stdout: readTestdata(t, "proxmox-ve-8/apt-get-dist-upgrade.txt")},
			"apt-mark showhold": {},
		},
	}
	r := scanApt(context.Background(), sys)
	if len(r.GetUpdates()) != 7 || !r.GetRestartRequired() {
		t.Fatalf("%d updates, restart %v", len(r.GetUpdates()), r.GetRestartRequired())
	}
	for _, want := range []string{problemDpkgInterrupted, "enterprise.proxmox.com/debian/pve could not be read (401 Unauthorized)", "needs a subscription"} {
		if !strings.Contains(r.GetProblem(), want) {
			t.Errorf("problem %q lacks %q", r.GetProblem(), want)
		}
	}
	if !slices.Contains(sys.envs["apt-get"], "LC_ALL=C") {
		t.Fatalf("env %v", sys.envs["apt-get"])
	}
	for _, call := range sys.calls {
		if strings.HasPrefix(call, "dpkg-query") {
			t.Fatalf("nothing to look up, yet %q", call)
		}
	}
}

func TestAptScanLooksUpKeptBackPackagesInOneCall(t *testing.T) {
	sys := &fakeSystem{
		t:     t,
		tools: []string{"apt-get", "dpkg-query"},
		answers: map[string]cmdResult{
			"apt-get -q":        {Stdout: readTestdata(t, "ubuntu-22.04/apt-get-update.txt")},
			"apt-get -s":        {Stdout: readTestdata(t, "ubuntu-22.04/apt-get-dist-upgrade.txt")},
			"apt-mark showhold": {Stdout: readTestdata(t, "ubuntu-22.04/apt-mark-showhold.txt")},
			"dpkg-query -W":     {Stdout: readTestdata(t, "ubuntu-22.04/dpkg-query.txt")},
		},
	}
	r := scanApt(context.Background(), sys)
	if r.GetProblem() != "" || r.GetRestartRequired() || len(r.GetUpdates()) != 20 {
		t.Fatalf("problem %q, restart %v, %d updates", r.GetProblem(), r.GetRestartRequired(), len(r.GetUpdates()))
	}
	var lookups []string
	for _, call := range sys.calls {
		if strings.HasPrefix(call, "dpkg-query") {
			lookups = append(lookups, call)
		}
	}
	if len(lookups) != 1 || !strings.HasSuffix(lookups[0], "-- libfwupdplugin5 docker-ce python3-update-manager update-manager-core") {
		t.Fatalf("lookups %v", lookups)
	}
	if u := byID(r.GetUpdates())["docker-ce:amd64"]; u == nil || !u.GetHeld() {
		t.Fatalf("docker-ce = %+v", u)
	}
}

func TestAptScanReportsAFailedSimulation(t *testing.T) {
	sys := &fakeSystem{
		t:     t,
		tools: []string{"apt-get", "dpkg-query"},
		answers: map[string]cmdResult{
			"apt-get -q": {},
			"apt-get -s": {Code: 100, Stderr: "E: Unmet dependencies. Try 'apt --fix-broken install' with no packages (or specify a solution).\n"},
		},
	}
	r := scanApt(context.Background(), sys)
	if r.GetProblem() != "The upgrade could not be simulated: Unmet dependencies. Try 'apt --fix-broken install' with no packages (or specify a solution)." {
		t.Fatalf("problem %q", r.GetProblem())
	}
}

func dnfSystem(t *testing.T, dir string, check cmdResult) *fakeSystem {
	return &fakeSystem{
		t:     t,
		tools: []string{"dnf", "rpm", "needs-restarting"},
		answers: map[string]cmdResult{
			"dnf -q --refresh":    check,
			"rpm -q":              {Stdout: readTestdata(t, "almalinux-9/rpm-q.txt")},
			"dnf -q updateinfo":   {Stdout: readTestdata(t, dir+"/dnf-updateinfo-list.txt")},
			"dnf -q versionlock":  {Stdout: readTestdata(t, dir+"/dnf-versionlock-list.txt")},
			"needs-restarting -r": {Code: 1},
		},
	}
}

func TestDnfScanOfAlmaLinux9(t *testing.T) {
	sys := dnfSystem(t, "almalinux-9", cmdResult{Stdout: readTestdata(t, "almalinux-9/dnf-check-update.txt"),
		Stderr: readTestdata(t, "almalinux-9/dnf-check-update-repo-error.stderr.txt"), Code: 100})
	r := scanDnf(context.Background(), sys)
	if len(r.GetUpdates()) != 10 || !r.GetRestartRequired() {
		t.Fatalf("%d updates, restart %v", len(r.GetUpdates()), r.GetRestartRequired())
	}
	if r.GetProblem() != "The repository epel could not be read; the scan used the other repositories." {
		t.Fatalf("problem %q", r.GetProblem())
	}
	if u := byID(r.GetUpdates())["openssl:x86_64"]; u.GetInstalledVersion() != "1:3.0.7-27.el9" || u.GetClassification() != "security" {
		t.Fatalf("openssl = %+v", u)
	}
	var rpm []string
	for _, call := range sys.calls {
		if strings.HasPrefix(call, "rpm ") {
			rpm = append(rpm, call)
		}
	}
	if len(rpm) != 1 || !strings.Contains(rpm[0], "-- NetworkManager.x86_64 ") {
		t.Fatalf("rpm calls %v", rpm)
	}
}

func TestDnfScanWithNothingMissing(t *testing.T) {
	sys := dnfSystem(t, "almalinux-9", cmdResult{Code: 0})
	sys.answers["needs-restarting -r"] = cmdResult{Code: 0}
	r := scanDnf(context.Background(), sys)
	if len(r.GetUpdates()) != 0 || r.GetProblem() != "" || r.GetRestartRequired() {
		t.Fatalf("%+v", r)
	}
	for _, call := range sys.calls {
		if strings.HasPrefix(call, "rpm") || strings.Contains(call, "updateinfo") {
			t.Fatalf("nothing to look up, yet %q", call)
		}
	}
}

func TestDnfScanReportsAnError(t *testing.T) {
	sys := dnfSystem(t, "almalinux-9", cmdResult{Code: 1,
		Stderr: "Error: Failed to download metadata for repo 'baseos': Cannot download repomd.xml: Cannot download repodata/repomd.xml: All mirrors were tried\n"})
	r := scanDnf(context.Background(), sys)
	if !strings.Contains(r.GetProblem(), "The repository baseos could not be read") ||
		!strings.Contains(r.GetProblem(), "dnf check-update failed: Failed to download metadata for repo 'baseos'") {
		t.Fatalf("problem %q", r.GetProblem())
	}
}

func TestDnfScanWithoutAdvisoriesSaysSo(t *testing.T) {
	sys := dnfSystem(t, "almalinux-9", cmdResult{Stdout: readTestdata(t, "almalinux-9/dnf-check-update.txt"), Code: 100})
	sys.answers["dnf -q updateinfo"] = cmdResult{Code: 1, Stderr: "Error: Failed to load updateinfo\n"}
	r := scanDnf(context.Background(), sys)
	if !strings.HasPrefix(r.GetProblem(), "The security advisories could not be read") {
		t.Fatalf("problem %q", r.GetProblem())
	}
	for _, u := range r.GetUpdates() {
		if u.GetClassification() != "other" {
			t.Fatalf("%s is %s", u.GetId(), u.GetClassification())
		}
	}
}
