package inventory

import "testing"

// The /etc.defaults/VERSION of a Synology NAS with DSM 7.1.1-42962 Update 8 (2026-11-01).
const dsm711Update8 = `majorversion="7"
minorversion="1"
major="7"
minor="1"
micro="1"
productversion="7.1.1"
buildphase="GM"
buildnumber="42962"
smallfixnumber="8"
nano="8"
base="42962"
builddate="2025/02/07"
buildtime="11:18:23"
`

func TestDSMNameNamesDSMAsSynologyDoes(t *testing.T) {
	cases := []struct {
		name    string
		content string
		want    string
	}{
		{"with an update", dsm711Update8, "Synology DSM 7.1.1-42962 Update 8"},
		{"without an update", "productversion=\"7.2\"\nbuildnumber=\"64570\"\nsmallfixnumber=\"0\"\n", "Synology DSM 7.2-64570"},
		{"without productversion", "majorversion=\"6\"\nminorversion=\"2\"\nbuildnumber=\"25556\"\n", "Synology DSM 6.2-25556"},
		{"not DSM", "NAME=\"Ubuntu\"\nVERSION_ID=\"24.04\"\n", ""},
		{"empty", "", ""},
	}
	for _, c := range cases {
		if got := DSMName(c.content); got != c.want {
			t.Errorf("%s: DSMName = %q, want %q", c.name, got, c.want)
		}
	}
}
