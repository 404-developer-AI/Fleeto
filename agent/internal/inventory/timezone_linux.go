//go:build linux

package inventory

import "os"

// timeZone is the IANA name of the endpoint's time zone (0.9.0): /etc/timezone (Debian family), else the target of the
// /etc/localtime symlink (every systemd distribution), else "".
func timeZone() string {
	if data, err := os.ReadFile("/etc/timezone"); err == nil {
		if name := ianaTimeZone(string(data)); name != "" {
			return name
		}
	}
	if target, err := os.Readlink("/etc/localtime"); err == nil {
		return zoneFromLocaltimeTarget(target)
	}
	return ""
}
