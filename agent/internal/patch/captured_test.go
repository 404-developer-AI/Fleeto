package patch

import "testing"

// Captured output of the test endpoints (2026-11-04, LC_ALL=C), in testdata/captured: the parsers must read what the real tools print,
// not only the representative files next to them.

func TestCapturedUbuntu2404(t *testing.T) {
	sim := parseAptSimulation(readTestdata(t, "captured/ubuntu-24.04/apt-get-dist-upgrade.txt"))
	got := aptUpdates(sim, nil, nil)
	// 10 upgraded and libsqlite3-0 deferred due to phasing; Conf lines and the autoremove hint are not updates.
	checkUpdates(t, got, 11, []wantUpdate{
		{id: "libapt-pkg6.0t64:amd64", version: "2.8.4ubuntu0.1", installed: "2.8.3", classification: "security", origin: "noble-security"},
		// The trailing "[apt-utils:amd64 ]" of a package apt breaks and re-installs is not part of the origin.
		{id: "apt:amd64", version: "2.8.4ubuntu0.1", installed: "2.8.3", classification: "security", origin: "noble-security"},
		// Two origins: security wins.
		{id: "libpcre2-8-0:amd64", version: "10.42-4ubuntu2.2", installed: "10.42-4ubuntu2.1", classification: "security", origin: "noble-security"},
		{id: "bind9-dnsutils:amd64", version: "1:9.18.39-0ubuntu0.24.04.9", installed: "1:9.18.39-0ubuntu0.24.04.8", classification: "security",
			origin: "noble-security"},
		{id: "linux-firmware-amd-graphics:all", version: "20240318.git3b128b60-0ubuntu3.4", installed: "20240318.git3b128b60-0ubuntu3.3",
			classification: "other", origin: "noble-updates"},
		{id: "python3-twisted:all", version: "24.3.0-1ubuntu0.3", installed: "24.3.0-1ubuntu0.2", classification: "security", origin: "noble-security"},
		// Deferred due to phasing: apt leaves it alone, without a candidate in the simulation.
		{id: "libsqlite3-0:" + debianArch(), classification: "other", held: true},
	})
	if holds := parseAptMarkShowhold(readTestdata(t, "captured/ubuntu-24.04/apt-mark-showhold.txt")); len(holds) != 0 {
		t.Fatalf("no package is held: %v", holds)
	}
}

func TestCapturedProxmoxVE8(t *testing.T) {
	got := aptUpdates(parseAptSimulation(readTestdata(t, "captured/proxmox-ve-8/apt-get-dist-upgrade.txt")), nil, nil)
	checkUpdates(t, got, 9, []wantUpdate{
		{id: "libapt-pkg6.0:amd64", version: "2.6.2", installed: "2.6.1", classification: "security", origin: "oldstable-security"},
		{id: "tzdata:all", version: "2026f-0+deb12u1", installed: "2026e-0+deb12u1", classification: "security", origin: "oldstable-security"},
		// An epoch in both versions, and the empty trailing "[]".
		{id: "bind9-libs:amd64", version: "1:9.18.49-1~deb12u3", installed: "1:9.18.49-1~deb12u2", classification: "security", origin: "oldstable-security"},
		{id: "libsasl2-2:amd64", version: "2.1.28+dfsg-10+deb12u1", installed: "2.1.28+dfsg-10", classification: "security", origin: "oldstable-security"},
	})
	// A Proxmox VE host on the no-subscription repository with every list read: no problem.
	if p := parseAptUpdateProblems(readTestdata(t, "captured/proxmox-ve-8/apt-get-update.txt")); len(p) != 0 {
		t.Fatalf("a clean update has no problem: %v", p)
	}
}

func TestCapturedAlmaLinux9(t *testing.T) {
	updates := parseDnfCheckUpdate(readTestdata(t, "captured/almalinux-9/dnf-check-update.txt"))
	got := dnfToProto(updates, parseDnfUpdateinfo(readTestdata(t, "captured/almalinux-9/dnf-updateinfo-list.txt")), nil)
	// The output starts with an empty line; repository names with a suffix ("-proxy") are kept as they are.
	checkUpdates(t, got, 10, []wantUpdate{
		{id: "kernel:x86_64", version: "5.14.0-687.65.1.el9_8", classification: "security", origin: "baseos-proxy", advisory: "ALSA-2026:93930",
			severity: "Moderate"},
		{id: "kernel-tools-libs:x86_64", version: "5.14.0-687.65.1.el9_8", classification: "security", origin: "baseos-proxy",
			advisory: "ALSA-2026:93930", severity: "Moderate"},
		// An epoch in check-update and in the package of the advisory.
		{id: "qemu-guest-agent:x86_64", version: "17:10.1.0-17.el9_8.9.alma.1", classification: "security", origin: "appstream-proxy",
			advisory: "ALSA-2026:88421", severity: "Important"},
		// No advisory: a regular update.
		{id: "python3-perf:x86_64", version: "5.14.0-687.65.1.el9_8", classification: "other", origin: "appstream-proxy"},
		{id: "tzdata:noarch", version: "2026f-1.el9_8", classification: "other", origin: "baseos-proxy"},
		{id: "tzdata-java:noarch", version: "2026f-1.el9_8", classification: "other", origin: "appstream-proxy"},
	})
}

func TestCapturedUbuntu2404Update(t *testing.T) {
	if p := parseAptUpdateProblems(readTestdata(t, "captured/ubuntu-24.04/apt-get-update.txt")); len(p) != 0 {
		t.Fatalf("a clean update has no problem: %v", p)
	}
}

// AlmaLinux 10 ships dnf 4 (not dnf 5): the output has the same shape as AlmaLinux 9.
func TestCapturedAlmaLinux10(t *testing.T) {
	updates := parseDnfCheckUpdate(readTestdata(t, "captured/almalinux-10/dnf-check-update.txt"))
	got := dnfToProto(updates, parseDnfUpdateinfo(readTestdata(t, "captured/almalinux-10/dnf-updateinfo-list.txt")), nil)
	checkUpdates(t, got, 29, []wantUpdate{
		// A long name that still fits on its line.
		{id: "pipewire-jack-audio-connection-kit-libs:x86_64", version: "1.4.11-1.el10_2.2", classification: "security", origin: "appstream",
			advisory: "ALSA-2026:93442", severity: "Moderate"},
		// An epoch in check-update and in the package of the advisory.
		{id: "java-21-openjdk-headless:x86_64", version: "1:21.0.13.0.9-1.2.el10.alma.1", classification: "security", origin: "appstream",
			advisory: "ALSA-2026:90504", severity: "Important"},
		{id: "util-linux-core:x86_64", version: "2.40.2-18.el10_2.20", classification: "security", origin: "baseos", advisory: "ALSA-2026:93461",
			severity: "Important"},
		{id: "kernel:x86_64", version: "6.12.0-211.73.1.el10_2", classification: "security", origin: "baseos", advisory: "ALSA-2026:93911",
			severity: "Moderate"},
		// Firmware without an advisory: a regular update.
		{id: "linux-firmware:noarch", version: "20261026-23.5.el10_2", classification: "other", origin: "baseos"},
	})
}
