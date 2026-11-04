package patch

import (
	"context"
	"regexp"
	"slices"
	"strconv"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Engine names in PatchScanReport.engine.
const (
	EngineApt = "apt"
	EngineDnf = "dnf"
)

const (
	// dpkgUpdatesDir holds the journal of a dpkg run; a file with a numeric name means dpkg was interrupted (apt checks the same).
	dpkgUpdatesDir = "/var/lib/dpkg/updates"
	// rebootRequiredFile is written by Ubuntu and Debian packages that need a restart.
	rebootRequiredFile = "/var/run/reboot-required"

	problemDpkgInterrupted = "dpkg was interrupted during an earlier installation; run 'dpkg --configure -a' on the endpoint."
)

// aptEnv makes apt answer in English and never ask anything.
var aptEnv = []string{"LC_ALL=C", "LANG=C", "DEBIAN_FRONTEND=noninteractive"}

// dpkgNamePattern is the form of a package name (with an optional architecture) the agent passes to dpkg-query.
var dpkgNamePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9+.\-]*(:[a-z0-9\-]+)?$`)

// distributionLabels are the apt release labels whose archive alone names the origin well.
var distributionLabels = []string{"Ubuntu", "Debian", "Debian-Security", "UbuntuESM", "UbuntuESMApps"}

// securityLabels are apt release labels whose updates are all security updates.
var securityLabels = []string{"Debian-Security", "UbuntuESM", "UbuntuESMApps"}

// scanApt runs an apt scan: refresh the package lists, simulate a dist-upgrade and read what it would change.
func scanApt(ctx context.Context, sys system) *agentv1.PatchScanReport {
	report, _ := scanAptChecked(ctx, sys)
	return report
}

// scanAptChecked is scanApt that also says whether the scan failed outright (stopped, or the simulation did not run), so an
// installation never takes an empty list for "nothing missing".
func scanAptChecked(ctx context.Context, sys system) (*agentv1.PatchScanReport, bool) {
	report := &agentv1.PatchScanReport{Engine: EngineApt}
	var probs problems

	if dpkgInterrupted(sys.ReadDirNames(dpkgUpdatesDir)) {
		probs.add(problemDpkgInterrupted)
	}

	// Refresh the lists as apt-daily does; a running unattended-upgrades or apt-daily is waited for up to 5 minutes.
	update := sys.Run(ctx, aptEnv, "apt-get", "-q", "-o", "DPkg::Lock::Timeout=300", "update")
	if ctx.Err() != nil {
		report.Problem = probs.String()
		return report, true
	}
	for _, p := range parseAptUpdateProblems(update.Stdout + "\n" + update.Stderr) {
		probs.add(p)
	}
	if update.Err != nil {
		probs.add("The package lists could not be refreshed: " + update.Err.Error() + ".")
	}

	// The simulation takes no lock and changes nothing; dist-upgrade is what Proxmox VE requires and what unattended-upgrades computes.
	sim := sys.Run(ctx, []string{"LC_ALL=C", "LANG=C"}, "apt-get", "-s", "-q", "-o", "Debug::NoLocking=1", "dist-upgrade")
	if ctx.Err() != nil {
		report.Problem = probs.String()
		return report, true
	}
	parsed := parseAptSimulation(sim.Stdout)
	failed := sim.Err != nil || sim.Code != 0
	if failed {
		probs.add(aptSimulationProblem(sim))
	}

	holds := map[string]bool{}
	if hold := sys.Run(ctx, aptEnv, "apt-mark", "showhold"); hold.Err == nil && hold.Code == 0 {
		for _, name := range parseAptMarkShowhold(hold.Stdout) {
			holds[name] = true
		}
	}

	// Removals and kept-back packages carry no architecture (or no version) in the simulation: dpkg-query tells them, in one call.
	var lookup []string
	for _, u := range parsed.Updates {
		if u.needsLookup && dpkgNamePattern.MatchString(u.query) {
			lookup = append(lookup, u.query)
		}
	}
	installed := map[string]dpkgInstalled{}
	if len(lookup) > 0 {
		args := append([]string{"-W", "-f=${binary:Package}\\t${Package}\\t${Architecture}\\t${Version}\\n", "--"}, lookup...)
		if res := sys.Run(ctx, aptEnv, "dpkg-query", args...); res.Err == nil {
			installed = parseDpkgQueryInstalled(res.Stdout)
		}
	}
	for _, u := range parsed.Updates {
		report.Updates = append(report.Updates, u.toProto(holds, installed))
	}
	report.RestartRequired = sys.Exists(rebootRequiredFile)
	report.Problem = probs.String()
	return report, failed || ctx.Err() != nil
}

// aptUpdate is one package of the simulation.
type aptUpdate struct {
	Name, Arch, Installed, Candidate string
	Origins                          []string
	Removal, Held                    bool
	// needsLookup: the architecture or the installed version is not in the simulation; query names the package for dpkg-query.
	needsLookup bool
	query       string
}

// aptSimulation is what parseAptSimulation read.
type aptSimulation struct {
	Updates []*aptUpdate
}

// parseAptSimulation reads the output of apt-get -s dist-upgrade:
//
//	Inst libssl3 [3.0.2-0ubuntu1.10] (3.0.2-0ubuntu1.12 Ubuntu:22.04/jammy-security [amd64])   an upgrade
//	Inst newpkg (1.2-3 Debian:12.5/stable [amd64])                                             a new dependency
//	Remv oldpkg [1.0]                                                                          a removal
//
// Conf lines repeat the Inst lines and are ignored. Packages listed under "have been kept back" (apt-mark holds, phased updates on
// Ubuntu 22.04, upgrades that would need a removal apt does not choose) or "deferred due to phasing" (apt 2.7 and later) have no Inst
// line; they are reported as held, without a candidate version. Packages listed under "The following held packages will be changed" are marked held.
func parseAptSimulation(out string) aptSimulation {
	var sim aptSimulation
	seen := map[string]*aptUpdate{}
	var keptBack, heldChanged []string
	section := ""
	for line := range strings.SplitSeq(out, "\n") {
		line = strings.TrimRight(line, "\r")
		if strings.HasPrefix(line, "  ") && section != "" {
			names := strings.Fields(line)
			switch section {
			case "kept":
				keptBack = append(keptBack, names...)
			case "held":
				heldChanged = append(heldChanged, names...)
			}
			continue
		}
		section = ""
		switch {
		case strings.HasPrefix(line, "The following packages have been kept back"),
			strings.HasPrefix(line, "The following upgrades have been deferred due to phasing"):
			section = "kept"
		case strings.HasPrefix(line, "The following held packages will be changed"):
			section = "held"
		case strings.HasPrefix(line, "The following "):
			section = "other"
		case strings.HasPrefix(line, "Inst "):
			if u := parseAptInst(line[len("Inst "):]); u != nil {
				if _, dup := seen[u.Name+":"+u.Arch]; !dup {
					seen[u.Name+":"+u.Arch] = u
					sim.Updates = append(sim.Updates, u)
				}
			}
		case strings.HasPrefix(line, "Remv "):
			if u := parseAptRemv(line[len("Remv "):]); u != nil {
				key := u.Name + ":" + u.Arch
				if _, dup := seen[key]; !dup {
					seen[key] = u
					sim.Updates = append(sim.Updates, u)
				}
			}
		}
	}
	for _, name := range heldChanged {
		n, arch := splitAptName(name)
		for _, u := range sim.Updates {
			if u.Name == n && (arch == "" || u.Arch == arch) {
				u.Held = true
			}
		}
	}
	for _, name := range keptBack {
		n, arch := splitAptName(name)
		if n == "" {
			continue
		}
		listed := slices.ContainsFunc(sim.Updates, func(u *aptUpdate) bool { return u.Name == n && (arch == "" || u.Arch == arch) })
		if listed {
			continue
		}
		u := &aptUpdate{Name: n, Arch: arch, Held: true, needsLookup: true, query: name}
		sim.Updates = append(sim.Updates, u)
	}
	return sim
}

// parseAptInst reads the part of an Inst line after "Inst ".
func parseAptInst(rest string) *aptUpdate {
	nameField, rest, _ := strings.Cut(strings.TrimSpace(rest), " ")
	name, arch := splitAptName(nameField)
	if name == "" {
		return nil
	}
	u := &aptUpdate{Name: name, Arch: arch}
	rest = strings.TrimSpace(rest)
	if strings.HasPrefix(rest, "[") {
		end := strings.IndexByte(rest, ']')
		if end < 0 {
			return nil
		}
		u.Installed = rest[1:end]
		rest = strings.TrimSpace(rest[end+1:])
	}
	if !strings.HasPrefix(rest, "(") {
		return nil
	}
	end := strings.IndexByte(rest, ')')
	if end < 0 {
		return nil
	}
	inner := strings.TrimSpace(rest[1:end])
	// "<candidate> <origin>, <origin> [<arch>]"; an origin label can hold spaces ("Proxmox Debian Repository:stable").
	if open := strings.LastIndex(inner, " ["); open >= 0 && strings.HasSuffix(inner, "]") {
		if a := inner[open+2 : len(inner)-1]; a != "" && u.Arch == "" {
			u.Arch = a
		}
		inner = inner[:open]
	}
	candidate, origins, _ := strings.Cut(inner, " ")
	u.Candidate = candidate
	for o := range strings.SplitSeq(origins, ",") {
		if o = strings.TrimSpace(o); o != "" {
			u.Origins = append(u.Origins, o)
		}
	}
	if u.Candidate == "" {
		return nil
	}
	if u.Arch == "" {
		u.needsLookup, u.query = true, name
	}
	return u
}

// parseAptRemv reads the part of a Remv line after "Remv ": the package and its installed version.
func parseAptRemv(rest string) *aptUpdate {
	nameField, rest, _ := strings.Cut(strings.TrimSpace(rest), " ")
	name, arch := splitAptName(nameField)
	if name == "" {
		return nil
	}
	u := &aptUpdate{Name: name, Arch: arch, Removal: true}
	rest = strings.TrimSpace(rest)
	if strings.HasPrefix(rest, "[") {
		if end := strings.IndexByte(rest, ']'); end > 0 {
			u.Installed = rest[1:end]
		}
	}
	if u.Arch == "" {
		// The simulation names a foreign architecture only; dpkg-query tells the real one ("all" included).
		u.needsLookup, u.query = true, name
	}
	return u
}

// splitAptName splits "libc6:i386" into name and architecture; a name without one returns an empty architecture.
func splitAptName(field string) (string, string) {
	name, arch, _ := strings.Cut(field, ":")
	if !dpkgNamePattern.MatchString(field) {
		return "", ""
	}
	return name, arch
}

// aptOrigin is one origin of a candidate as apt prints it: "Label:Version/Archive", "Label:Archive" or "Version/Archive".
type aptOrigin struct {
	Label, Archive string
}

func parseAptOrigin(s string) aptOrigin {
	var o aptOrigin
	label, rest, found := strings.Cut(s, ":")
	if found {
		o.Label = label
	} else {
		rest = s
	}
	if i := strings.LastIndexByte(rest, '/'); i >= 0 {
		rest = rest[i+1:]
	}
	o.Archive = rest
	return o
}

func (o aptOrigin) security() bool {
	return strings.HasSuffix(o.Archive, "-security") || slices.Contains(securityLabels, o.Label)
}

// name is what PatchUpdate.origin carries: the archive ("jammy-security", "oldstable-security"), and for a repository that is not
// the distribution's the label too ("Proxmox Debian Repository:stable"), since its archive alone says little.
func (o aptOrigin) name() string {
	if o.Label == "" || slices.Contains(distributionLabels, o.Label) {
		return o.Archive
	}
	return o.Label + ":" + o.Archive
}

// dpkgInstalled is a package as dpkg-query reports it.
type dpkgInstalled struct {
	Name, Arch, Version string
}

// parseDpkgQueryInstalled reads "binary:Package \t Package \t Architecture \t Version" lines, keyed by binary:Package (which
// carries ":arch" for a foreign architecture, as apt names it) and by Package.
func parseDpkgQueryInstalled(out string) map[string]dpkgInstalled {
	result := map[string]dpkgInstalled{}
	for line := range strings.SplitSeq(out, "\n") {
		fields := strings.Split(strings.TrimRight(line, "\r"), "\t")
		if len(fields) != 4 || fields[1] == "" || fields[3] == "" {
			continue
		}
		p := dpkgInstalled{Name: fields[1], Arch: fields[2], Version: fields[3]}
		result[fields[0]] = p
		if _, ok := result[fields[1]]; !ok {
			result[fields[1]] = p
		}
	}
	return result
}

// toProto turns a package of the simulation into a PatchUpdate.
func (u *aptUpdate) toProto(holds map[string]bool, installed map[string]dpkgInstalled) *agentv1.PatchUpdate {
	arch, installedVersion := u.Arch, u.Installed
	if u.needsLookup {
		if p, ok := installed[u.query]; ok {
			if arch == "" {
				arch = p.Arch
			}
			// A new dependency has nothing installed; a kept-back package or a removal has.
			if installedVersion == "" && u.Candidate == "" {
				installedVersion = p.Version
			}
		}
	}
	if arch == "" {
		arch = debianArch()
	}
	p := &agentv1.PatchUpdate{
		Id:               u.Name + ":" + arch,
		Name:             u.Name,
		Version:          u.Candidate,
		InstalledVersion: installedVersion,
		Classification:   classificationOther,
		Held:             u.Held || holds[u.Name] || holds[u.Name+":"+arch],
		Removal:          u.Removal,
	}
	if u.Removal {
		p.Version = ""
	}
	var chosen *aptOrigin
	for _, s := range u.Origins {
		o := parseAptOrigin(s)
		if o.security() {
			p.Classification = classificationSecurity
			chosen = &o
			break
		}
		if chosen == nil {
			chosen = &o
		}
	}
	if chosen != nil {
		p.Origin = chosen.name()
	}
	return p
}

// parseAptMarkShowhold reads apt-mark showhold: one package per line, with ":arch" for a foreign architecture.
func parseAptMarkShowhold(out string) []string {
	var names []string
	for line := range strings.SplitSeq(out, "\n") {
		if name := strings.TrimSpace(line); dpkgNamePattern.MatchString(name) {
			names = append(names, name)
		}
	}
	return names
}

// dpkgInterrupted reports whether the dpkg journal holds an unfinished run, as apt checks before it installs anything.
func dpkgInterrupted(names []string) bool {
	for _, name := range names {
		if name != "" && strings.Trim(name, "0123456789") == "" {
			return true
		}
	}
	return false
}

var (
	aptFetchPattern = regexp.MustCompile(`^[EW]: Failed to fetch (\S+)\s+(.*)$`)
	aptRepoPattern  = regexp.MustCompile(`^[EW]: The repository '(\S+)[^']*' (.*)$`)
	aptLockPattern  = regexp.MustCompile(`^E: Could not get lock (\S+?)\.? It is held by process \d+ \(([^)]*)\)`)
	aptIPSuffix     = regexp.MustCompile(`\s*\[IP: [^\]]*\]\s*$`)
)

