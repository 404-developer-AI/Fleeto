package inventory

import (
	"regexp"
	"strings"
)

const maxTimeZoneLen = 64

var (
	// ianaZonePattern is the form of an IANA time zone name ("Europe/Brussels", "Etc/GMT+1", "UTC").
	ianaZonePattern = regexp.MustCompile(`^[A-Za-z0-9_+\-]+(/[A-Za-z0-9_+\-]+)*$`)
	// windowsZonePattern is the form of a Windows time zone key name ("Romance Standard Time", "UTC-11").
	windowsZonePattern = regexp.MustCompile(`^[A-Za-z0-9 .+\-()]+$`)
)

// ianaTimeZone returns name when it looks like an IANA time zone, otherwise "".
func ianaTimeZone(name string) string {
	name = strings.TrimSpace(name)
	if len(name) > maxTimeZoneLen || !ianaZonePattern.MatchString(name) {
		return ""
	}
	return name
}

// zoneFromLocaltimeTarget reads the IANA name from the target of the /etc/localtime symlink:
// "/usr/share/zoneinfo/Europe/Brussels" and "../usr/share/zoneinfo/Europe/Brussels" give "Europe/Brussels". A target outside a
// zoneinfo directory gives "".
func zoneFromLocaltimeTarget(target string) string {
	target = strings.ReplaceAll(target, "\\", "/")
	i := strings.LastIndex(target, "zoneinfo/")
	if i < 0 {
		return ""
	}
	name := target[i+len("zoneinfo/"):]
	// zoneinfo/posix/... and zoneinfo/right/... hold the same zones with other leap second rules.
	for _, prefix := range []string{"posix/", "right/"} {
		name = strings.TrimPrefix(name, prefix)
	}
	return ianaTimeZone(name)
}

// windowsTimeZone returns name when it looks like a Windows time zone key name, otherwise "".
func windowsTimeZone(name string) string {
	name = strings.TrimSpace(name)
	if len(name) > maxTimeZoneLen || !windowsZonePattern.MatchString(name) {
		return ""
	}
	return name
}
