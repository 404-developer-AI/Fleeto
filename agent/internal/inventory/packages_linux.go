//go:build linux

package inventory

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"slices"
	"strings"
	"sync"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// aptListsDir holds the package lists apt downloaded.
const aptListsDir = "/var/lib/apt/lists"

// The apt lists are parsed again only when one of their files changed (name, size or modification time): an inventory run without
// an apt update reuses the index.
var (
	aptMu          sync.Mutex
	aptFingerprint string
	aptCached      aptLists
)

// aptLists is what the agent knows of the apt lists: the index, whether there were any, and when the newest was written.
type aptLists struct {
	Index   aptIndex
	State   string // "ok", "missing" or "unreadable"
	Updated time.Time
}

// readAptLists returns the index of the apt lists, from the cache while no list changed.
func readAptLists() aptLists {
	aptMu.Lock()
	defer aptMu.Unlock()
	entries, err := os.ReadDir(aptListsDir)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return aptLists{State: "missing"}
		}
		return aptLists{State: "unreadable"}
	}
	var fingerprint strings.Builder
	var packages []string
	var newest time.Time
	for _, entry := range entries {
		name := entry.Name()
		if entry.IsDir() || !strings.HasSuffix(name, "_Packages") {
			continue
		}
		info, err := entry.Info()
		if err != nil {
			continue
		}
		packages = append(packages, name)
		fmt.Fprintf(&fingerprint, "%s %d %d|", name, info.ModTime().UnixNano(), info.Size())
		if info.ModTime().After(newest) {
			newest = info.ModTime()
		}
	}
	if len(packages) == 0 {
		// Minimal images remove the lists; "not covered" for every package would be untrue, so the server says to run apt update.
		return aptLists{State: "missing"}
	}
	if fingerprint.String() == aptFingerprint && aptCached.Index != nil {
		return aptCached
	}

	index := aptIndex{}
	for _, name := range packages {
		origin := ""
		for _, release := range aptReleaseFileFor(name) {
			if file, err := os.Open(filepath.Join(aptListsDir, release)); err == nil { // #nosec G304 -- names from the apt lists directory.
				origin = aptReleaseOrigin(file)
				_ = file.Close()
				if origin != "" {
					break
				}
			}
		}
		if origin == "" {
			// A list without a Release file or Origin (a flat repository) is not the distribution's.
			origin = "unknown"
		}
		file, err := os.Open(filepath.Join(aptListsDir, name)) // #nosec G304 -- names from the apt lists directory.
		if err != nil {
			continue
		}
		_ = index.readAptPackages(file, origin)
		_ = file.Close()
	}
	aptFingerprint = fingerprint.String()
	aptCached = aptLists{Index: index, State: "ok", Updated: newest}
	return aptCached
}

// packageInfo describes the distribution and the state of its package manager for vulnerability matching (0.8.0).
func packageInfo(ctx context.Context) *agentv1.PackageInfo {
	release := osRelease()
	info := &agentv1.PackageInfo{DistributionId: release["ID"], DistributionVersion: release["VERSION_ID"]}
	if version := proxmoxVersion(ctx); version != "" {
		info.DistributionVariant = "proxmox-ve"
		info.DistributionVariantVersion = version
	}
	switch {
	case lookPath("dpkg-query"):
		info.PackageManager = "dpkg"
		lists := readAptLists()
		info.PackageLists = lists.State
		if !lists.Updated.IsZero() {
			info.PackageListsUpdatedAt = timestamppb.New(lists.Updated)
		}
		_, err := os.Stat("/var/run/reboot-required")
		info.RestartRequired = err == nil
		info.UbuntuProServices = ubuntuProServices()
		info.LivepatchActive, info.LivepatchFixes = livepatch(ctx)
	case lookPath("rpm"):
		info.PackageManager = "rpm"
		info.RestartRequired = rpmRestartRequired(ctx)
		info.LivepatchActive = kpatchActive(ctx)
		info.ModuleStreams = dnfModuleStreams()
	}
	return info
}

func lookPath(name string) bool {
	_, err := exec.LookPath(name)
	return err == nil
}

// ubuntuProServices lists the Ubuntu Pro services that are enabled, from the status file the pro client writes.
func ubuntuProServices() []string {
	data, err := os.ReadFile("/var/lib/ubuntu-advantage/status.json")
	if err != nil {
		return nil
	}
	return parseProStatus(data)
}

// livepatch reports whether Canonical Livepatch patched the running kernel, and the CVEs it fixed.
func livepatch(ctx context.Context) (bool, []string) {
	tool, err := exec.LookPath("canonical-livepatch")
	if err != nil {
		return false, nil
	}
	out, err := output(ctx, tool, "status", "--format", "json")
	if err != nil {
		return false, nil
	}
	return parseLivepatch([]byte(out))
}

// rpmRestartRequired asks needs-restarting (dnf-utils) whether the endpoint needs a restart: exit code 1 means it does.
func rpmRestartRequired(ctx context.Context) bool {
	name, args := "needs-restarting", []string{"-r"}
	if !lookPath(name) {
		if !lookPath("dnf") {
			return false
		}
		name, args = "dnf", []string{"needs-restarting", "-r"}
	}
	ctx, cancel := context.WithTimeout(ctx, commandTimeout)
	defer cancel()
	err := exec.CommandContext(ctx, name, args...).Run() // #nosec G204 -- fixed command and arguments.
	var exit *exec.ExitError
	return errors.As(err, &exit) && exit.ExitCode() == 1
}

// dnfModulesDir holds a file per module whose stream dnf enabled (RHEL family 8 and later).
const dnfModulesDir = "/etc/dnf/modules.d"

// dnfModuleStreams lists the enabled module streams as "name:stream", sorted; nil without the directory (no modules).
func dnfModuleStreams() []string {
	entries, err := os.ReadDir(dnfModulesDir)
	if err != nil {
		return nil
	}
	var streams []string
	for _, entry := range entries {
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".module") {
			continue
		}
		file, err := os.Open(filepath.Join(dnfModulesDir, entry.Name())) // #nosec G304 -- names from the dnf modules directory.
		if err != nil {
			continue
		}
		streams = append(streams, parseDnfModule(file)...)
		_ = file.Close()
	}
	slices.Sort(streams)
	return slices.Compact(streams)
}

// kpatchActive: kpatch lists at least one loaded patch module.
func kpatchActive(ctx context.Context) bool {
	tool, err := exec.LookPath("kpatch")
	if err != nil {
		return false
	}
	out, err := output(ctx, tool, "list")
	if err != nil {
		return false
	}
	_, loaded, ok := strings.Cut(out, "Loaded patch modules:")
	if !ok {
		return false
	}
	before, _, _ := strings.Cut(loaded, "Installed patch modules:")
	return strings.TrimSpace(before) != ""
}
