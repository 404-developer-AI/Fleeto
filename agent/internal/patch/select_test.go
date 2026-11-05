package patch

import (
	"reflect"
	"testing"
	"time"

	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func linuxReport() *agentv1.PatchScanReport {
	return &agentv1.PatchScanReport{Engine: EngineApt, Updates: []*agentv1.PatchUpdate{
		{Id: "libssl3:amd64", Name: "libssl3", Version: "3.0.2-0ubuntu1.12", InstalledVersion: "3.0.2-0ubuntu1.10", Classification: "security"},
		{Id: "curl:amd64", Name: "curl", Version: "7.81.0-1ubuntu1.15", InstalledVersion: "7.81.0-1ubuntu1.14", Classification: "other"},
		{Id: "linux-image-generic:amd64", Name: "linux-image-generic", Classification: "other", Held: true},
		{Id: "oldpkg:amd64", Name: "oldpkg", Removal: true},
	}}
}

func windowsReport(now time.Time) *agentv1.PatchScanReport {
	return &agentv1.PatchScanReport{Engine: EngineWindowsUpdate, Updates: []*agentv1.PatchUpdate{
		{Id: "11111111-1111-1111-1111-111111111111:200", Name: "2026-10 Cumulative Update for Windows 11 (KB5031354)",
			Classification: "Security Updates", Severity: "Critical", KbNumbers: []string{"KB5031354"}, ReleasedAt: timestamppb.New(now.AddDate(0, 0, -20))},
		{Id: "22222222-2222-2222-2222-222222222222:1", Name: "Update for Microsoft Defender Antivirus",
			Classification: "Definition Updates", ReleasedAt: timestamppb.New(now.AddDate(0, 0, -1))},
		{Id: "33333333-3333-3333-3333-333333333333:1", Name: "Intel - Display - 31.0.101", Classification: "Drivers", IsDriver: true,
			ReleasedAt: timestamppb.New(now.AddDate(0, 0, -30))},
		{Id: "44444444-4444-4444-4444-444444444444:1", Name: "Windows 11, version 25H2", Classification: "Upgrades", IsFeatureUpdate: true,
			ReleasedAt: timestamppb.New(now.AddDate(0, 0, -60))},
		{Id: "55555555-5555-5555-5555-555555555555:3", Name: "Microsoft .NET Framework update", Classification: "Updates"},
	}}
}

func targetIDs(sel Selection) []string {
	var ids []string
	for _, t := range sel.Targets {
		ids = append(ids, t.ID)
	}
	return ids
}

func TestSelectOnLinux(t *testing.T) {
	all := agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING
	cases := []struct {
		name     string
		job      *agentv1.PatchInstallJob
		targets  []string
		outcomes []Outcome
		narrowed bool
	}{
		{"everything missing without a filter", &agentv1.PatchInstallJob{Scope: all},
			[]string{"libssl3:amd64", "curl:amd64"},
			[]Outcome{{StatusHeld, "linux-image-generic:amd64", "linux-image-generic"}}, false},
		{"security only", &agentv1.PatchInstallJob{Scope: all, Filter: &agentv1.PatchInstallFilter{SecurityOnly: true}},
			[]string{"libssl3:amd64"},
			[]Outcome{{StatusHeld, "linux-image-generic:amd64", "linux-image-generic"}}, true},
		{"an excluded name", &agentv1.PatchInstallJob{Scope: all, Filter: &agentv1.PatchInstallFilter{ExcludedNames: []string{"curl", "Curl"}}},
			[]string{"libssl3:amd64"},
			[]Outcome{{StatusExcluded, "curl:amd64", "curl"}, {StatusHeld, "linux-image-generic:amd64", "linux-image-generic"}}, true},
		{"only the allowed ids", &agentv1.PatchInstallJob{Scope: all, Filter: &agentv1.PatchInstallFilter{RestrictToIds: true,
			AllowedIds: []string{"curl:amd64", "linux-image-generic:amd64", "vim:amd64"}}},
			[]string{"curl:amd64"},
			[]Outcome{{StatusExcluded, "libssl3:amd64", "libssl3"}, {StatusHeld, "linux-image-generic:amd64", "linux-image-generic"}}, true},
		{"allowed ids still meet the filter", &agentv1.PatchInstallJob{Scope: all, Filter: &agentv1.PatchInstallFilter{RestrictToIds: true,
			AllowedIds: []string{"curl:amd64", "libssl3:amd64"}, SecurityOnly: true}},
			[]string{"libssl3:amd64"},
			[]Outcome{{StatusHeld, "linux-image-generic:amd64", "linux-image-generic"}}, true},
		{"an empty allowed list installs nothing", &agentv1.PatchInstallJob{Scope: all, Filter: &agentv1.PatchInstallFilter{RestrictToIds: true}},
			nil,
			[]Outcome{{StatusExcluded, "libssl3:amd64", "libssl3"}, {StatusExcluded, "curl:amd64", "curl"},
				{StatusHeld, "linux-image-generic:amd64", "linux-image-generic"}}, true},
		{"allowed ids without restrict_to_ids are ignored", &agentv1.PatchInstallJob{Scope: all, Filter: &agentv1.PatchInstallFilter{
			AllowedIds: []string{"curl:amd64"}}},
			[]string{"libssl3:amd64", "curl:amd64"},
			[]Outcome{{StatusHeld, "linux-image-generic:amd64", "linux-image-generic"}}, false},
		{"named updates", &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED, Updates: []*agentv1.PatchInstallUpdate{
			{Id: "curl:amd64", Title: "curl", Version: "7.81.0-1ubuntu1.15"},
			{Id: "curl:amd64", Title: "curl again", Version: "7.81.0-1ubuntu1.15"},
			{Id: "vim:amd64", Title: "vim", Version: "2:8.2.3995-1ubuntu2.20"},
			{Id: "linux-image-generic:amd64", Title: "kernel", Version: "6.8.0-49.49"},
			{Id: "oldpkg:amd64", Version: "1.0"},
		}}, []string{"curl:amd64"},
			[]Outcome{{StatusNotMissing, "vim:amd64", "vim"}, {StatusHeld, "linux-image-generic:amd64", "kernel"}, {StatusExcluded, "oldpkg:amd64", "oldpkg"}}, true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			sel := Select(c.job, linuxReport())
			if got := targetIDs(sel); !reflect.DeepEqual(got, c.targets) {
				t.Errorf("targets %v, want %v", got, c.targets)
			}
			if !reflect.DeepEqual(sel.Outcomes, c.outcomes) {
				t.Errorf("outcomes %v, want %v", sel.Outcomes, c.outcomes)
			}
			if sel.Narrowed != c.narrowed {
				t.Errorf("narrowed %v, want %v", sel.Narrowed, c.narrowed)
			}
		})
	}
}

