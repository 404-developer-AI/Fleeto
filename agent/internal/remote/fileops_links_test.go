package remote

import (
	"os"
	"os/exec"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
)

// The file explorer of remote background runs as SYSTEM or root and changes files in folders a local user may be able to write in. A
// link the user plants (a junction needs no privilege on Windows) must never redirect a copy, delete, rename, new folder or upload to
// another place: every operation opens its folder one component at a time without following links (safedir.go). The technician still
// sees links in the listing and works in the folder they point to (security review of 0.9.0).

// plantDirLink makes link point to the folder target the way a local user can: a junction on Windows, a symbolic link elsewhere.
func plantDirLink(t *testing.T, link, target string) {
	t.Helper()
	if runtime.GOOS == "windows" {
		if out, err := exec.Command("cmd", "/c", "mklink", "/J", link, target).CombinedOutput(); err != nil {
			t.Skipf("could not create a junction: %v %s", err, out)
		}
		return
	}
	if err := os.Symlink(target, link); err != nil {
		t.Skipf("could not create a symbolic link: %v", err)
	}
}

func writeFile(t *testing.T, path, content string) {
	t.Helper()
	if err := os.MkdirAll(filepath.Dir(path), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(content), 0o644); err != nil {
		t.Fatal(err)
	}
}

func expectRefusedAsLink(t *testing.T, result map[string]any) {
	t.Helper()
	if result["ok"] == true {
		t.Fatal("the operation went through a link")
	}
	if msg, _ := result["error"].(string); !strings.Contains(msg, "is a link") || !strings.Contains(msg, "open the folder it points to") {
		t.Fatalf("the refusal does not say why and what to do: %q", msg)
	}
}

func expectEmpty(t *testing.T, dir string) {
	t.Helper()
	if entries, _ := os.ReadDir(dir); len(entries) != 0 {
		t.Fatalf("something appeared behind the link: %v", entries)
	}
}

// The technician pastes a folder into a folder a user can write in; the user planted a link with the folder's name there.
func TestCopyNeverWritesThroughALinkInTheDestination(t *testing.T) {
	bs := startBackground(t)
	root := t.TempDir()
	src := filepath.Join(root, "src")
	writeFile(t, filepath.Join(src, "sub", "version.dll"), "content")
	dest := filepath.Join(root, "userwritable")
	if err := os.Mkdir(dest, 0o777); err != nil {
		t.Fatal(err)
	}
	protected := t.TempDir()
	plantDirLink(t, filepath.Join(dest, "src"), protected)

	if result := bs.request("copy", map[string]any{"path": src, "dest": dest}); result["ok"] == true {
		t.Fatal("a copy onto a planted link was accepted")
	}
	expectEmpty(t, protected)
}

// The destination folder exists and a folder inside it is a link: the copy creates its folders new and never merges into existing ones.
func TestCopyNeverWritesThroughAnInnerLinkInTheDestination(t *testing.T) {
	bs := startBackground(t)
	root := t.TempDir()
	src := filepath.Join(root, "src")
	writeFile(t, filepath.Join(src, "sub", "payload.txt"), "content")
	dest := filepath.Join(root, "userwritable")
	if err := os.MkdirAll(filepath.Join(dest, "src"), 0o777); err != nil {
		t.Fatal(err)
	}
	protected := t.TempDir()
	plantDirLink(t, filepath.Join(dest, "src", "sub"), protected)

	result := bs.request("copy", map[string]any{"path": src, "dest": dest})
	if result["ok"] == true {
		t.Fatal("a copy into an existing folder holding a link was accepted")
	}
	if msg, _ := result["error"].(string); !strings.Contains(msg, "already exists in the destination") {
		t.Fatalf("the refusal does not say why: %q", msg)
	}
	expectEmpty(t, protected)
}

// The destination itself is reached through a link: refused with the reason.
func TestCopyIntoAFolderBehindALinkIsRefused(t *testing.T) {
	bs := startBackground(t)
	root := t.TempDir()
	writeFile(t, filepath.Join(root, "a.txt"), "content")
	protected := t.TempDir()
	link := filepath.Join(root, "docs")
	plantDirLink(t, link, protected)

	expectRefusedAsLink(t, bs.request("copy", map[string]any{"path": filepath.Join(root, "a.txt"), "dest": link}))
	expectEmpty(t, protected)
}

