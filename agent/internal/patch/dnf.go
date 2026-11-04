package patch

import (
	"context"
	"regexp"
	"slices"
	"strconv"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// dnfEnv makes dnf and rpm answer in English.
var dnfEnv = []string{"LC_ALL=C", "LANG=C"}

var (
	// rpmNamePattern is the form of a "name.arch" the agent passes to rpm.
	rpmNamePattern = regexp.MustCompile(`^[A-Za-z0-9_+][A-Za-z0-9_.+\-]*$`)
	rpmArchPattern = regexp.MustCompile(`^[a-z0-9_]+$`)
	dnfRepoPattern = regexp.MustCompile(`repo(?:sitory)?:?\s+['"]?([A-Za-z0-9_.:\-]+)['"]?`)
)

// severityRank orders the severities of an advisory; a higher one wins when a package has several.
var severityRank = map[string]int{"low": 1, "moderate": 2, "important": 3, "critical": 4}

// scanDnf runs a dnf scan: the missing updates with their repository, the installed versions, the advisories and the version locks.
func scanDnf(ctx context.Context, sys system) *agentv1.PatchScanReport {
	report := &agentv1.PatchScanReport{Engine: EngineDnf}
	var probs problems

	// --refresh fetches the metadata as dnf-automatic does; skip_if_unavailable lets one repository that cannot be read leave the
	// others usable, and is reported below.
	check := sys.Run(ctx, dnfEnv, "dnf", "-q", "--refresh", "--setopt=skip_if_unavailable=True", "check-update")
	if ctx.Err() != nil {
		return report
	}
	for _, repo := range parseDnfRepoProblems(check.Stderr) {
		probs.add("The repository " + repo + " could not be read; the scan used the other repositories.")
	}
	var updates []*dnfUpdate
	switch {
	case check.Err != nil:
		probs.add("dnf check-update could not run: " + check.Err.Error() + ".")
	case check.Code == 0:
	case check.Code == 100:
		updates = parseDnfCheckUpdate(check.Stdout)
	default:
		probs.add("dnf check-update failed: " + firstDnfError(check.Stderr, check.Stdout, check.Code))
	}

	if len(updates) > 0 {
		// The installed versions, in one rpm call.
		args := []string{"-q", "--qf", "%{NAME}.%{ARCH} %|EPOCH?{%{EPOCH}:}:{}|%{VERSION}-%{RELEASE}\\n", "--"}
		for _, u := range updates {
			if rpmNamePattern.MatchString(u.Name + "." + u.Arch) {
				args = append(args, u.Name+"."+u.Arch)
			}
		}
		if res := sys.Run(ctx, dnfEnv, "rpm", args...); res.Err == nil {
			installed := parseRpmInstalled(res.Stdout)
			for _, u := range updates {
				u.Installed = installed[u.Name+"."+u.Arch]
			}
		}

		// The advisories (dnf 4: updateinfo; dnf 5: advisory, for which updateinfo is an alias).
		info := sys.Run(ctx, dnfEnv, "dnf", "-q", "updateinfo", "list", "--updates")
		if ctx.Err() != nil {
			report.Updates = dnfToProto(updates, nil, nil)
			report.Problem = probs.String()
			return report
		}
		var advisories map[string]dnfAdvisory
		if info.Err == nil && info.Code == 0 {
			advisories = parseDnfUpdateinfo(info.Stdout)
		} else {
			probs.add("The security advisories could not be read, so no update is marked as a security update: " +
				firstDnfError(info.Stderr, info.Stdout, info.Code))
		}

		// Version locks, where the plugin (dnf 4) or the command (dnf 5) exists; best effort.
		var locked map[string]bool
		if lock := sys.Run(ctx, dnfEnv, "dnf", "-q", "versionlock", "list"); lock.Err == nil && lock.Code == 0 {
			locked = parseDnfVersionlock(lock.Stdout)
		}
		report.Updates = dnfToProto(updates, advisories, locked)
	}
	report.RestartRequired = needsRestarting(ctx, sys)
	report.Problem = probs.String()
	return report
}

// dnfUpdate is one package of dnf check-update.
type dnfUpdate struct {
	Name, Arch, Candidate, Repo, Installed string
}

// splitNameArch splits "openssl.x86_64" at its last dot.
func splitNameArch(field string) (string, string, bool) {
	i := strings.LastIndexByte(field, '.')
	if i <= 0 || i == len(field)-1 {
		return "", "", false
	}
	name, arch := field[:i], field[i+1:]
	if !rpmArchPattern.MatchString(arch) || !rpmNamePattern.MatchString(name) {
		return "", "", false
	}
	return name, arch, true
}

// parseDnfCheckUpdate reads dnf check-update: "name.arch  [epoch:]version-release  repository" per package. A name too long for its
// column puts the rest of the line on the next, indented line. The "Obsoleting Packages" section at the end is not read: its
// packages replace installed ones and are listed with their installed counterpart, not as updates of their own.
func parseDnfCheckUpdate(out string) []*dnfUpdate {
	var result []*dnfUpdate
	seen := map[string]bool{}
	var fields []string
	flush := func() {
		if len(fields) == 3 {
			name, arch, ok := splitNameArch(fields[0])
			if ok && strings.ContainsAny(fields[1], "0123456789") && !seen[name+"."+arch] {
				seen[name+"."+arch] = true
				result = append(result, &dnfUpdate{Name: name, Arch: arch, Candidate: fields[1], Repo: fields[2]})
			}
		}
		fields = nil
	}
	for line := range strings.SplitSeq(out, "\n") {
		line = strings.TrimRight(line, "\r")
		if strings.TrimSpace(line) == "" {
			flush()
			continue
		}
		if strings.HasPrefix(strings.TrimSpace(line), "Obsoleting Packages") {
			flush()
			break
		}
		continuation := line[0] == ' ' || line[0] == '\t'
		if !continuation {
			flush()
			fields = strings.Fields(line)
			continue
		}
		if len(fields) > 0 && len(fields) < 3 {
			fields = append(fields, strings.Fields(line)...)
		}
	}
	flush()
	return result
}

// parseRpmInstalled reads "name.arch [epoch:]version-release" lines. A package installed in several versions (a kernel) keeps the
// last one rpm lists, which is the most recently installed.
func parseRpmInstalled(out string) map[string]string {
	result := map[string]string{}
	for line := range strings.SplitSeq(out, "\n") {
		fields := strings.Fields(line)
		if len(fields) == 2 && !strings.Contains(line, "is not installed") {
			result[fields[0]] = fields[1]
		}
	}
	return result
}

// dnfAdvisory is the most important advisory of one package.
type dnfAdvisory struct {
	ID, Severity string
	Security     bool
}

// parseNEVRA splits "openssl-1:3.0.7-25.el9_3.x86_64" into name and arch.
func parseNEVRA(s string) (string, string, bool) {
	dot := strings.LastIndexByte(s, '.')
	if dot <= 0 {
		return "", "", false
	}
	nevr, arch := s[:dot], s[dot+1:]
	if !rpmArchPattern.MatchString(arch) {
		return "", "", false
	}
	// name-[epoch:]version-release: the name ends at the second dash from the end.
	i := strings.LastIndexByte(nevr, '-')
	if i <= 0 {
		return "", "", false
	}
	j := strings.LastIndexByte(nevr[:i], '-')
	if j <= 0 {
		return "", "", false
	}
	return nevr[:j], arch, true
}

// parseDnfUpdateinfo reads the advisories per package, keyed by "name.arch", in either form:
//
//	ALSA-2024:1234 Important/Sec. openssl-1:3.0.7-25.el9_3.x86_64                      dnf 4: updateinfo list
//	ALSA-2025:1234 security    Important openssl-1:3.2.2-6.el10_0.x86_64 2025-05-20 10:00:00   dnf 5: advisory list (Name Type Severity Package Issued)
//
// A security advisory wins over another type, and of two security advisories the higher severity.
func parseDnfUpdateinfo(out string) map[string]dnfAdvisory {
	result := map[string]dnfAdvisory{}
	for line := range strings.SplitSeq(out, "\n") {
		fields := strings.Fields(line)
		if len(fields) < 3 || fields[0] == "Name" || !strings.Contains(fields[0], "-") {
			continue
		}
		var adv dnfAdvisory
		adv.ID = fields[0]
		key := ""
		for _, f := range fields[1:] {
			if name, arch, ok := parseNEVRA(f); ok {
				key = name + "." + arch
				break
			}
			lower := strings.ToLower(f)
			switch {
			case strings.HasSuffix(lower, "/sec."):
				adv.Security = true
				adv.Severity = severityName(strings.TrimSuffix(lower, "/sec."))
			case lower == "security" || lower == "sec.":
				adv.Security = true
			case severityRank[lower] > 0:
				adv.Severity = severityName(lower)
			}
		}
		if key == "" {
			continue
		}
		if prev, ok := result[key]; ok && !better(adv, prev) {
			continue
		}
		result[key] = adv
	}
	return result
}

func severityName(lower string) string {
	if severityRank[lower] == 0 {
		return ""
	}
	return strings.ToUpper(lower[:1]) + lower[1:]
}

func better(a, b dnfAdvisory) bool {
	if a.Security != b.Security {
		return a.Security
	}
	return severityRank[strings.ToLower(a.Severity)] > severityRank[strings.ToLower(b.Severity)]
}

// parseDnfVersionlock reads the locked package names, in either form:
//
//	openssl-1:3.0.7-24.el9.*              dnf 4 (python3-dnf-plugin-versionlock)
//	Package name: openssl                 dnf 5
func parseDnfVersionlock(out string) map[string]bool {
	result := map[string]bool{}
	for line := range strings.SplitSeq(out, "\n") {
		line = strings.TrimSpace(line)
		if line == "" || strings.HasPrefix(line, "#") || strings.HasPrefix(line, "!") {
			continue
		}
		if name, ok := strings.CutPrefix(line, "Package name:"); ok {
			if name = strings.TrimSpace(name); name != "" {
				result[name] = true
			}
			continue
		}
		if strings.Contains(line, " ") {
			continue
		}
		spec := strings.TrimSuffix(line, ".*")
		i := strings.LastIndexByte(spec, '-')
		if i <= 0 {
			continue
		}
		j := strings.LastIndexByte(spec[:i], '-')
		if j <= 0 {
			continue
		}
		result[spec[:j]] = true
	}
	return result
}

func dnfToProto(updates []*dnfUpdate, advisories map[string]dnfAdvisory, locked map[string]bool) []*agentv1.PatchUpdate {
	result := make([]*agentv1.PatchUpdate, 0, len(updates))
	for _, u := range updates {
		p := &agentv1.PatchUpdate{
			Id:               u.Name + ":" + u.Arch,
			Name:             u.Name,
			Version:          u.Candidate,
			InstalledVersion: u.Installed,
			Classification:   classificationOther,
			Origin:           u.Repo,
			Held:             locked[u.Name],
		}
		if adv, ok := advisories[u.Name+"."+u.Arch]; ok {
			p.Advisory = adv.ID
			if adv.Security {
				p.Classification = classificationSecurity
				p.Severity = adv.Severity
			}
		}
		result = append(result, p)
	}
	return result
}

// parseDnfRepoProblems names the repositories dnf could not read, from its warnings ("Failed to download metadata for repo 'epel'",
// dnf 5: "Failed to download metadata ... for repository 'epel'").
func parseDnfRepoProblems(stderr string) []string {
	var repos []string
	for line := range strings.SplitSeq(stderr, "\n") {
		lower := strings.ToLower(line)
		if !strings.Contains(lower, "failed to download metadata") && !strings.Contains(lower, "cannot download repomd") {
			continue
		}
		m := dnfRepoPattern.FindStringSubmatch(line)
		if m == nil {
			continue
		}
		repo := strings.TrimSuffix(m[1], ":")
		if repo != "" && !slices.Contains(repos, repo) {
			repos = append(repos, repo)
		}
	}
	return repos
}

// firstDnfError is the first line of dnf that says what went wrong.
func firstDnfError(stderr, stdout string, code int) string {
	for _, text := range []string{stderr, stdout} {
		for line := range strings.SplitSeq(text, "\n") {
			line = strings.TrimSpace(line)
			if line == "" || strings.HasPrefix(line, "Last metadata expiration") || strings.HasPrefix(line, "Updating and loading") {
				continue
			}
			return strings.TrimPrefix(line, "Error: ")
		}
	}
	return "dnf ended with exit code " + strconv.Itoa(code) + "."
}

// needsRestarting asks needs-restarting (dnf-utils, or the dnf plugin) whether the endpoint needs a restart: exit code 1 means it
// does, as the inventory reads it.
func needsRestarting(ctx context.Context, sys system) bool {
	name, args := "needs-restarting", []string{"-r"}
	if !sys.LookPath(name) {
		name, args = "dnf", []string{"needs-restarting", "-r"}
	}
	res := sys.Run(ctx, dnfEnv, name, args...)
	return res.Err == nil && res.Code == 1
}
