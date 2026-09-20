//go:build windows

package remote

import (
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

// A junction needs no privilege on Windows, so a standard user can make one: an upload into a folder that is a junction is refused, and
// nothing appears behind it (security review of 0.3.0 step 7).
func TestAnUploadIntoAJunctionIsRefused(t *testing.T) {
	bs := startBackground(t)
	target := t.TempDir()
	link := filepath.Join(t.TempDir(), "Downloads")
	if out, err := exec.Command("cmd", "/c", "mklink", "/J", link, target).CombinedOutput(); err != nil {
		t.Skipf("could not create a junction: %v %s", err, out)
	}
	if bs.request("upload", map[string]any{"path": link, "name": "version.dll", "size": 4, "offset": 0})["ok"] == true {
		t.Fatal("an upload into a junction was accepted")
	}
	if entries, _ := os.ReadDir(target); len(entries) != 0 {
		t.Fatalf("files appeared behind the junction: %v", entries)
	}
}