// A link inside a copied folder is skipped, never followed.
func TestCopyDoesNotFollowALinkInTheSourceTree(t *testing.T) {
	bs := startBackground(t)
	root := t.TempDir()
	src := filepath.Join(root, "src")
	writeFile(t, filepath.Join(src, "keep.txt"), "kept")
	protected := t.TempDir()
	writeFile(t, filepath.Join(protected, "secret.txt"), "secret")
	plantDirLink(t, filepath.Join(src, "link"), protected)

	dest := filepath.Join(root, "out")
	bs.ok("copy", map[string]any{"path": src, "dest": dest})
	if _, err := os.Lstat(filepath.Join(dest, "link")); err == nil {
		t.Fatal("the copy took the link along")
	}
	if got, err := os.ReadFile(filepath.Join(dest, "keep.txt")); err != nil || string(got) != "kept" {
		t.Fatalf("the plain file was not copied: %q %v", got, err)
	}
}

// The technician's normal work keeps working: a folder tree is copied whole into a normal folder, and a file is copied to a new name.
func TestCopyOfAFolderTreeIntoANormalFolder(t *testing.T) {
	bs := startBackground(t)
	root := t.TempDir()
	src := filepath.Join(root, "Reports")
	writeFile(t, filepath.Join(src, "2026", "q1.txt"), "one")
	writeFile(t, filepath.Join(src, "2026", "deep", "q2.txt"), "two")
	writeFile(t, filepath.Join(src, "top.txt"), "top")
	dest := filepath.Join(root, "Backup")
	if err := os.Mkdir(dest, 0o755); err != nil {
		t.Fatal(err)
	}

	result := bs.ok("copy", map[string]any{"path": src, "dest": dest})
	if result["path"] != filepath.Join(dest, "Reports") {
		t.Fatalf("copied to %v", result["path"])
	}
	for rel, want := range map[string]string{"2026/q1.txt": "one", "2026/deep/q2.txt": "two", "top.txt": "top"} {
		if got, err := os.ReadFile(filepath.Join(dest, "Reports", filepath.FromSlash(rel))); err != nil || string(got) != want {
			t.Fatalf("%s is %q %v", rel, got, err)
		}
	}
	bs.ok("copy", map[string]any{"path": filepath.Join(src, "top.txt"), "dest": filepath.Join(dest, "renamed.txt")})
	if got, err := os.ReadFile(filepath.Join(dest, "renamed.txt")); err != nil || string(got) != "top" {
		t.Fatalf("renamed.txt is %q %v", got, err)
	}
}

// A recursive delete of a user folder holding a link removes the link itself, never what it points to.
func TestRecursiveDeleteRemovesALinkNotItsTarget(t *testing.T) {
	bs := startBackground(t)
	userDir := filepath.Join(t.TempDir(), "userfolder")
	writeFile(t, filepath.Join(userDir, "sub", "a.txt"), "a")
	protected := t.TempDir()
	writeFile(t, filepath.Join(protected, "keep.txt"), "keep")
	plantDirLink(t, filepath.Join(userDir, "link"), protected)

	bs.ok("delete", map[string]any{"path": userDir, "recursive": true})
	if _, err := os.Lstat(userDir); !os.IsNotExist(err) {
		t.Fatalf("the folder was not deleted: %v", err)
	}
	if _, err := os.Stat(filepath.Join(protected, "keep.txt")); err != nil {
		t.Fatalf("the delete followed the link: %v", err)
	}
}

// Deleting a link itself is allowed and leaves its target alone.
func TestDeletingALinkLeavesItsTarget(t *testing.T) {
	bs := startBackground(t)
	root := t.TempDir()
	protected := t.TempDir()
	writeFile(t, filepath.Join(protected, "keep.txt"), "keep")
	link := filepath.Join(root, "link")
	plantDirLink(t, link, protected)

	bs.ok("delete", map[string]any{"path": link, "recursive": true})
	if _, err := os.Lstat(link); !os.IsNotExist(err) {
		t.Fatalf("the link was not deleted: %v", err)
	}
	if _, err := os.Stat(filepath.Join(protected, "keep.txt")); err != nil {
		t.Fatalf("the target of the link was touched: %v", err)
	}
}

