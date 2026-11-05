//go:build windows

package patch

import (
	"context"
	"log/slog"
	"os"
	"runtime"
	"testing"
	"time"

	"github.com/go-ole/go-ole"
)

// TestLiveWindowsUpdateSearch runs one real, read-only search of the Windows Update Agent of this machine (it never downloads or
// installs). Only with FLEETO_WUA_LIVE=1, because it contacts Windows Update and can take minutes.
func TestLiveWindowsUpdateSearch(t *testing.T) {
	if os.Getenv("FLEETO_WUA_LIVE") != "1" {
		t.Skip("set FLEETO_WUA_LIVE=1 to search the Windows Update Agent of this machine")
	}
	ctx, cancel := context.WithTimeout(context.Background(), ScanTimeout)
	defer cancel()
	started := time.Now()
	r := Scan(ctx, "live", ScanOptions{Drivers: os.Getenv("FLEETO_WUA_DRIVERS") == "1"}, slog.Default())
	Finalize(r)
	t.Logf("engine %s, source %s %s, restart %v, %d updates in %s, problem %q", r.GetEngine(), r.GetSource(), r.GetSourceServer(),
		r.GetRestartRequired(), len(r.GetUpdates()), time.Since(started).Round(time.Millisecond), r.GetProblem())
	for _, u := range r.GetUpdates() {
		t.Logf("%s | %s | %s | %s | kb %v | cves %d | released %v | size %d | downloaded %v | restart %v | mandatory %v | driver %v | feature %v",
			u.GetId(), u.GetName(), u.GetClassification(), u.GetSeverity(), u.GetKbNumbers(), len(u.GetCves()),
			u.GetReleasedAt().AsTime().Format(time.DateOnly), u.GetSizeBytes(), u.GetDownloaded(), u.GetRestartRequired(),
			u.GetMandatory(), u.GetIsDriver(), u.GetIsFeatureUpdate())
	}
	if r.GetEngine() != EngineWindowsUpdate {
		t.Fatalf("engine %q", r.GetEngine())
	}
}

// TestScanRefusesASecondSearchWhileOneRuns proves that a search left running by a timed-out scan blocks a second one.
func TestScanRefusesASecondSearchWhileOneRuns(t *testing.T) {
	if !wuaBusy.CompareAndSwap(false, true) {
		t.Fatal("a search runs already")
	}
	defer wuaBusy.Store(false)
	r := Scan(context.Background(), "req", ScanOptions{}, slog.Default())
	if r.GetProblem() != problemWUABusy || r.GetEngine() != EngineWindowsUpdate || r.GetRequestId() != "req" {
		t.Fatalf("report %+v", r)
	}
}

// TestLiveReadsInstalledUpdates reads the installed updates of this machine with the same code, so the VARIANT handling of
// MaxDownloadSize (VT_DECIMAL) and LastDeploymentChangeTime (VT_DATE) is checked on real values also when nothing is missing.
// Read-only, only with FLEETO_WUA_LIVE=1.
func TestLiveReadsInstalledUpdates(t *testing.T) {
	if os.Getenv("FLEETO_WUA_LIVE") != "1" {
		t.Skip("set FLEETO_WUA_LIVE=1 to search the Windows Update Agent of this machine")
	}
	done := make(chan struct{})
	go func() {
		defer close(done)
		runtime.LockOSThread()
		if err := ole.CoInitializeEx(0, ole.COINIT_MULTITHREADED); err != nil {
			t.Errorf("CoInitializeEx: %v", err)
			return
		}
		defer ole.CoUninitialize()
		sess := &comSession{logger: slog.Default()}
		defer sess.close()
		// Installed updates, read-only like the scan itself.
		res, err := sess.Search("IsInstalled=1 and Type='Software'", serverSelectionDefault, "")
		if err != nil {
			t.Errorf("search: %v", err)
			return
		}
		t.Logf("%d installed updates, result code %d", len(res.Updates), res.ResultCode)
		dated, sized := 0, 0
		for i, u := range res.Updates {
			p := mapUpdate(u)
			if p == nil {
				t.Errorf("update %d has no identity: %+v", i, u)
				continue
			}
			if p.GetReleasedAt() != nil {
				dated++
			}
			if p.GetSizeBytes() > 0 {
				sized++
			}
			if i < 8 {
				t.Logf("%s | %s | %s | %s | kb %v | released %v | size %d | restart %v", p.GetId(), p.GetName(), p.GetClassification(),
					p.GetSeverity(), p.GetKbNumbers(), u.LastDeploymentChangeTime.Format(time.DateOnly), p.GetSizeBytes(), p.GetRestartRequired())
			}
		}
		t.Logf("%d with a release date, %d with a size", dated, sized)
	}()
	<-done
}
