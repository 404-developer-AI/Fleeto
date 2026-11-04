package patch

import (
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func readTestdata(t *testing.T, name string) string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("testdata", filepath.FromSlash(name)))
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func byID(updates []*agentv1.PatchUpdate) map[string]*agentv1.PatchUpdate {
	m := map[string]*agentv1.PatchUpdate{}
	for _, u := range updates {
		m[u.GetId()] = u
	}
	return m
}

type wantUpdate struct {
	id, version, installed, classification, origin, advisory, severity string
	held, removal                                                      bool
}

func checkUpdates(t *testing.T, got []*agentv1.PatchUpdate, count int, want []wantUpdate) {
	t.Helper()
	if len(got) != count {
		ids := make([]string, 0, len(got))
		for _, u := range got {
			ids = append(ids, u.GetId())
		}
		t.Fatalf("%d updates, want %d: %v", len(got), count, ids)
	}
	m := byID(got)
	for _, w := range want {
		u := m[w.id]
		if u == nil {
			t.Errorf("%s is missing", w.id)
			continue
		}
		if name, _, _ := strings.Cut(w.id, ":"); u.GetName() != name {
			t.Errorf("%s: name %q", w.id, u.GetName())
		}
		if u.GetVersion() != w.version || u.GetInstalledVersion() != w.installed || u.GetClassification() != w.classification ||
			u.GetOrigin() != w.origin || u.GetAdvisory() != w.advisory || u.GetSeverity() != w.severity || u.GetHeld() != w.held ||
			u.GetRemoval() != w.removal {
			t.Errorf("%s = %+v,\nwant %+v", w.id, u, w)
		}
	}
}

func aptUpdates(sim aptSimulation, holds map[string]bool, installed map[string]dpkgInstalled) []*agentv1.PatchUpdate {
	var out []*agentv1.PatchUpdate
	for _, u := range sim.Updates {
		out = append(out, u.toProto(holds, installed))
	}
	return out
}

func TestAptSimulationOfUbuntu2204(t *testing.T) {
	sim := parseAptSimulation(readTestdata(t, "ubuntu-22.04/apt-get-dist-upgrade.txt"))
	holds := map[string]bool{}
	for _, n := range parseAptMarkShowhold(readTestdata(t, "ubuntu-22.04/apt-mark-showhold.txt")) {
		holds[n] = true
	}
	installed := parseDpkgQueryInstalled(readTestdata(t, "ubuntu-22.04/dpkg-query.txt"))
	got := aptUpdates(sim, holds, installed)
	// 16 Inst lines, 1 Remv line, 3 kept back.
	checkUpdates(t, got, 20, []wantUpdate{
		{id: "libssl3:amd64", version: "3.0.2-0ubuntu1.19", installed: "3.0.2-0ubuntu1.18", classification: "security", origin: "jammy-security"},
		{id: "libssl3:i386", version: "3.0.2-0ubuntu1.19", installed: "3.0.2-0ubuntu1.18", classification: "security", origin: "jammy-security"},
		{id: "tzdata:all", version: "2024b-0ubuntu0.22.04.1", installed: "2024a-0ubuntu0.22.04.1", classification: "other", origin: "jammy-updates"},
		{id: "imagemagick-6-common:all", version: "8:6.9.11.60+dfsg-1.3ubuntu0.22.04.5+esm1", installed: "8:6.9.11.60+dfsg-1.3ubuntu0.22.04.5",
			classification: "security", origin: "jammy-apps-security"},
		// A new dependency: nothing installed.
		{id: "libfwupd2:amd64", version: "1.7.9-1~22.04.4", classification: "other", origin: "jammy-updates"},
		{id: "linux-image-5.15.0-126-generic:amd64", version: "5.15.0-126.136", classification: "security", origin: "jammy-security"},
		// A removal: its architecture from dpkg-query.
		{id: "libfwupdplugin5:amd64", installed: "1.7.9-1~22.04.3", classification: "other", removal: true},
		// Kept back: held, installed version and architecture from dpkg-query, no candidate.
		{id: "docker-ce:amd64", installed: "5:27.3.1-1~ubuntu.22.04~jammy", classification: "other", held: true},
		{id: "update-manager-core:all", installed: "1:22.04.20", classification: "other", held: true},
	})
}

func TestAptSimulationOfUbuntu2404DeferredPhasing(t *testing.T) {
	got := aptUpdates(parseAptSimulation(readTestdata(t, "ubuntu-24.04/apt-get-dist-upgrade.txt")), nil, nil)
	checkUpdates(t, got, 12, []wantUpdate{
		{id: "openssh-server:amd64", version: "1:9.6p1-3ubuntu13.8", installed: "1:9.6p1-3ubuntu13.5", classification: "security", origin: "noble-security"},
		{id: "systemd:amd64", version: "255.4-1ubuntu8.5", installed: "255.4-1ubuntu8.4", classification: "other", origin: "noble-updates"},
		// Without dpkg-query the architecture is the agent's own.
		{id: "python3-distupgrade:" + debianArch(), classification: "other", held: true},
	})
}

