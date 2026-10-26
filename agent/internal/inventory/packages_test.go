package inventory

import (
	"slices"
	"strings"
	"testing"
)

// An InRelease file of the Ubuntu archive as apt stores it: the signature header and an empty line before the fields.
const ubuntuInRelease = `-----BEGIN PGP SIGNED MESSAGE-----
Hash: SHA512

Origin: Ubuntu
Label: Ubuntu
Suite: jammy-updates
Codename: jammy
Date: Mon, 26 Oct 2026 10:00:00 UTC
MD5Sum:
 0123456789abcdef 1234 main/binary-amd64/Packages
-----BEGIN PGP SIGNATURE-----
`

func TestAptReleaseOriginReadsTheOriginPastTheSignatureHeader(t *testing.T) {
	if got := aptReleaseOrigin(strings.NewReader(ubuntuInRelease)); got != "Ubuntu" {
		t.Errorf("origin = %q, want Ubuntu", got)
	}
	if got := aptReleaseOrigin(strings.NewReader("Label: flat\nMD5Sum:\n Origin: in the file list\n")); got != "" {
		t.Errorf("origin = %q, want none for a release file without one", got)
	}
}

func TestAptReleaseFileForNamesTheReleaseFilesOfAList(t *testing.T) {
	got := aptReleaseFileFor("archive.ubuntu.com_ubuntu_dists_jammy-updates_main_binary-amd64_Packages")
	want := []string{"archive.ubuntu.com_ubuntu_dists_jammy-updates_InRelease", "archive.ubuntu.com_ubuntu_dists_jammy-updates_Release"}
	if !slices.Equal(got, want) {
		t.Errorf("got %v, want %v", got, want)
	}
	if got := aptReleaseFileFor("ppa.example_Packages"); got != nil {
		t.Errorf("a flat repository has no release file by this pattern, got %v", got)
	}
}

func TestReadAptPackagesIndexesEveryStanza(t *testing.T) {
	idx := aptIndex{}
	packages := "Package: openssh-server\nArchitecture: amd64\nVersion: 1:8.9p1-3ubuntu0.10\nSource: openssh\n\n" +
		"Package: nginx\nVersion: 1.18.0-6ubuntu14.6\n"
	if err := idx.readAptPackages(strings.NewReader(packages), "Ubuntu"); err != nil {
		t.Fatal(err)
	}
	if len(idx) != 2 || idx["nginx"][0] != (aptCandidate{Origin: "Ubuntu", Version: "1.18.0-6ubuntu14.6"}) {
		t.Errorf("unexpected index: %v", idx)
	}
}

func TestAptOriginDecidesByNameAndInstalledVersion(t *testing.T) {
	idx := aptIndex{
		"openssh-server": {{"Ubuntu", "1:8.9p1-3ubuntu0.13"}},
		// nginx from nginx.org next to the distribution's.
		"nginx": {{"Ubuntu", "1.18.0-6ubuntu14.6"}, {"nginx", "1.26.2-1~jammy"}},
		// docker-ce only from Docker.
		"docker-ce":   {{"Docker", "5:27.3.1-1~ubuntu.22.04~jammy"}},
		"libssl3":     {{"Ubuntu", "3.0.2-0ubuntu1.18"}, {"UbuntuESM", "3.0.2-0ubuntu1.18+esm1"}},
		"proxmox-ve":  {{"Proxmox", "8.2.0"}},
		"pve-manager": {{"Proxmox", "8.2.4"}},
	}
	cases := []struct{ name, installed, want string }{
		// The installed version is outdated and in no list: only the distribution offers the name.
		{"openssh-server", "1:8.9p1-3ubuntu0.6", "Ubuntu"},
		// The version of nginx.org is installed: it belongs to nginx.
		{"nginx", "1.26.2-1~jammy", "nginx"},
		// An outdated version of the distribution's nginx.
		{"nginx", "1.18.0-6ubuntu14.4", "Ubuntu"},
		// Newer than anything the distribution has, and not in a list any more: the other origin.
		{"nginx", "1.27.0-1~jammy", "nginx"},
		{"docker-ce", "5:26.1.0-1~ubuntu.22.04~jammy", "Docker"},
		// The ESM version that is installed.
		{"libssl3", "3.0.2-0ubuntu1.18+esm1", "UbuntuESM"},
		{"pve-manager", "8.2.2", "Proxmox"},
		// Installed from a file.
		{"my-tool", "1.0", localOrigin},
	}
	for _, c := range cases {
		if got := aptOrigin(idx, c.name, c.installed); got != c.want {
			t.Errorf("aptOrigin(%s %s) = %q, want %q", c.name, c.installed, got, c.want)
		}
	}
}

