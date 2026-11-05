package patch

import (
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// The selection of an installation (0.9.0 step 3, PATCHING.md section 5): which of the updates a fresh scan reports as missing a
// PatchInstallJob installs, and what it says about the others. Pure functions, so they are tested on captured scans.

// The status of one update in a "fleeto-patch:" line of the job output, which the server reads for the deployment history.
const (
	StatusInstalled  = "installed"
	StatusFailed     = "failed"
	StatusNotMissing = "not-missing"
	StatusHeld       = "held"
	StatusExcluded   = "excluded"
)

// The restart state in the last "fleeto-patch: restart" line.
const (
	RestartNotRequired = "not-required"
	RestartRequired    = "required"
	RestartRestarting  = "restarting"
)

const (
	linePrefix  = "fleeto-patch: "
	maxTitleLen = 200
	// severityUnspecified matches an update without a severity in a severity filter.
	severityUnspecified = "Unspecified"
)

// Target is one update the installation installs.
type Target struct {
	// ID is PatchUpdate.id: "name:arch" on Linux, "UpdateID:RevisionNumber" on Windows.
	ID    string
	Title string
	// Name and Arch are the package (Linux).
	Name, Arch string
	// Version is what to install (Linux): the version of the job for SPECIFIED, the candidate of the scan for ALL_MISSING.
	Version string
	// Installed is the installed version the scan reported; empty for a new dependency.
	Installed string
	// KB is the first KB number of a Windows update, for the output only.
	KB string
}

// Outcome is one "fleeto-patch:" line.
type Outcome struct {
	Status, ID, Title string
}

// Selection is what an installation does with the scan before it.
type Selection struct {
	Targets []Target
	// Outcomes are the updates that are not installed: not-missing, held and excluded, in order.
	Outcomes []Outcome
	// Narrowed is set when the selection leaves out something a full upgrade would install: the job named updates, or its filter
	// left out or excluded a missing update. Without it apt runs a dist-upgrade.
	Narrowed bool
}

// Select applies a job to the updates a scan reports as missing.
func Select(job *agentv1.PatchInstallJob, report *agentv1.PatchScanReport) Selection {
	windows := report.GetEngine() == EngineWindowsUpdate
	if job.GetScope() == agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED {
		return selectSpecified(job, report)
	}
	var sel Selection
	filter := job.GetFilter()
	allowed := allowedIDs(filter)
	for _, u := range report.GetUpdates() {
		switch {
		case u == nil || u.GetRemoval():
			// A removal is not installed on its own; a dist-upgrade decides it with the upgrade that needs it.
		case u.GetHeld():
			sel.Outcomes = append(sel.Outcomes, Outcome{StatusHeld, u.GetId(), u.GetName()})
		case excludedByName(u, filter, windows):
			sel.Outcomes = append(sel.Outcomes, Outcome{StatusExcluded, u.GetId(), u.GetName()})
			sel.Narrowed = true
		case !passesFilter(u, filter, windows):
			// Outside the filter: not part of this installation, so not listed.
			sel.Narrowed = true
		case allowed != nil && !allowed[u.GetId()]:
			// Not on the list fleeto-signer made (its deferral has not passed, or on Linux its severity is not selected): left out,
			// so the installation never installs more than the server allowed.
			sel.Outcomes = append(sel.Outcomes, Outcome{StatusExcluded, u.GetId(), u.GetName()})
			sel.Narrowed = true
		default:
			sel.Targets = append(sel.Targets, targetOf(u, u.GetVersion(), u.GetName()))
		}
	}
	return sel
}

// selectSpecified takes exactly the named updates that are still missing.
func selectSpecified(job *agentv1.PatchInstallJob, report *agentv1.PatchScanReport) Selection {
	sel := Selection{Narrowed: true}
	byID := map[string]*agentv1.PatchUpdate{}
	for _, u := range report.GetUpdates() {
		if u != nil {
			byID[u.GetId()] = u
		}
	}
	seen := map[string]bool{}
	for _, named := range job.GetUpdates() {
		id := named.GetId()
		if seen[id] {
			continue
		}
		seen[id] = true
		found := byID[id]
		title := named.GetTitle()
		if title == "" && found != nil {
			title = found.GetName()
		}
		switch {
		case found == nil:
			sel.Outcomes = append(sel.Outcomes, Outcome{StatusNotMissing, id, title})
		case found.GetHeld():
			sel.Outcomes = append(sel.Outcomes, Outcome{StatusHeld, id, title})
		case found.GetRemoval():
			// A package an upgrade would remove cannot be installed by name.
			sel.Outcomes = append(sel.Outcomes, Outcome{StatusExcluded, id, title})
		default:
			sel.Targets = append(sel.Targets, targetOf(found, named.GetVersion(), title))
		}
	}
	return sel
}

// allowedIDs is the set of filter.allowed_ids when filter.restrict_to_ids is set, and nil otherwise. With restrict_to_ids and an
// empty list the set is empty, so nothing is installed.
func allowedIDs(f *agentv1.PatchInstallFilter) map[string]bool {
	if !f.GetRestrictToIds() {
		return nil
	}
	set := make(map[string]bool, len(f.GetAllowedIds()))
	for _, id := range f.GetAllowedIds() {
		set[id] = true
	}
	return set
}

func targetOf(u *agentv1.PatchUpdate, version, title string) Target {
	t := Target{ID: u.GetId(), Title: title, Version: version, Installed: u.GetInstalledVersion()}
	if name, arch, ok := strings.Cut(u.GetId(), ":"); ok {
		t.Name, t.Arch = name, arch
	}
	if kbs := u.GetKbNumbers(); len(kbs) > 0 {
		t.KB = kbs[0]
	}
	return t
}

// excludedByName: a package name on Linux (exact), a case-insensitive part of the title on Windows.
func excludedByName(u *agentv1.PatchUpdate, f *agentv1.PatchInstallFilter, windows bool) bool {
	for _, name := range f.GetExcludedNames() {
		if name == "" {
			continue
		}
		if windows {
			if strings.Contains(strings.ToLower(u.GetName()), strings.ToLower(name)) {
				return true
			}
		} else if u.GetName() == name {
			return true
		}
	}
	return false
}

// passesFilter applies the filter of ALL_MISSING: security only on Linux; classifications, severities, the release date and drivers
// on Windows. Feature updates are not installed in 0.9.0.
func passesFilter(u *agentv1.PatchUpdate, f *agentv1.PatchInstallFilter, windows bool) bool {
	if !windows {
		return !f.GetSecurityOnly() || u.GetClassification() == classificationSecurity
	}
	if u.GetIsFeatureUpdate() {
		return false
	}
	if u.GetIsDriver() && !f.GetIncludeDrivers() {
		return false
	}
	if c := f.GetClassifications(); len(c) > 0 && !containsFold(c, u.GetClassification()) {
		return false
	}
	if s := f.GetSeverities(); len(s) > 0 {
		severity := u.GetSeverity()
		if severity == "" {
			severity = severityUnspecified
		}
		if !containsFold(s, severity) {
			return false
		}
	}
	if before := f.GetReleasedBefore(); before != nil {
		// An update without a release date cannot be shown to be old enough for the deferral.
		if u.GetReleasedAt() == nil || !u.GetReleasedAt().AsTime().Before(before.AsTime()) {
			return false
		}
	}
	return true
}

func containsFold(list []string, s string) bool {
	for _, v := range list {
		if strings.EqualFold(strings.TrimSpace(v), s) {
			return true
		}
	}
	return false
}

// Line is one "fleeto-patch:" line of an update, ending in a newline.
func Line(status, id, title string) string {
	id = strings.ReplaceAll(cut(id, maxNameLen+1+maxNameLen), " ", "")
	line := linePrefix + status + " " + id
	if title = cut(title, maxTitleLen); title != "" {
		line += " " + title
	}
	return line + "\n"
}

// RestartLine is the "fleeto-patch: restart" line, ending in a newline.
func RestartLine(state string) string { return linePrefix + "restart " + state + "\n" }
