//go:build windows

package remote

import (
	"os"
	"os/exec"
	"path/filepath"
	"testing"

	"golang.org/x/sys/windows"
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

// A folder may be given by its short (8.3) name, as the temporary folder of a build agent is: an upload into it must work, because the
// check compares what Windows resolves on both sides (found by CI after the security review of 0.3.0 step 7).
func TestAnUploadIntoAFolderWithAShortName(t *testing.T) {
	bs := startBackground(t)
	dir := t.TempDir()
	short := shortPath(t, dir)
	if short == dir {
		t.Skip("this file system keeps no short names")
	}
	if tb := bs.uploadWhole(t, short, "notes.txt", []byte("hello")); tb.Kind != "end" {
		t.Fatalf("upload into %s: %s", short, tb.Error)
	}
	if got, err := os.ReadFile(filepath.Join(dir, "notes.txt")); err != nil || string(got) != "hello" {
		t.Fatalf("the file is %q %v", got, err)
	}
}

func shortPath(t *testing.T, path string) string {
	t.Helper()
	name, err := windows.UTF16PtrFromString(path)
	if err != nil {
		t.Fatal(err)
	}
	buf := make([]uint16, windows.MAX_LONG_PATH)
	n, err := windows.GetShortPathName(name, &buf[0], uint32(len(buf)))
	if err != nil || n == 0 {
		t.Skipf("no short name for %s: %v", path, err)
	}
	return windows.UTF16ToString(buf[:n])
}