func TestAptSimulationOfDebian12(t *testing.T) {
	got := aptUpdates(parseAptSimulation(readTestdata(t, "debian-12/apt-get-dist-upgrade.txt")), nil, nil)
	checkUpdates(t, got, 9, []wantUpdate{
		{id: "libc6:amd64", version: "2.36-9+deb12u13", installed: "2.36-9+deb12u10", classification: "security", origin: "oldstable-security"},
		{id: "base-files:amd64", version: "12.4+deb12u12", installed: "12.4+deb12u11", classification: "other", origin: "oldstable"},
		{id: "linux-image-6.1.0-41-amd64:amd64", version: "6.1.153-1", classification: "security", origin: "oldstable-security"},
	})
}

func TestAptSimulationOfProxmoxVE8(t *testing.T) {
	got := aptUpdates(parseAptSimulation(readTestdata(t, "proxmox-ve-8/apt-get-dist-upgrade.txt")), nil, nil)
	checkUpdates(t, got, 7, []wantUpdate{
		{id: "pve-manager:all", version: "8.4.14", installed: "8.4.1", classification: "other", origin: "Proxmox Debian Repository:stable"},
		{id: "proxmox-kernel-6.8.12-16-pve-signed:amd64", version: "6.8.12-16", classification: "other", origin: "Proxmox Debian Repository:stable"},
		{id: "tzdata:all", version: "2025b-0+deb12u2", installed: "2025b-0+deb12u1", classification: "other", origin: "oldstable-updates"},
	})
}

func TestAptOrigins(t *testing.T) {
	for _, tc := range []struct {
		in, name string
		security bool
	}{
		{"Ubuntu:22.04/jammy-security", "jammy-security", true},
		{"Ubuntu:22.04/jammy-updates", "jammy-updates", false},
		{"UbuntuESM:22.04/jammy-infra-updates", "jammy-infra-updates", true},
		{"UbuntuESMApps:22.04/jammy-apps-security", "jammy-apps-security", true},
		{"Debian-Security:12/oldstable-security", "oldstable-security", true},
		{"Debian:12.12/oldstable", "oldstable", false},
		{"Proxmox:bookworm/pve-no-subscription", "Proxmox:pve-no-subscription", false},
		{"Proxmox Debian Repository:stable", "Proxmox Debian Repository:stable", false},
		{"Google LLC:1.0/stable", "Google LLC:stable", false},
		{"localhost", "localhost", false},
	} {
		o := parseAptOrigin(tc.in)
		if o.name() != tc.name || o.security() != tc.security {
			t.Errorf("%q: name %q security %v", tc.in, o.name(), o.security())
		}
	}
}

func TestAptInstLines(t *testing.T) {
	u := parseAptInst("foo [1.0] (1.1 Ubuntu:22.04/jammy-updates, Ubuntu:22.04/jammy-security [all])")
	if u == nil || u.Name != "foo" || u.Arch != "all" || u.Installed != "1.0" || u.Candidate != "1.1" || len(u.Origins) != 2 {
		t.Fatalf("%+v", u)
	}
	if p := u.toProto(nil, nil); p.GetClassification() != "security" || p.GetOrigin() != "jammy-security" {
		t.Fatalf("%+v", p)
	}
	for _, bad := range []string{"", "Foo [1.0] (1.1 x [all])", "foo [1.0", "foo [1.0] 1.1", "-o [1] (2 x [all])"} {
		if u := parseAptInst(bad); u != nil {
			t.Errorf("%q parsed as %+v", bad, u)
		}
	}
}