// repositoryName shortens a repository URL to host and path, without the dists part: "enterprise.proxmox.com/debian/pve".
func repositoryName(url string) string {
	_, rest, found := strings.Cut(url, "://")
	if !found {
		rest = url
	}
	if i := strings.Index(rest, "/dists/"); i >= 0 {
		rest = rest[:i]
	}
	return strings.TrimRight(rest, "/")
}

// parseAptUpdateProblems reads the output of apt-get update and returns what went wrong, in words for the Patches tab: every
// repository that could not be read, a package lock that stayed held, an interrupted dpkg, and otherwise the first error.
func parseAptUpdateProblems(out string) []string {
	type repoProblem struct{ name, reason string }
	var repos []repoProblem
	addRepo := func(name, reason string) {
		for i := range repos {
			if repos[i].name == name {
				if repos[i].reason == "" {
					repos[i].reason = reason
				}
				return
			}
		}
		repos = append(repos, repoProblem{name, reason})
	}
	var other []string
	lock, dpkg := "", false
	for line := range strings.SplitSeq(out, "\n") {
		line = strings.TrimSpace(strings.TrimRight(line, "\r"))
		switch {
		case strings.Contains(line, "dpkg was interrupted"):
			dpkg = true
		case aptFetchPattern.MatchString(line):
			m := aptFetchPattern.FindStringSubmatch(line)
			addRepo(repositoryName(m[1]), strings.Join(strings.Fields(aptIPSuffix.ReplaceAllString(m[2], "")), " "))
		case aptRepoPattern.MatchString(line):
			m := aptRepoPattern.FindStringSubmatch(line)
			addRepo(repositoryName(m[1]), "")
		case aptLockPattern.MatchString(line):
			lock = aptLockPattern.FindStringSubmatch(line)[2]
		case strings.HasPrefix(line, "E: Some index files failed to download"),
			strings.HasPrefix(line, "W: Some index files failed to download"):
			// The summary of the failures above.
		case strings.HasPrefix(line, "E: "):
			other = append(other, strings.TrimPrefix(line, "E: "))
		}
	}
	var result []string
	if dpkg {
		result = append(result, problemDpkgInterrupted)
	}
	if lock != "" {
		result = append(result, "The package lock was held by another process ("+lock+") for more than 5 minutes; the scan used the package lists as they were.")
	}
	enterprise := false
	for _, r := range repos {
		message := "The repository " + r.name + " could not be read"
		if r.reason != "" {
			message += " (" + r.reason + ")"
		}
		result = append(result, message+"; the scan used the other package lists.")
		if strings.HasPrefix(r.name, "enterprise.proxmox.com/") && strings.Contains(r.reason, "401") {
			enterprise = true
		}
	}
	if enterprise {
		result = append(result, "The Proxmox enterprise repository needs a subscription: add the subscription key, or switch the host to the no-subscription repository.")
	}
	if len(result) == 0 && len(other) > 0 {
		result = append(result, "apt-get update failed: "+other[0])
	}
	return result
}

// aptSimulationProblem describes a simulation that failed.
func aptSimulationProblem(sim cmdResult) string {
	text := sim.Stderr + "\n" + sim.Stdout
	if strings.Contains(text, "dpkg was interrupted") {
		return problemDpkgInterrupted
	}
	for line := range strings.SplitSeq(text, "\n") {
		if line = strings.TrimSpace(line); strings.HasPrefix(line, "E: ") {
			return "The upgrade could not be simulated: " + strings.TrimPrefix(line, "E: ")
		}
	}
	if sim.Err != nil {
		return "The upgrade could not be simulated: " + sim.Err.Error() + "."
	}
	return "The upgrade could not be simulated: apt-get ended with exit code " + strconv.Itoa(sim.Code) + "."
}
