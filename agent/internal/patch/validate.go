package patch

import (
	"errors"
	"fmt"
	"regexp"
	"strings"
	"unicode"
	"unicode/utf8"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// The body of a PatchInstallJob is validated before anything runs (PATCHING.md section 11): what reaches apt, dnf or the Windows
// Update Agent is an identifier of a known form, passed as an argument, never through a shell.

const (
	// MaxSpecifiedUpdates is the most updates SCOPE_SPECIFIED may name.
	MaxSpecifiedUpdates = 100
	// MaxExcludedNames is the most names a filter may exclude.
	MaxExcludedNames = 20
	// MinRestartNotice and MaxRestartNotice bound the seconds between the notice and the restart.
	MinRestartNotice = 60
	MaxRestartNotice = 86400

	maxFilterValues    = 20
	maxFilterValueLen  = 100
	maxRestartMessage  = 2000
	maxJobTitleLen     = 1000
	maxPackageIDLength = 300
)

var (
	// dpkgIDPattern is "name:arch" of a Debian package (Debian policy for the name).
	dpkgIDPattern = regexp.MustCompile(`^[a-z0-9][a-z0-9+.\-]*:[a-z0-9_]+$`)
	// rpmIDPattern is "name:arch" of an rpm package; rpm names also allow uppercase and "_".
	rpmIDPattern = regexp.MustCompile(`^[A-Za-z0-9_+][A-Za-z0-9_.+\-]*:[a-z0-9_]+$`)
	// wuaIDPattern is "UpdateID:RevisionNumber" as the scan reports it.
	wuaIDPattern = regexp.MustCompile(`^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}:\d{1,6}$`)
	// packageVersionPattern holds the characters dpkg and rpm allow in a version ("1:3.0.2-0ubuntu1.12", "3.0.7-25.el9_3").
	packageVersionPattern = regexp.MustCompile(`^[0-9A-Za-z.+~:_\-]{1,100}$`)
	// dpkgPackagePattern and rpmPackagePattern are a package name alone, as excluded names are given.
	dpkgPackagePattern = regexp.MustCompile(`^[a-z0-9][a-z0-9+.\-]*$`)
	rpmPackagePattern  = regexp.MustCompile(`^[A-Za-z0-9_+][A-Za-z0-9_.+\-]*$`)
)

// validUpdateID reports whether an update id has the form of the engine.
func validUpdateID(engine, id string) bool {
	if len(id) > maxPackageIDLength {
		return false
	}
	switch engine {
	case EngineApt:
		return dpkgIDPattern.MatchString(id)
	case EngineDnf:
		return rpmIDPattern.MatchString(id)
	case EngineWindowsUpdate:
		return wuaIDPattern.MatchString(id)
	}
	return false
}

// validPackageName reports whether a package name alone has the form of the engine.
func validPackageName(engine, name string) bool {
	if len(name) > maxNameLen {
		return false
	}
	switch engine {
	case EngineApt:
		return dpkgPackagePattern.MatchString(name)
	case EngineDnf:
		return rpmPackagePattern.MatchString(name)
	}
	return false
}

// validVersion reports whether a version holds only what dpkg and rpm allow.
func validVersion(version string) bool { return packageVersionPattern.MatchString(version) }

// plainText reports whether s is valid UTF-8 of at most limit bytes without control characters.
func plainText(s string, limit int) bool {
	if len(s) > limit || !utf8.ValidString(s) {
		return false
	}
	return !strings.ContainsFunc(s, unicode.IsControl)
}

// ValidateInstallJob checks a job body for the engine of this endpoint (EngineApt, EngineDnf or EngineWindowsUpdate). A body that
// fails is refused with the reason, and nothing runs.
func ValidateInstallJob(job *agentv1.PatchInstallJob, engine string) error {
	if job == nil {
		return errors.New("the job carries no installation")
	}
	if engine != EngineApt && engine != EngineDnf && engine != EngineWindowsUpdate {
		return errors.New("no supported package manager was found on this endpoint")
	}
	windows := engine == EngineWindowsUpdate
	switch job.GetScope() {
	case agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED:
		n := len(job.GetUpdates())
		if n == 0 || n > MaxSpecifiedUpdates {
			return fmt.Errorf("the job names %d updates; it must name 1 to %d", n, MaxSpecifiedUpdates)
		}
		for _, u := range job.GetUpdates() {
			if !validUpdateID(engine, u.GetId()) {
				return fmt.Errorf("the update id %q is not of the form this endpoint uses", cut(u.GetId(), 100))
			}
			if !plainText(u.GetTitle(), maxJobTitleLen) {
				return fmt.Errorf("the title of update %s is not plain text", u.GetId())
			}
			if windows {
				if u.GetVersion() != "" {
					return fmt.Errorf("update %s carries a version, which a Windows update has not", u.GetId())
				}
			} else if !validVersion(u.GetVersion()) {
				return fmt.Errorf("the version of update %s is missing or holds characters a package version cannot have", u.GetId())
			}
		}
	case agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING:
		if len(job.GetUpdates()) > 0 {
			return errors.New("an installation of everything missing names no updates")
		}
	default:
		return errors.New("the job does not say what to install")
	}

	f := job.GetFilter()
	if n := len(f.GetExcludedNames()); n > MaxExcludedNames {
		return fmt.Errorf("the filter excludes %d names; at most %d are allowed", n, MaxExcludedNames)
	}
	for _, name := range f.GetExcludedNames() {
		if windows {
			if strings.TrimSpace(name) == "" || !plainText(name, maxFilterValueLen) {
				return fmt.Errorf("the excluded title %q is not plain text of at most %d characters", cut(name, 100), maxFilterValueLen)
			}
		} else if !validPackageName(engine, name) {
			return fmt.Errorf("the excluded name %q is not a package name", cut(name, 100))
		}
	}
	for what, values := range map[string][]string{"classifications": f.GetClassifications(), "severities": f.GetSeverities()} {
		if len(values) > maxFilterValues {
			return fmt.Errorf("the filter names %d %s; at most %d are allowed", len(values), what, maxFilterValues)
		}
		for _, v := range values {
			if !plainText(v, maxFilterValueLen) {
				return fmt.Errorf("the filter holds %s that are not plain text", what)
			}
		}
	}

	switch job.GetRestart() {
	case agentv1.PatchRestart_PATCH_RESTART_IF_REQUIRED:
		if s := job.GetRestartNoticeSeconds(); s < MinRestartNotice || s > MaxRestartNotice {
			return fmt.Errorf("the restart notice of %d seconds is outside %d to %d", s, MinRestartNotice, MaxRestartNotice)
		}
	case agentv1.PatchRestart_PATCH_RESTART_NEVER:
		if s := job.GetRestartNoticeSeconds(); s != 0 && (s < MinRestartNotice || s > MaxRestartNotice) {
			return fmt.Errorf("the restart notice of %d seconds is outside %d to %d", s, MinRestartNotice, MaxRestartNotice)
		}
	default:
		return errors.New("the job does not say whether the endpoint may restart")
	}
	if len(job.GetRestartMessage()) > maxRestartMessage || !utf8.ValidString(job.GetRestartMessage()) {
		return errors.New("the restart message is too long or not valid text")
	}
	return nil
}
