package inventory

import (
	"bufio"
	"encoding/json"
	"io"
	"regexp"
	"slices"
	"strconv"
	"strings"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// The parts of the package inventory that vulnerability matching needs (0.8.0, MD-Files/VULNERABILITIES.md §6) and that do not
// depend on the platform, so they are tested everywhere.

// distributionOrigins are the apt origins whose packages the security feeds of Ubuntu and Debian cover.
var distributionOrigins = map[string]bool{"Ubuntu": true, "UbuntuESM": true, "UbuntuESMApps": true, "Debian": true}

// localOrigin is the origin of a package that is in no apt list: installed from a file, or from a repository that was removed.
const localOrigin = "local"

// aptCandidate is one version of a package name in one apt list, with the origin of that list.
type aptCandidate struct {
	Origin  string
	Version string
}

// aptIndex maps a package name to the versions the apt lists offer, with their origin.
type aptIndex map[string][]aptCandidate

// readAptPackages adds the Package and Version of every stanza of a Packages file to the index under the origin of its list.
func (idx aptIndex) readAptPackages(r io.Reader, origin string) error {
	scanner := bufio.NewScanner(r)
	scanner.Buffer(make([]byte, 0, 64*1024), 4*1024*1024)
	var name, version string
	flush := func() {
		if name != "" && version != "" {
			idx[name] = append(idx[name], aptCandidate{Origin: origin, Version: version})
		}
		name, version = "", ""
	}
	for scanner.Scan() {
		line := scanner.Text()
		switch {
		case line == "":
			flush()
		case strings.HasPrefix(line, "Package: "):
			name = strings.TrimSpace(line[len("Package: "):])
		case strings.HasPrefix(line, "Version: "):
			version = strings.TrimSpace(line[len("Version: "):])
		}
	}
	flush()
	return scanner.Err()
}

// aptReleaseOrigin reads the Origin field of a Release or InRelease file; "" when it has none.
func aptReleaseOrigin(r io.Reader) string {
	// An InRelease file starts with the header of its signature and an empty line; the fields come before the file lists, whose
	// lines start with a space.
	scanner := bufio.NewScanner(r)
	for scanner.Scan() {
		line := scanner.Text()
		if value, ok := strings.CutPrefix(line, "Origin: "); ok {
			return strings.TrimSpace(value)
		}
		if strings.HasPrefix(line, "MD5Sum:") || strings.HasPrefix(line, "SHA256:") {
			break
		}
	}
	return ""
}

// aptReleaseFileFor names the Release files of the list a Packages file belongs to: for
// "archive.ubuntu.com_ubuntu_dists_jammy-updates_main_binary-amd64_Packages" they are
// "archive.ubuntu.com_ubuntu_dists_jammy-updates_InRelease" and "..._Release". Nil for a name that does not follow the pattern.
func aptReleaseFileFor(packagesFile string) []string {
	head, rest, ok := strings.Cut(packagesFile, "_dists_")
	if !ok {
		return nil
	}
	suite, _, ok := strings.Cut(rest, "_")
	if !ok {
		return nil
	}
	prefix := head + "_dists_" + suite
	return []string{prefix + "_InRelease", prefix + "_Release"}
}

// aptOrigin decides where an installed package came from (MD-Files/VULNERABILITIES.md §6). The apt lists hold per pocket only the
// newest version, so the installed version, when it is outdated, is often in no list: the decision is by name. A name only the
// distribution offers is the distribution's. A name another origin offers as well belongs to the origin whose version matches
// the installed one, or else to the other origin when the installed version is newer than every version of the distribution.
// A name in no list is local.
func aptOrigin(idx aptIndex, name, installed string) string {
	candidates := idx[name]
	if len(candidates) == 0 {
		return localOrigin
	}
	for _, c := range candidates {
		if c.Version == installed && distributionOrigins[c.Origin] {
			return c.Origin
		}
	}
	for _, c := range candidates {
		if c.Version == installed {
			return c.Origin
		}
	}
	var distribution, other string
	newestDistribution := ""
	for _, c := range candidates {
		if distributionOrigins[c.Origin] {
			if distribution == "" || compareDpkg(c.Version, newestDistribution) > 0 {
				distribution, newestDistribution = c.Origin, c.Version
			}
		} else if other == "" {
			other = c.Origin
		}
	}
	switch {
	case other == "":
		return distribution
	case distribution == "":
		return other
	case compareDpkg(installed, newestDistribution) > 0:
		return other
	default:
		return distribution
	}
}

// splitSourceRPM reads "openssh-8.7p1-38.el9_4.1.src.rpm" into the name "openssh" and the version-release "8.7p1-38.el9_4.1".
func splitSourceRPM(value string) (name, versionRelease string) {
	value = strings.TrimSpace(value)
	if value == "" || value == "(none)" {
		return "", ""
	}
	for _, suffix := range []string{".src.rpm", ".nosrc.rpm"} {
		value = strings.TrimSuffix(value, suffix)
	}
	release := strings.LastIndexByte(value, '-')
	if release <= 0 {
		return "", ""
	}
	version := strings.LastIndexByte(value[:release], '-')
	if version <= 0 {
		return "", ""
	}
	return value[:version], value[version+1:]
}

// dpkgEpoch is the epoch of a Debian version ("1:8.9p1-3ubuntu0.10" is 1), 0 without one.
func dpkgEpoch(version string) uint32 {
	epoch, _, ok := strings.Cut(version, ":")
	if !ok {
		return 0
	}
	n, err := strconv.ParseUint(epoch, 10, 32)
	if err != nil {
		return 0
	}
	return uint32(n)
}

// compareDpkg compares two Debian versions as dpkg does (epoch, upstream version, revision, with the ~ rule). The server has its
// own port of the same algorithm (PackageVersions.CompareDpkg); the agent needs it only to decide an origin.
func compareDpkg(a, b string) int {
	epochA, upstreamA, revisionA := splitDpkg(a)
	epochB, upstreamB, revisionB := splitDpkg(b)
	if epochA != epochB {
		if epochA < epochB {
			return -1
		}
		return 1
	}
	if c := verrevcmp(upstreamA, upstreamB); c != 0 {
		return c
	}
	return verrevcmp(revisionA, revisionB)
}

func splitDpkg(version string) (uint64, string, string) {
	version = strings.TrimSpace(version)
	var epoch uint64
	if head, rest, ok := strings.Cut(version, ":"); ok {
		if n, err := strconv.ParseUint(head, 10, 64); err == nil {
			epoch, version = n, rest
		}
	}
	if i := strings.LastIndexByte(version, '-'); i >= 0 {
		return epoch, version[:i], version[i+1:]
	}
	return epoch, version, ""
}

func dpkgOrder(c byte) int {
	switch {
	case c >= '0' && c <= '9':
		return 0
	case (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z'):
		return int(c)
	case c == '~':
		return -1
	case c == 0:
		return 0
	default:
		return int(c) + 256
	}
}

func isDigit(c byte) bool { return c >= '0' && c <= '9' }

// verrevcmp is dpkg's comparison of an upstream version or a revision (lib/dpkg/version.c).
func verrevcmp(a, b string) int {
	at := func(s string, i int) byte {
		if i < len(s) {
			return s[i]
		}
		return 0
	}
	i, j := 0, 0
	for i < len(a) || j < len(b) {
		firstDiff := 0
		for (i < len(a) && !isDigit(a[i])) || (j < len(b) && !isDigit(b[j])) {
			ac, bc := dpkgOrder(at(a, i)), dpkgOrder(at(b, j))
			if ac != bc {
				return sign(ac - bc)
			}
			i++
			j++
		}
		for i < len(a) && a[i] == '0' {
			i++
		}
		for j < len(b) && b[j] == '0' {
			j++
		}
		for i < len(a) && j < len(b) && isDigit(a[i]) && isDigit(b[j]) {
			if firstDiff == 0 {
				firstDiff = int(a[i]) - int(b[j])
			}
			i++
			j++
		}
		if i < len(a) && isDigit(a[i]) {
			return 1
		}
		if j < len(b) && isDigit(b[j]) {
			return -1
		}
		if firstDiff != 0 {
			return sign(firstDiff)
		}
	}
	return 0
}

func sign(n int) int {
	switch {
	case n < 0:
		return -1
	case n > 0:
		return 1
	default:
		return 0
	}
}

// proStatus is the part of /var/lib/ubuntu-advantage/status.json Fleeto reads.
type proStatus struct {
	Services []struct {
		Name   string `json:"name"`
		Status string `json:"status"`
	} `json:"services"`
}

// parseProStatus lists the enabled Ubuntu Pro services, sorted; nil when the file cannot be read.
func parseProStatus(data []byte) []string {
	var status proStatus
	if err := json.Unmarshal(data, &status); err != nil {
		return nil
	}
	var enabled []string
	for _, service := range status.Services {
		if service.Status == "enabled" && service.Name != "" {
			enabled = append(enabled, service.Name)
		}
	}
	slices.Sort(enabled)
	return slices.Compact(enabled)
}

// livepatchStatus is the part of `canonical-livepatch status --format json` Fleeto reads.
type livepatchStatus struct {
	Status []struct {
		Running   bool `json:"Running"`
		Livepatch struct {
			State string          `json:"State"`
			Fixes json.RawMessage `json:"Fixes"`
		} `json:"Livepatch"`
	} `json:"Status"`
}

var cvePattern = regexp.MustCompile(`(?i)\bCVE-\d{4}-\d{4,7}\b`)

// parseLivepatch reports whether Livepatch applied patches to the running kernel, and the CVEs of the fixes it marks as patched.
// The fixes are read loosely: the CVE identifiers in each fix that is not marked as unpatched, uppercase and sorted.
func parseLivepatch(data []byte) (bool, []string) {
	var status livepatchStatus
	if err := json.Unmarshal(data, &status); err != nil {
		return false, nil
	}
	for _, kernel := range status.Status {
		if !kernel.Running || !strings.EqualFold(kernel.Livepatch.State, "applied") {
			continue
		}
		var fixes []map[string]any
		_ = json.Unmarshal(kernel.Livepatch.Fixes, &fixes)
		var cves []string
		for _, fix := range fixes {
			if patched, ok := fix["Patched"].(bool); ok && !patched {
				continue
			}
			text, _ := json.Marshal(fix)
			for _, cve := range cvePattern.FindAllString(string(text), -1) {
				cves = append(cves, strings.ToUpper(cve))
			}
		}
		slices.Sort(cves)
		return true, slices.Compact(cves)
	}
	return false, nil
}

// maxSoftware bounds the packages in one inventory; a Linux endpoint with a desktop has a few thousand.
const maxSoftware = 5000

// parseDpkg reads the dpkg-query output: status, package, version, maintainer and, from 0.8.0, the source package with its
// version and the architecture per line. Only installed packages ("ii") count.
func parseDpkg(out string) []*agentv1.SoftwareItem {
	var items []*agentv1.SoftwareItem
	for line := range strings.SplitSeq(out, "\n") {
		fields := strings.Split(strings.TrimRight(line, "\r"), "\t")
		if len(fields) < 4 || !strings.HasPrefix(strings.TrimSpace(fields[0]), "ii") || fields[1] == "" {
			continue
		}
		if len(items) >= maxSoftware {
			break
		}
		item := &agentv1.SoftwareItem{Name: fields[1], Version: fields[2], Publisher: publisher(fields[3]), Epoch: dpkgEpoch(fields[2])}
		if len(fields) >= 7 {
			// ${source:Package} and ${source:Version} equal the package and its version when it is built from a source of the same name.
			item.SourceName, item.SourceVersion, item.Architecture = fields[4], fields[5], fields[6]
		}
		items = append(items, item)
	}
	return items
}

func parseRpm(out string) []*agentv1.SoftwareItem {
	var items []*agentv1.SoftwareItem
	for line := range strings.SplitSeq(out, "\n") {
		fields := strings.Split(strings.TrimRight(line, "\r"), "\t")
		// rpm prints (none) for a header without a name.
		if len(fields) < 4 || fields[0] == "" || fields[0] == "(none)" {
			continue
		}
		if len(items) >= maxSoftware {
			break
		}
		item := &agentv1.SoftwareItem{Name: fields[0], Version: fields[1], Publisher: publisher(fields[2])}
		if seconds, err := strconv.ParseInt(strings.TrimSpace(fields[3]), 10, 64); err == nil && seconds > 0 {
			// The same shape as the Windows registry: YYYYMMDD.
			item.InstallDate = time.Unix(seconds, 0).UTC().Format("20060102")
		}
		if len(fields) >= 8 {
			// rpm prints (none) for a tag the header does not have.
			if epoch, err := strconv.ParseUint(strings.TrimSpace(fields[4]), 10, 32); err == nil {
				item.Epoch = uint32(epoch)
			}
			item.Architecture = none(fields[5])
			item.SourceName, item.SourceVersion = splitSourceRPM(fields[6])
			item.ModuleStream = none(fields[7])
			// The vendor is set when a package is built: "AlmaLinux" for the distribution's own packages.
			item.Origin = none(fields[2])
		}
		items = append(items, item)
	}
	return items
}

// none is a value rpm printed, or "" for (none).
func none(value string) string {
	value = strings.TrimSpace(value)
	if value == "(none)" {
		return ""
	}
	return value
}

// publisher is the maintainer or vendor without its email address, which is personal data the inventory does not need.
func publisher(value string) string {
	name, _, _ := strings.Cut(value, "<")
	name = strings.TrimSpace(name)
	if name == "(none)" {
		return ""
	}
	return name
}

// services lists the systemd services with their start type and state, so they can be picked in the check dialog.

// parsePveVersion reads the version of pve-manager from what pveversion prints: pve-manager/8.4.5/<hash> (running kernel: ...).
func parsePveVersion(out string) string {
	_, rest, ok := strings.Cut(strings.TrimSpace(out), "pve-manager/")
	if !ok {
		return ""
	}
	version, _, _ := strings.Cut(rest, "/")
	return strings.TrimSpace(version)
}

// parseDnfModule reads one file of /etc/dnf/modules.d, an ini file with a section per module, and returns "name:stream" for every
// module whose state is enabled. The state of a module dnf enables by default (the [d][e] of "dnf module list") is written there
// as well once one of its packages is installed.
func parseDnfModule(r io.Reader) []string {
	var streams []string
	var name, stream, state string
	flush := func() {
		if name != "" && stream != "" && state == "enabled" {
			streams = append(streams, name+":"+stream)
		}
		name, stream, state = "", "", ""
	}
	scanner := bufio.NewScanner(r)
	for scanner.Scan() {
		line := strings.TrimSpace(scanner.Text())
		switch {
		case strings.HasPrefix(line, "["):
			flush()
			name = strings.TrimSpace(strings.Trim(line, "[]"))
		case strings.HasPrefix(line, "name="):
			name = strings.TrimSpace(strings.TrimPrefix(line, "name="))
		case strings.HasPrefix(line, "stream="):
			stream = strings.TrimSpace(strings.TrimPrefix(line, "stream="))
		case strings.HasPrefix(line, "state="):
			state = strings.TrimSpace(strings.TrimPrefix(line, "state="))
		}
	}
	flush()
	return streams
}