func TestSelectedVersionsComeFromTheJobForNamedUpdatesAndFromTheScanOtherwise(t *testing.T) {
	named := Select(&agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED,
		Updates: []*agentv1.PatchInstallUpdate{{Id: "libssl3:amd64", Version: "3.0.2-0ubuntu1.11"}}}, linuxReport())
	if got := named.Targets[0]; got.Version != "3.0.2-0ubuntu1.11" || got.Name != "libssl3" || got.Arch != "amd64" ||
		got.Installed != "3.0.2-0ubuntu1.10" || got.Title != "libssl3" {
		t.Fatalf("named target %+v", got)
	}
	all := Select(&agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING}, linuxReport())
	if all.Targets[0].Version != "3.0.2-0ubuntu1.12" {
		t.Fatalf("a target of everything missing installs the candidate of the scan, got %q", all.Targets[0].Version)
	}
}

func TestSelectOnWindows(t *testing.T) {
	now := time.Now()
	all := agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING
	cu, defender, driver, net := "11111111-1111-1111-1111-111111111111:200", "22222222-2222-2222-2222-222222222222:1",
		"33333333-3333-3333-3333-333333333333:1", "55555555-5555-5555-5555-555555555555:3"
	cases := []struct {
		name     string
		filter   *agentv1.PatchInstallFilter
		targets  []string
		excluded []string
	}{
		{"no filter: software only, never a feature update", nil, []string{cu, defender, net}, nil},
		{"with drivers", &agentv1.PatchInstallFilter{IncludeDrivers: true}, []string{cu, defender, driver, net}, nil},
		{"classifications, case-insensitive", &agentv1.PatchInstallFilter{Classifications: []string{"security updates", "Updates"}}, []string{cu, net}, nil},
		{"severities with unspecified", &agentv1.PatchInstallFilter{Severities: []string{"Critical", "Unspecified"}}, []string{cu, defender, net}, nil},
		{"severities without unspecified", &agentv1.PatchInstallFilter{Severities: []string{"Critical"}}, []string{cu}, nil},
		{"deferral: released more than a week ago, a missing date is not old enough",
			&agentv1.PatchInstallFilter{ReleasedBefore: timestamppb.New(now.AddDate(0, 0, -7))}, []string{cu}, nil},
		{"excluded part of a title", &agentv1.PatchInstallFilter{ExcludedNames: []string{"defender"}}, []string{cu, net}, []string{defender}},
		{"only the allowed ids; outside the filter stays unlisted", &agentv1.PatchInstallFilter{RestrictToIds: true, AllowedIds: []string{cu, driver}},
			[]string{cu}, []string{defender, net}},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			sel := Select(&agentv1.PatchInstallJob{Scope: all, Filter: c.filter}, windowsReport(now))
			if got := targetIDs(sel); !reflect.DeepEqual(got, c.targets) {
				t.Errorf("targets %v, want %v", got, c.targets)
			}
			var excluded []string
			for _, o := range sel.Outcomes {
				if o.Status == StatusExcluded {
					excluded = append(excluded, o.ID)
				}
			}
			if !reflect.DeepEqual(excluded, c.excluded) {
				t.Errorf("excluded %v, want %v", excluded, c.excluded)
			}
		})
	}
	sel := Select(&agentv1.PatchInstallJob{Scope: all}, windowsReport(now))
	if sel.Targets[0].KB != "KB5031354" {
		t.Fatalf("the KB number travels with the target, got %q", sel.Targets[0].KB)
	}
}