// The technician listed userfolder/docs/keep.txt while docs was a folder; the user swapped docs for a link afterwards.
func TestDeleteThroughALinkOnThePathIsRefused(t *testing.T) {
	bs := startBackground(t)
	userDir := filepath.Join(t.TempDir(), "userfolder")
	if err := os.Mkdir(userDir, 0o777); err != nil {
		t.Fatal(err)
	}
	protected := t.TempDir()
	writeFile(t, filepath.Join(protected, "keep.txt"), "keep")
	plantDirLink(t, filepath.Join(userDir, "docs"), protected)

	expectRefusedAsLink(t, bs.request("delete", map[string]any{"path": filepath.Join(userDir, "docs", "keep.txt")}))
	expectRefusedAsLink(t, bs.request("delete", map[string]any{"path": filepath.Join(userDir, "docs", "keep.txt"), "recursive": true}))
	if _, err := os.Stat(filepath.Join(protected, "keep.txt")); err != nil {
		t.Fatalf("the file behind the link was deleted: %v", err)
	}
}

func TestRenameAndNewFolderThroughALinkOnThePathAreRefused(t *testing.T) {
	bs := startBackground(t)
	userDir := filepath.Join(t.TempDir(), "userfolder")
	if err := os.Mkdir(userDir, 0o777); err != nil {
		t.Fatal(err)
	}
	protected := t.TempDir()
	writeFile(t, filepath.Join(protected, "config.ini"), "x")
	plantDirLink(t, filepath.Join(userDir, "docs"), protected)

	expectRefusedAsLink(t, bs.request("rename", map[string]any{"path": filepath.Join(userDir, "docs", "config.ini"), "name": "config.old"}))
	expectRefusedAsLink(t, bs.request("mkdir", map[string]any{"path": filepath.Join(userDir, "docs"), "name": "planted"}))
	if _, err := os.Stat(filepath.Join(protected, "config.ini")); err != nil {
		t.Fatalf("the file behind the link was renamed: %v", err)
	}
	if _, err := os.Stat(filepath.Join(protected, "planted")); err == nil {
		t.Fatal("a folder was created behind the link")
	}
	// Renaming the link itself is the technician's choice and stays possible.
	bs.ok("rename", map[string]any{"path": filepath.Join(userDir, "docs"), "name": "docs-link"})
	if _, err := os.Stat(filepath.Join(protected, "config.ini")); err != nil {
		t.Fatalf("renaming the link touched its target: %v", err)
	}
}

// An upload into a folder below a link is refused: every component of the folder is checked, not only the last one.
func TestAnUploadBelowALinkOnThePathIsRefused(t *testing.T) {
	bs := startBackground(t)
	root := t.TempDir()
	protected := t.TempDir()
	if err := os.Mkdir(filepath.Join(protected, "inner"), 0o755); err != nil {
		t.Fatal(err)
	}
	plantDirLink(t, filepath.Join(root, "docs"), protected)

	expectRefusedAsLink(t, bs.request("upload", map[string]any{"path": filepath.Join(root, "docs", "inner"), "name": "a.txt", "size": 1, "offset": 0}))
	expectEmpty(t, filepath.Join(protected, "inner"))
}

// The listing shows a link as a link, with the kind of what it points to and where, so the technician can open that folder.
func TestTheListingMarksALinkAndWhereItPoints(t *testing.T) {
	bs := startBackground(t)
	root := t.TempDir()
	protected := t.TempDir()
	plantDirLink(t, filepath.Join(root, "docs"), protected)

	entries, _ := bs.ok("list", map[string]any{"path": root})["entries"].([]any)
	if len(entries) != 1 {
		t.Fatalf("entries %v", entries)
	}
	e, _ := entries[0].(map[string]any)
	if e["symlink"] != true || e["dir"] != true {
		t.Fatalf("the link is not shown as a linked folder: %v", e)
	}
	target, _ := e["target"].(string)
	want, _ := filepath.EvalSymlinks(protected)
	if got, _ := filepath.EvalSymlinks(target); target == "" || !strings.EqualFold(got, want) {
		t.Fatalf("the link points to %q, the listing says %q", protected, target)
	}
}
