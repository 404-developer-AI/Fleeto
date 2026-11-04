package inventory

import "testing"

func TestTimeZoneNames(t *testing.T) {
	for _, tc := range []struct{ in, want string }{
		{"Europe/Brussels\n", "Europe/Brussels"},
		{"Etc/UTC", "Etc/UTC"},
		{"America/Argentina/Buenos_Aires", "America/Argentina/Buenos_Aires"},
		{"Etc/GMT+1", "Etc/GMT+1"},
		{"../etc/passwd", ""},
		{"Europe/Brussels; rm -rf /", ""},
		{"/Europe/Brussels", ""},
		{"", ""},
	} {
		if got := ianaTimeZone(tc.in); got != tc.want {
			t.Errorf("ianaTimeZone(%q) = %q", tc.in, got)
		}
	}
	for _, tc := range []struct{ in, want string }{
		{"/usr/share/zoneinfo/Europe/Brussels", "Europe/Brussels"},
		{"../usr/share/zoneinfo/America/New_York", "America/New_York"},
		{"/usr/share/zoneinfo/posix/Europe/Amsterdam", "Europe/Amsterdam"},
		{"/usr/share/zoneinfo/UTC", "UTC"},
		{"/var/db/timezone/zoneinfo/Asia/Tokyo", "Asia/Tokyo"},
		{"/etc/somewhere/else", ""},
	} {
		if got := zoneFromLocaltimeTarget(tc.in); got != tc.want {
			t.Errorf("zoneFromLocaltimeTarget(%q) = %q", tc.in, got)
		}
	}
	for _, tc := range []struct{ in, want string }{
		{"Romance Standard Time", "Romance Standard Time"},
		{"UTC-11", "UTC-11"},
		{"Russia Time Zone 3", "Russia Time Zone 3"},
		{"bad\x00name", ""},
	} {
		if got := windowsTimeZone(tc.in); got != tc.want {
			t.Errorf("windowsTimeZone(%q) = %q", tc.in, got)
		}
	}
}
