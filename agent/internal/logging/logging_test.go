package logging

import (
	"os"
	"path/filepath"
	"testing"
	"time"
)

func TestDailyFileRotatesAndPrunesOldFiles(t *testing.T) {
	dir := t.TempDir()
	d, err := NewDailyFile(dir)
	if err != nil {
		t.Fatal(err)
	}
	defer d.Close()

	now := time.Date(2026, 9, 14, 10, 0, 0, 0, time.Local)
	old := filepath.Join(dir, "fleeto-agent-20260901.log")
	recent := filepath.Join(dir, "fleeto-agent-20260910.log")
	for _, p := range []string{old, recent} {
		if err := os.WriteFile(p, []byte("x"), 0o600); err != nil {
			t.Fatal(err)
		}
	}

	d.now = func() time.Time { return now }
	if _, err := d.Write([]byte("first\n")); err != nil {
		t.Fatal(err)
	}
	d.now = func() time.Time { return now.Add(24 * time.Hour) }
	if _, err := d.Write([]byte("second\n")); err != nil {
		t.Fatal(err)
	}

	if _, err := os.Stat(old); !os.IsNotExist(err) {
		t.Errorf("expected %s to be pruned", old)
	}
	if _, err := os.Stat(recent); err != nil {
		t.Errorf("expected %s to be kept: %v", recent, err)
	}
	for _, name := range []string{"fleeto-agent-20260914.log", "fleeto-agent-20260915.log"} {
		if _, err := os.Stat(filepath.Join(dir, name)); err != nil {
			t.Errorf("expected %s: %v", name, err)
		}
	}
}
