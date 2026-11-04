//go:build windows

package inventory

import "golang.org/x/sys/windows/registry"

// timeZone is the Windows name of the endpoint's time zone (0.9.0), such as "Romance Standard Time", from the key name Windows
// keeps for the active time zone (what GetDynamicTimeZoneInformation returns as TimeZoneKeyName); "" when it cannot be read.
func timeZone() string {
	k, err := registry.OpenKey(registry.LOCAL_MACHINE, `SYSTEM\CurrentControlSet\Control\TimeZoneInformation`, registry.QUERY_VALUE|registry.WOW64_64KEY)
	if err != nil {
		return ""
	}
	defer k.Close()
	return windowsTimeZone(stringValue(k, "TimeZoneKeyName"))
}
