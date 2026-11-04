package patch

import (
	"encoding/binary"
	"fmt"
	"math"
	"strings"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// fakeRegistry answers registry reads from maps keyed by "path\name".
type fakeRegistry struct {
	strings map[string]string
	dwords  map[string]uint32
	keys    []string
}

func (r fakeRegistry) String(path, name string) (string, bool) {
	v, ok := r.strings[path+`\`+name]
	return v, ok
}

func (r fakeRegistry) Uint32(path, name string) (uint32, bool) {
	v, ok := r.dwords[path+`\`+name]
	return v, ok
}

func (r fakeRegistry) KeyExists(path string) bool { return containsString(r.keys, path) }

// fakeWUA is a Windows Update Agent that records the searches it was asked for.
type fakeWUA struct {
	muRegistered bool
	muErr        error
	result       wuaSearchResult
	searchErr    error
	reboot       bool
	rebootErr    error

	muAsked  bool
	searches []string
}

func (f *fakeWUA) MicrosoftUpdateRegistered() (bool, error) {
	f.muAsked = true
	return f.muRegistered, f.muErr
}

func (f *fakeWUA) Search(criteria string, selection int32, serviceID string) (wuaSearchResult, error) {
	f.searches = append(f.searches, fmt.Sprintf("%s|%d|%s", criteria, selection, serviceID))
	return f.result, f.searchErr
}

func (f *fakeWUA) RebootRequired() (bool, error) { return f.reboot, f.rebootErr }

func wsusRegistry(server string, use uint32) fakeRegistry {
	return fakeRegistry{
		strings: map[string]string{wuPolicyKey + `\WUServer`: server},
		dwords:  map[string]uint32{wuPolicyAUKey + `\UseWUServer`: use},
	}
}

func TestChooseSource(t *testing.T) {
	cases := []struct {
		name      string
		reg       fakeRegistry
		wua       *fakeWUA
		want      wuaSource
		wantAsked bool
	}{
		{"wsus from group policy", wsusRegistry("https://admin:secret@wsus.contoso.local:8531/ClientWebService?x=1", 1),
			&fakeWUA{muRegistered: true}, wuaSource{Source: SourceWSUS, Server: "wsus.contoso.local:8531"}, false},
		{"wsus without scheme", wsusRegistry("wsus01:8530", 1), &fakeWUA{},
			wuaSource{Source: SourceWSUS, Server: "wsus01:8530"}, false},
		{"WUServer without UseWUServer", wsusRegistry("http://wsus:8530", 0), &fakeWUA{},
			wuaSource{Source: SourceWindowsUpdate}, true},
		{"microsoft update registered", fakeRegistry{}, &fakeWUA{muRegistered: true},
			wuaSource{Source: SourceMicrosoftUpdate, ServerSelection: serverSelectionOthers, ServiceID: microsoftUpdateServiceID}, true},
		{"plain windows update", fakeRegistry{}, &fakeWUA{}, wuaSource{Source: SourceWindowsUpdate}, true},
		{"services unreadable", fakeRegistry{}, &fakeWUA{muErr: &wuaError{Op: "Services", Code: hrAccessDenied}},
			wuaSource{Source: SourceWindowsUpdate}, true},
	}
	for _, c := range cases {
		got, _ := chooseSource(c.reg, c.wua)
		if got != c.want || c.wua.muAsked != c.wantAsked {
			t.Errorf("%s: got %+v (asked %v), want %+v (asked %v)", c.name, got, c.wua.muAsked, c.want, c.wantAsked)
		}
	}
}

func TestHostPortKeepsOnlyHostAndPort(t *testing.T) {
	cases := map[string]string{
		"http://wsus.contoso.local:8530":                "wsus.contoso.local:8530",
		"https://user:pw@wsus:8531/path/to?token=abc#x": "wsus:8531",
		"wsus":                       "wsus",
		" http://10.0.0.5 ":          "10.0.0.5",
		"http://[fd00::5]:8530/":     "[fd00::5]:8530",
		"http://wsus:99999":          "",
		"http://":                    "",
		"http://bad host:8530":       "",
		"http://wsus%0d%0aX-Evil:1/": "",
		"":                           "",
	}
	for in, want := range cases {
		if got := hostPort(in); got != want {
			t.Errorf("hostPort(%q) = %q, want %q", in, got, want)
		}
	}
}

func TestScanWUAMapsTheUpdates(t *testing.T) {
	released := time.Date(2026, 10, 14, 0, 0, 0, 0, time.UTC)
	wua := &fakeWUA{muRegistered: true, reboot: false, result: wuaSearchResult{ResultCode: wuaResultSucceeded, Updates: []wuaUpdate{
		{
			UpdateID: "8B2C3A9E-0D1F-4C55-9E3A-7B1D2C3E4F50", RevisionNumber: 201,
			Title:        "2026-10 Cumulative Update for Windows 11 Version 24H2 for x64-based Systems (KB5066835)",
			KBArticleIDs: []string{"5066835", "5066835", "bad;id"}, MsrcSeverity: "Critical",
			LastDeploymentChangeTime: released, CveIDs: []string{"cve-2026-12345", "CVE-2026-23456", "not-a-cve"},
			MaxDownloadSize: 812345678, IsDownloaded: true, IsMandatory: false, RebootBehavior: 1, Type: 1,
			Categories: []wuaCategory{
				{ID: "6407468e-edc7-4ecd-8c32-521f64cee65e", Name: "Windows 11", Type: "Product"},
				// The name is in the language of the endpoint; the id gives the English name.
				{ID: "0FA1201D-4330-4FA8-8AE9-B877473B6441", Name: "Beveiligingsupdates", Type: "UpdateClassification"},
			},
		},
		{
			UpdateID: "11111111-2222-3333-4444-555555555555", RevisionNumber: 1, Title: "Windows 11, version 25H2",
			RebootBehavior: 2, Type: 1, Categories: []wuaCategory{{ID: "3689bdc8-b205-4af4-8d4a-a63924c5e9d5", Name: "Upgrades", Type: "UpdateClassification"}},
		},
		{
			UpdateID: "22222222-2222-3333-4444-555555555555", RevisionNumber: 3,
			Title:          "Security Intelligence Update for Microsoft Defender Antivirus - KB2267602 (Version 1.441.1.0)",
			KBArticleIDs:   []string{"KB2267602"},
			RebootBehavior: 0, Type: 1, Categories: []wuaCategory{{ID: "e0789628-ce08-4437-be74-2495b842f43b", Type: "UpdateClassification"}},
		},
		{
			UpdateID: "33333333-2222-3333-4444-555555555555", RevisionNumber: 1, Title: "Contoso - Printer - 1.2.3",
			Type: 2, Categories: []wuaCategory{{ID: "0d1b9c2a-0000-0000-0000-000000000000", Name: "Something New", Type: "UpdateClassification"}},
		},
		{UpdateID: "not a guid", Title: "broken"},
	}}}
	reg := fakeRegistry{keys: []string{cbsRebootPendingKey}}
	r := scanWUA(wua, reg)
	if r.GetEngine() != EngineWindowsUpdate || r.GetSource() != SourceMicrosoftUpdate || r.GetSourceServer() != "" || r.GetProblem() != "" {
		t.Fatalf("report %+v", r)
	}
	if !r.GetRestartRequired() {
		t.Error("the component servicing restart indicator is a restart required")
	}
	if len(wua.searches) != 1 || wua.searches[0] != wuaCriteria+"|3|"+microsoftUpdateServiceID {
		t.Fatalf("searches %v", wua.searches)
	}
	if !strings.Contains(wuaCriteria, "Type='Software'") || strings.Contains(wuaCriteria, "Driver") {
		t.Fatal("a scan searches software only")
	}
	if len(r.GetUpdates()) != 4 {
		t.Fatalf("%d updates", len(r.GetUpdates()))
	}
	cu := r.GetUpdates()[0]
	if cu.GetId() != "8b2c3a9e-0d1f-4c55-9e3a-7b1d2c3e4f50:201" || cu.GetVersion() != "" || cu.GetOrigin() != "" ||
		cu.GetClassification() != classificationSecurityUpdates || cu.GetSeverity() != "Critical" ||
		strings.Join(cu.GetKbNumbers(), ",") != "KB5066835" || strings.Join(cu.GetCves(), ",") != "CVE-2026-12345,CVE-2026-23456" ||
		!cu.GetReleasedAt().AsTime().Equal(released) || cu.GetSizeBytes() != 812345678 || !cu.GetDownloaded() ||
		!cu.GetRestartRequired() || cu.GetMandatory() || cu.GetIsDriver() || cu.GetIsFeatureUpdate() {
		t.Errorf("cumulative update %+v", cu)
	}
	upgrade := r.GetUpdates()[1]
	if upgrade.GetClassification() != classificationUpgrades || !upgrade.GetIsFeatureUpdate() || upgrade.GetReleasedAt() != nil ||
		!upgrade.GetRestartRequired() {
		t.Errorf("upgrade %+v", upgrade)
	}
	defs := r.GetUpdates()[2]
	if defs.GetClassification() != "Definition Updates" || defs.GetRestartRequired() || strings.Join(defs.GetKbNumbers(), ",") != "KB2267602" {
		t.Errorf("definitions %+v", defs)
	}
	driver := r.GetUpdates()[3]
	if !driver.GetIsDriver() || driver.GetClassification() != "Something New" {
		t.Errorf("driver %+v", driver)
	}

	Finalize(r)
	if r.GetUpdates()[0].GetClassification() != classificationSecurityUpdates || r.GetUpdates()[1].GetClassification() == "other" {
		t.Errorf("Finalize keeps the Windows classifications, security first: %v, %v",
			r.GetUpdates()[0].GetClassification(), r.GetUpdates()[1].GetClassification())
	}
}

func TestScanWUAOfAWSUSEndpointThatCannotReachIt(t *testing.T) {
	wua := &fakeWUA{muRegistered: true, searchErr: &wuaError{Op: "Search", Code: hrNotFound}, rebootErr: fmt.Errorf("no")}
	r := scanWUA(wua, wsusRegistry("http://wsus.contoso.local:8530/", 1))
	if r.GetSource() != SourceWSUS || r.GetSourceServer() != "wsus.contoso.local:8530" || wua.muAsked {
		t.Fatalf("report %+v, services asked %v", r, wua.muAsked)
	}
	if wua.searches[0] != wuaCriteria+"|0|" {
		t.Fatalf("a WSUS is searched with ssDefault: %v", wua.searches)
	}
	if !strings.Contains(r.GetProblem(), "The WSUS server does not answer") || !strings.Contains(r.GetProblem(), "0x80244019") ||
		len(r.GetUpdates()) != 0 || r.GetRestartRequired() {
		t.Fatalf("problem %q", r.GetProblem())
	}
}

func TestScanWUAWithErrorsReportsAPartialList(t *testing.T) {
	wua := &fakeWUA{result: wuaSearchResult{ResultCode: wuaResultSucceededWithErrors, Updates: []wuaUpdate{
		{UpdateID: "44444444-2222-3333-4444-555555555555", Title: "Update"},
	}}}
	r := scanWUA(wua, fakeRegistry{})
	if r.GetProblem() != problemWUAPartial || len(r.GetUpdates()) != 1 || r.GetSource() != SourceWindowsUpdate {
		t.Fatalf("report %+v", r)
	}
}

func TestWUAProblems(t *testing.T) {
	cases := map[uint32]string{
		hrNameNotResolved:     "Windows Update could not be reached (0x8024402C)",
		hrInetTimeout:         "did not answer in time (0x80072EE2)",
		hrRequestTimeout:      "did not answer in time",
		hrInetCannotConnect:   "could not be reached",
		hrEndpointUnreachable: "could not be reached",
		hrServiceDisabled:     "The Windows Update service (wuauserv) is disabled or not running (0x80070422)",
		hrAUNoService:         "wuauserv",
		hrWUDisabled:          "A policy of the endpoint does not allow it",
		hrDenied:              "refused the connection",
		hrInetSecureFailure:   "Check the clock",
		0x80240FFF:            "The Windows Update Agent search failed (0x80240FFF).",
		0:                     problemWUAInternal,
	}
	for code, want := range cases {
		if got := wuaProblem(code, SourceWindowsUpdate); !strings.Contains(got, want) {
			t.Errorf("0x%08X: %q lacks %q", code, got, want)
		}
	}
	if got := wuaProblem(hrServerError, SourceWSUS); !strings.HasPrefix(got, "The WSUS server answered with an error") {
		t.Errorf("WSUS: %q", got)
	}
	if got := wuaProblem(hrInetTimeout, SourceMicrosoftUpdate); !strings.HasPrefix(got, "Microsoft Update did not answer") {
		t.Errorf("Microsoft Update: %q", got)
	}
}

func decimal(scale, sign byte, hi uint32, lo uint64) [16]byte {
	var raw [16]byte
	binary.LittleEndian.PutUint16(raw[0:2], 14) // VT_DECIMAL, where DECIMAL has its reserved word
	raw[2], raw[3] = scale, sign
	binary.LittleEndian.PutUint32(raw[4:8], hi)
	binary.LittleEndian.PutUint64(raw[8:16], lo)
	return raw
}

func TestDecimalToUint64(t *testing.T) {
	cases := []struct {
		raw  [16]byte
		want uint64
	}{
		{decimal(0, 0, 0, 812345678), 812345678},
		{decimal(0, 0, 0, 0), 0},
		{decimal(2, 0, 0, 12345), 123},
		{decimal(0, 0x80, 0, 5), 0}, // negative
		{decimal(0, 0x80, 0, 0), 0}, // negative zero
		{decimal(29, 0, 0, 5), 0},   // invalid scale
		{decimal(0, 0, 1, 0), 0},    // above uint64
		{decimal(1, 0, 1, 0), 1844674407370955161},
	}
	for _, c := range cases {
		if got := decimalToUint64(c.raw); got != c.want {
			t.Errorf("%v: %d, want %d", c.raw, got, c.want)
		}
	}
}

func TestSizeFromValue(t *testing.T) {
	cases := []struct {
		in   any
		want uint64
	}{
		{int64(5), 5}, {int32(-1), 0}, {uint64(7), 7}, {float64(1e9), 1000000000}, {math.NaN(), 0}, {"123", 123}, {"x", 0},
		{nil, 0}, {true, 0},
	}
	for _, c := range cases {
		if got := sizeFromValue(c.in); got != c.want {
			t.Errorf("%v: %d, want %d", c.in, got, c.want)
		}
	}
}

func TestOleDate(t *testing.T) {
	// 46309 is 2026-10-14; .5 is noon.
	if got, ok := oleDate(46309.5); !ok || !got.Equal(time.Date(2026, 10, 14, 12, 0, 0, 0, time.UTC)) {
		t.Errorf("got %v %v", got, ok)
	}
	for _, missing := range []float64{0, -1, 25569, math.NaN(), math.Inf(1), 3e6} {
		if _, ok := oleDate(missing); ok {
			t.Errorf("%v is not a release date", missing)
		}
	}
}

func TestFinalizeBoundsWindowsFields(t *testing.T) {
	r := &agentv1.PatchScanReport{Engine: EngineWindowsUpdate, Source: strings.Repeat("s", 50), SourceServer: strings.Repeat("h", 400),
		Updates: []*agentv1.PatchUpdate{
			{Id: "b:1", Name: "Other", Classification: "Updates"},
			{Id: "c:1", Name: "Critical", Classification: classificationCriticalUpdates},
			{Id: "a:1", Name: "Security", Classification: classificationSecurityUpdates,
				KbNumbers: []string{"KB1", "kb2; rm -rf", "KB1"}, Cves: []string{"CVE-2026-1234", "x"}},
		}}
	Finalize(r)
	if len(r.GetSource()) != maxSourceLen || len(r.GetSourceServer()) != maxSourceServerLen {
		t.Errorf("source %d, server %d bytes", len(r.GetSource()), len(r.GetSourceServer()))
	}
	got := []string{}
	for _, u := range r.GetUpdates() {
		got = append(got, u.GetClassification())
	}
	if strings.Join(got, ",") != "Security Updates,Critical Updates,Updates" {
		t.Errorf("order %v", got)
	}
	sec := r.GetUpdates()[0]
	if strings.Join(sec.GetKbNumbers(), ",") != "KB1" || strings.Join(sec.GetCves(), ",") != "CVE-2026-1234" {
		t.Errorf("kb %v, cves %v", sec.GetKbNumbers(), sec.GetCves())
	}
}
