package patch

import (
	"bytes"
	"context"
	"errors"
	"slices"
	"strings"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// fakeWUAInstall is a Windows Update Agent that installs: it records what it was asked and answers from its fields.
type fakeWUAInstall struct {
	fakeWUA
	collectErr  error
	notFound    []string
	download    wuaOperation
	install     wuaOperation
	installErr  error
	busyLooks   int
	rebootFirst bool

	collected []string
	steps     []string
}

func (f *fakeWUAInstall) Collect(ids []string) ([]string, error) {
	f.steps = append(f.steps, "collect")
	if f.collectErr != nil {
		return nil, f.collectErr
	}
	for _, id := range ids {
		if !slices.Contains(f.notFound, id) {
			f.collected = append(f.collected, id)
		}
	}
	return f.collected, nil
}

func (f *fakeWUAInstall) Download() (wuaOperation, error) {
	f.steps = append(f.steps, "download")
	return f.download, nil
}

func (f *fakeWUAInstall) InstallerBusy() (bool, error) {
	if f.busyLooks > 0 {
		f.busyLooks--
		return true, nil
	}
	return false, nil
}

func (f *fakeWUAInstall) RebootRequiredBeforeInstallation() (bool, error) { return f.rebootFirst, nil }

func (f *fakeWUAInstall) Install() (wuaOperation, error) {
	f.steps = append(f.steps, "install")
	return f.install, f.installErr
}

const (
	cuID       = "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	defenderID = "6f1d3c1e-5a4b-4f7e-9a31-2b8f0c1d2e3f"
)

func wuaWithTwoUpdates() *fakeWUAInstall {
	f := &fakeWUAInstall{}
	f.result = wuaSearchResult{ResultCode: wuaResultSucceeded, Updates: []wuaUpdate{
		{UpdateID: cuID, RevisionNumber: 200, Title: "2026-10 Cumulative Update for Windows 11 (KB5031354)", KBArticleIDs: []string{"5031354"},
			RebootBehavior: 1, Categories: []wuaCategory{{ID: "0fa1201d-4330-4fa8-8ae9-b877473b6441", Type: "UpdateClassification"}}},
		{UpdateID: defenderID, RevisionNumber: 1, Title: "Security Intelligence Update for Microsoft Defender Antivirus",
			Categories: []wuaCategory{{ID: "e0789628-ce08-4437-be74-2495b842f43b", Type: "UpdateClassification"}}},
	}}
	f.download = wuaOperation{ResultCode: wuaResultSucceeded, Updates: []wuaUpdateResult{{ResultCode: 2}, {ResultCode: 2}}}
	f.install = wuaOperation{ResultCode: wuaResultSucceededWithErrors, RebootRequired: true,
		Updates: []wuaUpdateResult{{ResultCode: 2, RebootRequired: true}, {ResultCode: 4, HResult: 0x80242016}}}
	return f
}

func runWUA(t *testing.T, f *fakeWUAInstall, job *agentv1.PatchInstallJob) (InstallResult, string, []time.Duration) {
	t.Helper()
	var buf bytes.Buffer
	var waits []time.Duration
	result := installWUA(context.Background(), f, fakeRegistry{}, job, false, newInstallOutput(&buf, &buf), func(d time.Duration) { waits = append(waits, d) })
	return result, buf.String(), waits
}

func TestWindowsInstallsDownloadsAndReportsPerUpdate(t *testing.T) {
	f := wuaWithTwoUpdates()
	result, out, _ := runWUA(t, f, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if !slices.Equal(f.steps, []string{"collect", "download", "install"}) {
		t.Fatalf("steps %v", f.steps)
	}
	if !slices.Equal(f.collected, []string{cuID + ":200", defenderID + ":1"}) {
		t.Fatalf("collected %v", f.collected)
	}
	for _, want := range []string{
		"Installed 2026-10 Cumulative Update for Windows 11 (KB5031354) — Succeeded",
		"Not installed Security Intelligence Update for Microsoft Defender Antivirus — Failed (0x80242016)",
		"fleeto-patch: installed " + cuID + ":200 2026-10 Cumulative Update for Windows 11 (KB5031354)\n",
		"fleeto-patch: failed " + defenderID + ":1 Security Intelligence Update",
	} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}
	if result.ExitCode != 1 || result.Installed != 1 || !result.RestartRequired {
		t.Fatalf("result %+v", result)
	}
}

func TestWindowsRefusesWhileARestartMustComeFirst(t *testing.T) {
	f := wuaWithTwoUpdates()
	f.rebootFirst = true
	result, out, _ := runWUA(t, f, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if slices.Contains(f.steps, "install") || result.ExitCode != 1 || result.Error != problemRestartFirst || !result.RestartRequired {
		t.Fatalf("steps %v, result %+v", f.steps, result)
	}
	if !strings.Contains(out, "Restart the endpoint first") || strings.Contains(out, "fleeto-patch: installed") {
		t.Fatalf("output:\n%s", out)
	}
}

func TestWindowsWaitsForABusyInstallerAndGivesUpAfterFiveMinutes(t *testing.T) {
	f := wuaWithTwoUpdates()
	f.busyLooks = 2
	result, _, waits := runWUA(t, f, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if len(waits) != 2 || !slices.Contains(f.steps, "install") || result.Installed != 1 {
		t.Fatalf("waits %v, steps %v, result %+v", waits, f.steps, result)
	}

	stuck := wuaWithTwoUpdates()
	stuck.busyLooks = 1000
	result, out, waits := runWUA(t, stuck, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if slices.Contains(stuck.steps, "install") || result.Error != problemInstallerBusy || len(waits) != int(installerBusyWait/installerBusyPoll) {
		t.Fatalf("waits %d, steps %v, result %+v", len(waits), stuck.steps, result)
	}
	if !strings.Contains(out, "fleeto-patch: failed "+cuID+":200") {
		t.Fatalf("output:\n%s", out)
	}
}

func TestWindowsNamedUpdatesThatAreNotMissingOrNotFoundAgain(t *testing.T) {
	f := wuaWithTwoUpdates()
	f.notFound = []string{defenderID + ":1"}
	other := "11111111-2222-3333-4444-555555555555:7"
	f.install = wuaOperation{ResultCode: 2, Updates: []wuaUpdateResult{{ResultCode: 2}}}
	result, out, _ := runWUA(t, f, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED,
		Updates: []*agentv1.PatchInstallUpdate{{Id: cuID + ":200", Title: "KB5031354"}, {Id: defenderID + ":1", Title: "Defender"},
			{Id: other, Title: "Older update"}}})
	for _, want := range []string{"fleeto-patch: installed " + cuID + ":200 KB5031354\n", "fleeto-patch: failed " + defenderID + ":1 Defender\n",
		"fleeto-patch: not-missing " + other + " Older update\n"} {
		if !strings.Contains(out, want) {
			t.Errorf("output lacks %q:\n%s", want, out)
		}
	}
	if result.ExitCode != 1 || result.RestartRequired {
		t.Fatalf("result %+v", result)
	}
}

func TestWindowsSearchFailureInstallsNothing(t *testing.T) {
	f := wuaWithTwoUpdates()
	f.searchErr = &wuaError{Op: "Search", Code: hrNoConnection, Err: errors.New("no connection")}
	result, _, _ := runWUA(t, f, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if len(f.steps) != 0 || result.ExitCode != 1 || !strings.Contains(result.Error, "could not be reached") {
		t.Fatalf("steps %v, result %+v", f.steps, result)
	}
}

func TestWindowsNothingMissingSucceedsAndReportsAPendingRestart(t *testing.T) {
	f := &fakeWUAInstall{}
	f.reboot = true
	result, out, _ := runWUA(t, f, &agentv1.PatchInstallJob{Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING})
	if len(f.steps) != 0 || result.ExitCode != 0 || !result.RestartRequired || !strings.Contains(out, "Nothing to install") {
		t.Fatalf("steps %v, result %+v", f.steps, result)
	}
}

func TestWindowsInstallationSearchesDriversWhenTheJobOrTheConfigurationIncludesThem(t *testing.T) {
	driverID := "77777777-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	newFake := func() *fakeWUAInstall {
		f := wuaWithTwoUpdates()
		f.driverResult = wuaSearchResult{ResultCode: wuaResultSucceeded, Updates: []wuaUpdate{{UpdateID: driverID, RevisionNumber: 4,
			Title: "Intel - Display - 31.0.101", Type: 2}}}
		f.download.Updates = append(f.download.Updates, wuaUpdateResult{ResultCode: 2})
		f.install.Updates = append(f.install.Updates, wuaUpdateResult{ResultCode: 2})
		return f
	}
	all := agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_ALL_MISSING
	cases := []struct {
		name          string
		filter        *agentv1.PatchInstallFilter
		configDrivers bool
		searches      int
		driver        bool
	}{
		{"neither", nil, false, 1, false},
		{"the job's filter", &agentv1.PatchInstallFilter{IncludeDrivers: true}, false, 2, true},
		// The configuration scans drivers, so they are found; the filter of the job still decides whether one is installed.
		{"the configuration only", nil, true, 2, false},
		{"both", &agentv1.PatchInstallFilter{IncludeDrivers: true}, true, 2, true},
	}
	for _, c := range cases {
		t.Run(c.name, func(t *testing.T) {
			f := newFake()
			var buf bytes.Buffer
			installWUA(context.Background(), f, fakeRegistry{}, &agentv1.PatchInstallJob{Scope: all, Filter: c.filter}, c.configDrivers,
				newInstallOutput(&buf, &buf), func(time.Duration) {})
			if len(f.searches) != c.searches {
				t.Fatalf("searches %v", f.searches)
			}
			if got := slices.Contains(f.collected, driverID+":4"); got != c.driver {
				t.Fatalf("driver collected %v, want %v (%v)", got, c.driver, f.collected)
			}
		})
	}

	// A driver named by a deployment is found when the configuration scans drivers.
	f := newFake()
	var buf bytes.Buffer
	result := installWUA(context.Background(), f, fakeRegistry{}, &agentv1.PatchInstallJob{
		Scope: agentv1.PatchInstallScope_PATCH_INSTALL_SCOPE_SPECIFIED, Updates: []*agentv1.PatchInstallUpdate{{Id: driverID + ":4"}}},
		true, newInstallOutput(&buf, &buf), func(time.Duration) {})
	if !slices.Equal(f.collected, []string{driverID + ":4"}) || result.Installed != 1 {
		t.Fatalf("collected %v, result %+v\n%s", f.collected, result, buf.String())
	}
}
