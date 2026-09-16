//go:build linux

package inventory

import "testing"

func TestParseDpkgListsInstalledPackagesWithoutTheMaintainerEmail(t *testing.T) {
	out := "ii \tnginx-core\t1.24.0-2ubuntu7\tUbuntu Developers <ubuntu-devel-discuss@lists.ubuntu.com>\n" +
		"rc \tremoved-pkg\t1.0\tSomeone <someone@example.com>\n" +
		"ii \tzlib1g\t1:1.3.dfsg-3.1\tDebian Maintainer\n"

	items := parseDpkg(out)

	if len(items) != 2 {
		t.Fatalf("got %d packages, want 2: %v", len(items), items)
	}
	if items[0].GetName() != "nginx-core" || items[0].GetVersion() != "1.24.0-2ubuntu7" {
		t.Errorf("unexpected first package: %v", items[0])
	}
	if items[0].GetPublisher() != "Ubuntu Developers" {
		t.Errorf("publisher = %q, want the maintainer without the email address", items[0].GetPublisher())
	}
	if items[1].GetName() != "zlib1g" || items[1].GetPublisher() != "Debian Maintainer" {
		t.Errorf("unexpected second package: %v", items[1])
	}
}

func TestParseRpmReadsTheInstallDate(t *testing.T) {
	out := "openssl\t3.0.7-24.el9\tRed Hat, Inc.\t1735689600\n(none)\t\t\t\nkernel\t5.14.0-427.el9\tRed Hat, Inc.\t0\n"

	items := parseRpm(out)

	if len(items) != 2 {
		t.Fatalf("got %d packages, want 2: %v", len(items), items)
	}
	if items[0].GetName() != "openssl" || items[0].GetVersion() != "3.0.7-24.el9" || items[0].GetPublisher() != "Red Hat, Inc." {
		t.Errorf("unexpected package: %v", items[0])
	}
	if items[0].GetInstallDate() != "20250101" {
		t.Errorf("installDate = %q, want 20250101", items[0].GetInstallDate())
	}
	if items[1].GetInstallDate() != "" {
		t.Errorf("a package without an install time got %q", items[1].GetInstallDate())
	}
}

func TestParseUnitsNamesServicesWithoutTheSuffixAndFollowsTheStartType(t *testing.T) {
	units := "  fleeto-agent.service loaded active running Fleeto Agent\n" +
		"  postfix@-.service loaded active running Postfix Mail Transport Agent (instance -)\n" +
		"  cups.service loaded inactive dead CUPS Scheduler\n" +
		"  ssh.socket loaded active listening OpenSSH socket\n" +
		"  short.service loaded active running\n"
	startTypes := map[string]string{"fleeto-agent.service": "automatic", "cups.service": "manual"}

	items := parseUnits(units, startTypes)

	if len(items) != 4 {
		t.Fatalf("got %d services, want 4 (the socket unit is not a service): %v", len(items), items)
	}
	if items[0].GetName() != "fleeto-agent" || items[0].GetState() != "running" || items[0].GetStartType() != "automatic" {
		t.Errorf("unexpected agent service: %v", items[0])
	}
	if items[0].GetDisplayName() != "Fleeto Agent" {
		t.Errorf("displayName = %q, want the unit description", items[0].GetDisplayName())
	}
	if items[1].GetName() != "postfix@-" {
		t.Errorf("a templated unit keeps its instance: %v", items[1])
	}
	if items[2].GetState() != "stopped" || items[2].GetStartType() != "manual" {
		t.Errorf("unexpected cups service: %v", items[2])
	}
	if items[3].GetDisplayName() != "" {
		t.Errorf("a unit without a description got %q", items[3].GetDisplayName())
	}
}

func TestStartTypeUsesTheSameWordsAsWindows(t *testing.T) {
	cases := map[string]string{
		"enabled": "automatic", "enabled-runtime": "automatic", "static": "automatic",
		"disabled": "manual", "masked": "disabled", "bad": "",
	}
	for state, want := range cases {
		if got := startTypeOf(state); got != want {
			t.Errorf("startTypeOf(%q) = %q, want %q", state, got, want)
		}
	}
}

func TestUnitStateSaysStoppedForAOneshotUnitThatFinished(t *testing.T) {
	cases := []struct {
		active, sub, want string
	}{
		{"active", "running", "running"},
		{"active", "exited", "stopped"},
		{"activating", "start", "starting"},
		{"deactivating", "stop", "stopping"},
		{"failed", "failed", "stopped"},
		{"inactive", "dead", "stopped"},
	}
	for _, c := range cases {
		if got := unitState(c.active, c.sub); got != c.want {
			t.Errorf("unitState(%q, %q) = %q, want %q", c.active, c.sub, got, c.want)
		}
	}
}