func TestTheOutputLinesHaveTheFormTheServerReads(t *testing.T) {
	cases := map[string]string{
		Line(StatusInstalled, "libssl3:amd64", "libssl3"):                         "fleeto-patch: installed libssl3:amd64 libssl3\n",
		Line(StatusNotMissing, "vim:amd64", ""):                                   "fleeto-patch: not-missing vim:amd64\n",
		Line(StatusFailed, "1111:2", "Update\nfleeto-patch: installed forged\tx"): "fleeto-patch: failed 1111:2 Update fleeto-patch: installed forged x\n",
		Line(StatusHeld, "a b:amd64", "  title  "):                                "fleeto-patch: held ab:amd64 title\n",
		RestartLine(RestartNotRequired):                                           "fleeto-patch: restart not-required\n",
		RestartLine(RestartRequired):                                              "fleeto-patch: restart required\n",
		RestartLine(RestartRestarting):                                            "fleeto-patch: restart restarting\n",
	}
	for got, want := range cases {
		if got != want {
			t.Errorf("got %q, want %q", got, want)
		}
	}
	long := Line(StatusInstalled, "x:amd64", string(make([]byte, 0))+stringOf('é', 300))
	if len(long) > len("fleeto-patch: installed x:amd64 ")+maxTitleLen+1 {
		t.Errorf("a title is cut to %d bytes, line is %d bytes", maxTitleLen, len(long))
	}
}

func stringOf(r rune, n int) string {
	out := make([]rune, n)
	for i := range out {
		out[i] = r
	}
	return string(out)
}