func TestSplitSourceRPMReadsNameAndVersionRelease(t *testing.T) {
	cases := []struct{ value, name, version string }{
		{"openssh-8.7p1-38.el9_4.1.src.rpm", "openssh", "8.7p1-38.el9_4.1"},
		{"python3.11-3.11.7-1.el9.src.rpm", "python3.11", "3.11.7-1.el9"},
		{"nginx-1.20.1-14.module_el8.7.0+3249+3b1c1f0b.alma.src.rpm", "nginx", "1.20.1-14.module_el8.7.0+3249+3b1c1f0b.alma"},
		{"(none)", "", ""},
		{"broken.src.rpm", "", ""},
	}
	for _, c := range cases {
		name, version := splitSourceRPM(c.value)
		if name != c.name || version != c.version {
			t.Errorf("splitSourceRPM(%q) = %q %q, want %q %q", c.value, name, version, c.name, c.version)
		}
	}
}

func TestCompareDpkgFollowsDpkg(t *testing.T) {
	cases := []struct {
		a, b string
		want int
	}{
		{"1:8.9p1-3ubuntu0.6", "1:8.9p1-3ubuntu0.10", -1},
		{"1.0~rc1", "1.0", -1},
		{"1.0", "1.0-0", 0},
		{"1:1.0", "2.0", 1},
		{"1.26.2-1~jammy", "1.18.0-6ubuntu14.6", 1},
		{"2.4.52-1ubuntu4.12", "2.4.52-1ubuntu4.12+esm1", -1},
	}
	for _, c := range cases {
		if got := compareDpkg(c.a, c.b); got != c.want {
			t.Errorf("compareDpkg(%s, %s) = %d, want %d", c.a, c.b, got, c.want)
		}
	}
	if dpkgEpoch("1:8.9p1-3") != 1 || dpkgEpoch("8.9p1-3") != 0 {
		t.Error("dpkgEpoch")
	}
}

func TestParseProStatusListsTheEnabledServices(t *testing.T) {
	data := []byte(`{"attached": true, "services": [{"name": "esm-infra", "status": "enabled"}, {"name": "livepatch", "status": "disabled"},
		{"name": "esm-apps", "status": "enabled"}]}`)
	if got := parseProStatus(data); !slices.Equal(got, []string{"esm-apps", "esm-infra"}) {
		t.Errorf("got %v", got)
	}
	if got := parseProStatus([]byte("not json")); got != nil {
		t.Errorf("got %v for an unreadable file", got)
	}
}

func TestParseLivepatchReadsThePatchedCvesOfTheRunningKernel(t *testing.T) {
	data := []byte(`{"Client-Version": "10.8.3", "Status": [{"Kernel": "5.15.0-91.101-generic", "Running": true,
		"Livepatch": {"CheckState": "checked", "State": "applied", "Version": "105.1",
		"Fixes": [{"Name": "cve-2024-1086", "Description": "nf_tables", "Patched": true},
		          {"Name": "cve-2024-26581", "Patched": false},
		          {"Name": "CVE-2023-52447 and cve-2024-1086", "Patched": true}]}}]}`)
	active, fixes := parseLivepatch(data)
	if !active || !slices.Equal(fixes, []string{"CVE-2023-52447", "CVE-2024-1086"}) {
		t.Errorf("got %v %v", active, fixes)
	}
	if active, _ := parseLivepatch([]byte(`{"Status": [{"Running": true, "Livepatch": {"State": "nothing-to-apply"}}]}`)); active {
		t.Error("nothing applied is not active")
	}
}
