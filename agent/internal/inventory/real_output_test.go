package inventory

import (
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Parser tests on the real output of test endpoints (captured 2026-10-26 with the commands of the agent, MD-Files/VULNERABILITIES.md
// §12): Ubuntu 24.04 with a restart pending since kernel 6.8.0-53, Proxmox VE 8.4 on Debian 12 with the no-subscription repository,
// and AlmaLinux 8.5 converted from CentOS 8, with packages of the early AlmaLinux builds (vendor CloudLinux), EPEL, Zabbix and module
// streams. The apt lists keep only the Package and Version lines of the installed packages and the Release header without its file
// list.

func readTestdata(t *testing.T, path string) string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join("testdata", path))
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func byName(items []*agentv1.SoftwareItem) map[string][]*agentv1.SoftwareItem {
	result := map[string][]*agentv1.SoftwareItem{}
	for _, item := range items {
		result[item.GetName()] = append(result[item.GetName()], item)
	}
	return result
}

func TestParseDpkgOnRealUbuntu2404Output(t *testing.T) {
	items := parseDpkg(readTestdata(t, "ubuntu-24.04/dpkg-query.txt"))
	// 760 lines, 57 of them removed packages whose configuration stayed ("rc"): only the 703 installed ones count.
	if len(items) != 703 {
		t.Fatalf("want 703 installed packages, got %d", len(items))
	}
	packages := byName(items)
	if _, ok := packages["linux-image-6.8.0-100-generic"]; ok {
		t.Error("a removed kernel (rc) must not be listed")
	}

	// A binary with an epoch of its own, built from a source without one: matching uses the source version.
	bsdutils := packages["bsdutils"][0]
	if bsdutils.GetVersion() != "1:2.39.3-9ubuntu6.6" || bsdutils.GetEpoch() != 1 || bsdutils.GetSourceName() != "util-linux" ||
		bsdutils.GetSourceVersion() != "2.39.3-9ubuntu6.6" || bsdutils.GetArchitecture() != "amd64" {
		t.Errorf("bsdutils: %+v", bsdutils)
	}
	// The running kernel and the newest one are both installed.
	running := strings.TrimSpace(readTestdata(t, "ubuntu-24.04/uname-r.txt"))
	if image := packages["linux-image-"+running]; len(image) != 1 || image[0].GetSourceName() != "linux-signed" || image[0].GetSourceVersion() != "6.8.0-53.55" {
		t.Errorf("running kernel %s: %+v", running, image)
	}
	if _, ok := packages["linux-image-6.8.0-147-generic"]; !ok {
		t.Error("the newest kernel is installed")
	}
	for _, item := range items {
		if strings.Contains(item.GetPublisher(), "@") {
			t.Fatalf("%s: the maintainer's email address must be left out: %q", item.GetName(), item.GetPublisher())
		}
		if item.GetSourceName() == "" || item.GetSourceVersion() == "" || item.GetArchitecture() == "" {
			t.Fatalf("%s: source and architecture are always printed: %+v", item.GetName(), item)
		}
	}
}

// aptTestIndex reads the apt lists of a capture as readAptLists does, and returns the index with the origin of every list.
func aptTestIndex(t *testing.T, capture string) (aptIndex, map[string]string) {
	t.Helper()
	dir := filepath.Join("testdata", capture, "apt-lists")
	entries, err := os.ReadDir(dir)
	if err != nil {
		t.Fatal(err)
	}
	idx := aptIndex{}
	origins := map[string]string{}
	for _, entry := range entries {
		if !strings.HasSuffix(entry.Name(), "_Packages") {
			continue
		}
		origin := ""
		for _, release := range aptReleaseFileFor(entry.Name()) {
			if file, err := os.Open(filepath.Join(dir, release)); err == nil {
				origin = aptReleaseOrigin(file)
				_ = file.Close()
				if origin != "" {
					break
				}
			}
		}
		origins[entry.Name()] = origin
		file, err := os.Open(filepath.Join(dir, entry.Name()))
		if err != nil {
			t.Fatal(err)
		}
		if err := idx.readAptPackages(file, origin); err != nil {
			t.Fatal(err)
		}
		_ = file.Close()
	}
	return idx, origins
}

func TestAptOriginOnRealUbuntu2404Lists(t *testing.T) {
	idx, origins := aptTestIndex(t, "ubuntu-24.04")
	if len(origins) != 15 {
		t.Fatalf("want the 15 lists of main, restricted, universe and multiverse in noble, -updates, -backports and -security, got %d", len(origins))
	}
	for list, origin := range origins {
		if origin != "Ubuntu" {
			t.Errorf("%s: origin %q, want Ubuntu from its InRelease file", list, origin)
		}
	}

	// Every installed package came from Ubuntu, the running kernel of a release long superseded included: Ubuntu keeps the older
	// kernel ABIs in its lists.
	for _, item := range parseDpkg(readTestdata(t, "ubuntu-24.04/dpkg-query.txt")) {
		if origin := aptOrigin(idx, item.GetName(), item.GetVersion()); origin != "Ubuntu" {
			t.Errorf("%s %s: origin %q, want Ubuntu", item.GetName(), item.GetVersion(), origin)
		}
	}
}

func TestParseDpkgAndAptOriginOnRealProxmoxVe84Output(t *testing.T) {
	if version := parsePveVersion(readTestdata(t, "proxmox-ve-8.4/pveversion.txt")); version != "8.4.5" {
		t.Errorf("pve-manager version %q, want 8.4.5", version)
	}
	items := parseDpkg(readTestdata(t, "proxmox-ve-8.4/dpkg-query.txt"))
	if len(items) != 716 {
		t.Fatalf("want 716 installed packages, got %d", len(items))
	}

	idx, origins := aptTestIndex(t, "proxmox-ve-8.4")
	for list, origin := range origins {
		want := "Debian"
		if strings.HasPrefix(list, "download.proxmox.com_") {
			want = "Proxmox"
		}
		if origin != want {
			t.Errorf("%s: origin %q, want %s", list, origin, want)
		}
	}

	counts := map[string]int{}
	got := map[string]string{}
	for _, item := range items {
		origin := aptOrigin(idx, item.GetName(), item.GetVersion())
		counts[origin]++
		got[item.GetName()] = origin
	}
	// Nothing local: every installed package is offered by Debian or Proxmox.
	if counts["Debian"] != 613 || counts["Proxmox"] != 103 || len(counts) != 2 {
		t.Errorf("origins %v, want 613 Debian and 103 Proxmox", counts)
	}
	for name, want := range map[string]string{
		// Debian's own packages.
		"openssh-server": "Debian", "libc6": "Debian", "linux-base": "Debian",
		// Packages Debian offers too, which Proxmox replaced with its own build (+pmx, -pve): the installed version decides.
		"grub-common": "Proxmox", "open-iscsi": "Proxmox", "smartmontools": "Proxmox", "frr": "Proxmox",
		// The management stack and the kernel of Proxmox VE, which no Debian feed covers (VULNERABILITIES.md §4).
		"pve-manager": "Proxmox", "pve-qemu-kvm": "Proxmox", "proxmox-kernel-6.8.12-12-pve-signed": "Proxmox",
	} {
		if got[name] != want {
			t.Errorf("%s: origin %q, want %s", name, got[name], want)
		}
	}
}

func TestParseRpmOnRealAlmaLinux85Output(t *testing.T) {
	items := parseRpm(readTestdata(t, "almalinux-8.5/rpm-qa.txt"))
	if len(items) != 814 {
		t.Fatalf("want 814 packages, got %d", len(items))
	}
	packages := byName(items)
	vendors := map[string]int{}
	for _, item := range items {
		vendors[item.GetOrigin()]++
	}
	// The early AlmaLinux 8 builds carry the vendor CloudLinux; packages left from CentOS 8, EPEL (Fedora Project) and Zabbix
	// (no vendor) are not AlmaLinux's.
	want := map[string]int{"AlmaLinux": 465, "CloudLinux": 322, "CentOS": 6, "Fedora Project": 14, "": 7}
	for vendor, count := range want {
		if vendors[vendor] != count {
			t.Errorf("vendor %q: want %d packages, got %d", vendor, count, vendors[vendor])
		}
	}

	podman := packages["podman"][0]
	// A package of the container-tools:rhel8 stream, without %{MODULARITYLABEL}: the server takes the stream from the enabled
	// modules (dnfModuleStreams) instead.
	if podman.GetVersion() != "3.3.1-9.module_el8.5.0+2586+018f24d7" || podman.GetModuleStream() != "" || podman.GetOrigin() != "AlmaLinux" ||
		podman.GetSourceName() != "podman" || podman.GetSourceVersion() != "3.3.1-9.module_el8.5.0+2586+018f24d7" || podman.GetArchitecture() != "x86_64" {
		t.Errorf("podman: %+v", podman)
	}
	if libtheora := packages["libtheora"][0]; libtheora.GetEpoch() != 1 || libtheora.GetOrigin() != "CloudLinux" {
		t.Errorf("libtheora: %+v", libtheora)
	}
	// One kernel of AlmaLinux and two left from CentOS; matching takes the running one.
	if kernels := packages["kernel"]; len(kernels) != 3 {
		t.Errorf("want 3 kernels, got %d", len(kernels))
	}
	if zabbix := packages["zabbix-agent2"][0]; zabbix.GetOrigin() != "" || zabbix.GetSourceName() != "zabbix" {
		t.Errorf("zabbix-agent2: %+v", zabbix)
	}
	// gpg-pubkey is no package of a repository: no architecture, no source.
	for _, key := range packages["gpg-pubkey"] {
		if key.GetArchitecture() != "" || key.GetSourceName() != "" {
			t.Errorf("gpg-pubkey: %+v", key)
		}
	}
}

func TestParseDnfModuleListsTheEnabledStreams(t *testing.T) {
	// The shape dnf writes to /etc/dnf/modules.d/<module>.module; the enabled streams of the AlmaLinux 8.5 test endpoint
	// ("dnf module list --enabled" in testdata) are container-tools:rhel8, virt:rhel, perl:5.26 and others.
	files := map[string]string{
		"container-tools.module": "[container-tools]\nname=container-tools\nstream=rhel8\nprofiles=\nstate=enabled\n",
		"virt.module":            "[virt]\r\nname=virt\r\nstream=rhel\r\nprofiles=\r\nstate=enabled\r\n",
		"nodejs.module":          "[nodejs]\nname=nodejs\nstream=\nprofiles=\nstate=disabled\n",
		"php.module":             "[php]\nname=php\nstream=7.4\nprofiles=\nstate=\n",
	}
	var streams []string
	for _, name := range []string{"container-tools.module", "nodejs.module", "php.module", "virt.module"} {
		streams = append(streams, parseDnfModule(strings.NewReader(files[name]))...)
	}
	if strings.Join(streams, ",") != "container-tools:rhel8,virt:rhel" {
		t.Errorf("got %v", streams)
	}
	if !strings.Contains(readTestdata(t, "almalinux-8.5/dnf-modules.txt"), "container-tools      rhel8 [d][e]") {
		t.Error("the test endpoint has container-tools:rhel8 enabled")
	}
}