func TestAptUpdateProblems(t *testing.T) {
	if p := parseAptUpdateProblems(readTestdata(t, "ubuntu-22.04/apt-get-update.txt")); len(p) != 0 {
		t.Fatalf("a clean update has no problem: %v", p)
	}
	p := parseAptUpdateProblems(readTestdata(t, "proxmox-ve-8/apt-get-update.txt"))
	want := []string{
		"The repository enterprise.proxmox.com/debian/pve could not be read (401 Unauthorized); the scan used the other package lists.",
		"The repository enterprise.proxmox.com/debian/ceph-quincy could not be read (401 Unauthorized); the scan used the other package lists.",
		"The Proxmox enterprise repository needs a subscription: add the subscription key, or switch the host to the no-subscription repository.",
	}
	if strings.Join(p, "\n") != strings.Join(want, "\n") {
		t.Fatalf("problems =\n%s", strings.Join(p, "\n"))
	}

	lock := "Waiting for cache lock: Could not get lock /var/lib/apt/lists/lock. It is held by process 812 (apt-get)...\n" +
		"E: Could not get lock /var/lib/apt/lists/lock. It is held by process 812 (apt-get)\n" +
		"E: Unable to lock directory /var/lib/apt/lists/\n"
	if p := parseAptUpdateProblems(lock); len(p) != 1 || !strings.Contains(p[0], "held by another process (apt-get)") {
		t.Fatalf("lock: %v", p)
	}
	network := "Err:1 http://archive.ubuntu.com/ubuntu jammy InRelease\n  Temporary failure resolving 'archive.ubuntu.com'\n" +
		"W: Failed to fetch http://archive.ubuntu.com/ubuntu/dists/jammy/InRelease  Temporary failure resolving 'archive.ubuntu.com'\n" +
		"W: Some index files failed to download. They have been ignored, or old ones used instead.\n"
	if p := parseAptUpdateProblems(network); len(p) != 1 ||
		p[0] != "The repository archive.ubuntu.com/ubuntu could not be read (Temporary failure resolving 'archive.ubuntu.com'); the scan used the other package lists." {
		t.Fatalf("network: %v", p)
	}
	dpkg := "E: dpkg was interrupted, you must manually run 'dpkg --configure -a' to correct the problem.\n"
	if p := parseAptUpdateProblems(dpkg); len(p) != 1 || p[0] != problemDpkgInterrupted {
		t.Fatalf("dpkg: %v", p)
	}
	if p := parseAptUpdateProblems("E: Something else went wrong\n"); len(p) != 1 || p[0] != "apt-get update failed: Something else went wrong" {
		t.Fatalf("other: %v", p)
	}
}

func TestDpkgInterrupted(t *testing.T) {
	if dpkgInterrupted(nil) || dpkgInterrupted([]string{"tmp.i"}) {
		t.Fatal("no journal entry")
	}
	if !dpkgInterrupted([]string{"tmp.i", "0003"}) {
		t.Fatal("a numeric journal entry means dpkg was interrupted")
	}
}

func dnfUpdates(t *testing.T, dir string) []*agentv1.PatchUpdate {
	t.Helper()
	updates := parseDnfCheckUpdate(readTestdata(t, dir+"/dnf-check-update.txt"))
	if dir == "almalinux-9" {
		installed := parseRpmInstalled(readTestdata(t, dir+"/rpm-q.txt"))
		for _, u := range updates {
			u.Installed = installed[u.Name+"."+u.Arch]
		}
	}
	return dnfToProto(updates, parseDnfUpdateinfo(readTestdata(t, dir+"/dnf-updateinfo-list.txt")),
		parseDnfVersionlock(readTestdata(t, dir+"/dnf-versionlock-list.txt")))
}

func TestDnfOfAlmaLinux9(t *testing.T) {
	got := dnfUpdates(t, "almalinux-9")
	// The Obsoleting Packages section is not read.
	checkUpdates(t, got, 10, []wantUpdate{
		// The long name wraps onto the next line.
		{id: "NetworkManager-config-connectivity-redhat:noarch", version: "1:1.46.0-26.el9_4", installed: "1:1.46.0-20.el9_4",
			classification: "security", origin: "appstream", advisory: "ALSA-2024:9180", severity: "Moderate"},
		// A security advisory wins over a bugfix one; the last of several installed kernels.
		{id: "kernel:x86_64", version: "5.14.0-427.42.1.el9_4", installed: "5.14.0-427.40.1.el9_4", classification: "security", origin: "baseos",
			advisory: "ALSA-2024:9315", severity: "Important"},
		// Of two security advisories the higher severity.
		{id: "openssl:x86_64", version: "1:3.0.7-28.el9_4", installed: "1:3.0.7-27.el9", classification: "security", origin: "baseos",
			advisory: "ALSA-2024:9333", severity: "Moderate"},
		// A bugfix advisory names the advisory, not a severity; a version lock is held.
		{id: "tzdata:noarch", version: "2024b-2.el9", installed: "2024a-1.el9", classification: "other", origin: "baseos",
			advisory: "ALBA-2024:9200", held: true},
	})
}

