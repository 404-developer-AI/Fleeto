//go:build windows

package screen

import (
	"os"
	"os/exec"
	"path/filepath"
	"testing"
)

// A staging folder is created in one step with its access list, never reused: whatever a user put in its place first makes it fail
// (security review of 0.3.0 step 7).
func TestAStagingFolderIsNeverReused(t *testing.T) {
	dir := filepath.Join(t.TempDir(), "session")
	if err := createFolder(dir, rootSDDL); err != nil {
		t.Fatal(err)
	}
	if err := createFolder(dir, rootSDDL); err == nil {
		t.Fatal("an existing folder was taken as a new staging folder")
	}
}

// A junction where the staging root belongs is removed itself when the agent starts; the folder it points to keeps its files.
func TestAJunctionInPlaceOfTheStagingRootIsRemovedNotFollowed(t *testing.T) {
	target := t.TempDir()
	keep := filepath.Join(target, "keep.txt")
	if err := os.WriteFile(keep, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	root := filepath.Join(t.TempDir(), "RemoteClipboard")
	if out, err := exec.Command("cmd", "/c", "mklink", "/J", root, target).CombinedOutput(); err != nil {
		t.Skipf("could not create a junction: %v %s", err, out)
	}
	if err := os.RemoveAll(root); err != nil {
		t.Fatal(err)
	}
	if _, err := os.Stat(keep); err != nil {
		t.Fatalf("the folder behind the junction lost its files: %v", err)
	}
	if err := createFolder(root, rootSDDL); err != nil {
		t.Fatal(err)
	}
	if info, err := os.Lstat(root); err != nil || !info.IsDir() || info.Mode()&os.ModeSymlink != 0 {
		t.Fatalf("the staging root is not a folder of its own: %v %v", info, err)
	}
}

// The base folder is secured through a handle to the folder itself: a link is refused, so another folder's access list never changes.
func TestSecuringTheBaseFolderRefusesAJunction(t *testing.T) {
	target := t.TempDir()
	link := filepath.Join(t.TempDir(), "Fleeto")
	if out, err := exec.Command("cmd", "/c", "mklink", "/J", link, target).CombinedOutput(); err != nil {
		t.Skipf("could not create a junction: %v %s", err, out)
	}
	if err := secureFolder(link, baseSDDL); err == nil {
		t.Fatal("a junction was secured as the base folder")
	}
}
