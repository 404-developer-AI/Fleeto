package storage

import (
	"context"
	"os"
	"strings"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/checks"
)

// TestRealVolumeScan scans a real volume, for a developer on a real machine: set FLEETO_TEST_STORAGE_VOLUME to C: (from an
// administrator shell, so the master file table can be read) or to a mount point on Linux (as root). Skipped otherwise.
// It prints what the report holds and compares the size of the root with the used space the volume reports.
func TestRealVolumeScan(t *testing.T) {
	volume := os.Getenv("FLEETO_TEST_STORAGE_VOLUME")
	if volume == "" {
		t.Skip("set FLEETO_TEST_STORAGE_VOLUME to scan a real volume")
	}
	drives, err := checks.FixedDrives()
	if err != nil {
		t.Fatalf("list volumes: %v", err)
	}
	var drive *checks.DriveUsage
	for i := range drives {
		if strings.EqualFold(drives[i].Name, volume) {
			drive = &drives[i]
		}
	}
	if drive == nil {
		t.Fatalf("volume %s is not a fixed volume", volume)
	}

	report := ScanVolume(context.Background(), *drive, "")
	used := drive.Total - drive.Free
	t.Logf("method %s, complete %v, %d ms, %d files, %d folders, error %q", report.GetMethod(), report.GetComplete(),
		report.GetDurationMs(), report.GetFileCount(), report.GetFolderCount(), report.GetError())
	if len(report.GetFolders()) == 0 {
		t.Fatal("the report lists no folders")
	}
	root := report.GetFolders()[0]
	t.Logf("root %s: %d bytes; the volume reports %d bytes used (%.1f%%)", root.GetPath(), root.GetSizeBytes(), used,
		float64(root.GetSizeBytes())*100/float64(max(used, 1)))
	for _, f := range report.GetFolders()[:min(15, len(report.GetFolders()))] {
		t.Logf("  %14d  %s", f.GetSizeBytes(), f.GetPath())
	}
	for _, f := range report.GetFiles()[:min(5, len(report.GetFiles()))] {
		t.Logf("  file %14d  %s", f.GetSizeBytes(), f.GetPath())
	}
}