// dnf 5 is not on any operating system Fleeto supports yet (AlmaLinux 10 ships dnf 4, captured 2026-11-05); the parsers stay tolerant of
// its representative output for when one moves to it.
func TestDnf5Representative(t *testing.T) {
	got := dnfUpdates(t, "dnf5")
	checkUpdates(t, got, 7, []wantUpdate{
		{id: "kernel-modules-core:x86_64_v2", version: "6.12.0-55.40.1.el10_0", classification: "security", origin: "baseos",
			advisory: "ALSA-2025:9878", severity: "Important"},
		{id: "openssl:x86_64_v2", version: "1:3.2.2-16.el10_0.4", classification: "security", origin: "baseos",
			advisory: "ALSA-2025:7425", severity: "Moderate"},
		{id: "python3-libdnf5:x86_64_v2", version: "5.2.8.1-4.el10_0.alma.1", classification: "other", origin: "baseos",
			advisory: "ALBA-2025:8102", held: true},
	})
}

func TestDnfCheckUpdateIgnoresNoise(t *testing.T) {
	out := "Last metadata expiration check: 0:00:01 ago on Wed 04 Nov 2026 10:00:00 AM UTC.\n" +
		"Security: kernel-core-5.14.0-427.42.1.el9_4.x86_64 is an installed security update\n" +
		"\n" +
		"bash.x86_64      5.1.8-9.el9     baseos\n" +
		"broken line\n" +
		"too.many.x86_64 1 2 3\n"
	got := parseDnfCheckUpdate(out)
	if len(got) != 1 || got[0].Name != "bash" || got[0].Arch != "x86_64" || got[0].Candidate != "5.1.8-9.el9" {
		t.Fatalf("%+v", got)
	}
}

func TestNEVRA(t *testing.T) {
	for _, tc := range []struct{ in, name, arch string }{
		{"openssl-1:3.0.7-25.el9_3.x86_64", "openssl", "x86_64"},
		{"NetworkManager-config-connectivity-redhat-1:1.46.0-26.el9_4.noarch", "NetworkManager-config-connectivity-redhat", "noarch"},
		{"kernel-6.12.0-55.40.1.el10_0.x86_64_v2", "kernel", "x86_64_v2"},
	} {
		if name, arch, ok := parseNEVRA(tc.in); !ok || name != tc.name || arch != tc.arch {
			t.Errorf("%s: %q %q %v", tc.in, name, arch, ok)
		}
	}
	for _, bad := range []string{"Important/Sec.", "bugfix", "2025-07-01", "foo.x86_64"} {
		if _, _, ok := parseNEVRA(bad); ok {
			t.Errorf("%q parsed", bad)
		}
	}
}

func TestDnfRepoProblems(t *testing.T) {
	got := parseDnfRepoProblems(readTestdata(t, "almalinux-9/dnf-check-update-repo-error.stderr.txt"))
	if len(got) != 1 || got[0] != "epel" {
		t.Fatalf("%v", got)
	}
	dnf5 := `>>> Curl error (6): Could not resolve host name for https://mirrors.almalinux.org/mirrorlist/10/baseos [Could not resolve host]` + "\n" +
		`Failed to download metadata (metalink: "https://mirrors.almalinux.org/mirrorlist/10/baseos") for repository "baseos"` + "\n"
	if got := parseDnfRepoProblems(dnf5); len(got) != 1 || got[0] != "baseos" {
		t.Fatalf("dnf 5: %v", got)
	}
}

func TestFinalizeBoundsTheReport(t *testing.T) {
	r := &agentv1.PatchScanReport{ScanId: "not-a-uuid", Problem: strings.Repeat("é", 800), Engine: "apt"}
	for i := range MaxUpdates + 5 {
		class := "other"
		if i == MaxUpdates+4 {
			class = "security"
		}
		r.Updates = append(r.Updates, &agentv1.PatchUpdate{
			Id: "pkg" + strings.Repeat("x", i%3) + ":" + strconv.Itoa(i), Name: "pkg\x00" + strings.Repeat("n", 300),
			Version: "1\xff", Classification: class,
		})
	}
	r.Updates = append(r.Updates, &agentv1.PatchUpdate{Id: "empty:all"})
	Finalize(r)
	if !scanIDPattern.MatchString(r.GetScanId()) || !r.GetTruncated() || len(r.GetUpdates()) != MaxUpdates {
		t.Fatalf("scan id %q, truncated %v, %d updates", r.GetScanId(), r.GetTruncated(), len(r.GetUpdates()))
	}
	if len(r.GetProblem()) > maxProblemLen || !strings.HasSuffix(r.GetProblem(), "é") {
		t.Fatalf("problem of %d bytes", len(r.GetProblem()))
	}
	first := r.GetUpdates()[0]
	if first.GetClassification() != "security" || len(first.GetName()) > maxNameLen || strings.ContainsRune(first.GetName(), 0) ||
		first.GetVersion() != "1" {
		t.Fatalf("first = %+v", first)
	}
}
