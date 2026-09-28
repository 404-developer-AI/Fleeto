package storage

import (
	"context"
	"fmt"
	"os"
	"path/filepath"
	"runtime"
	"strings"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

func TestSelectionKeepsTheLargestFoldersWithTheirParents(t *testing.T) {
	tr := newTree("/", "/")
	// 40 top folders, each with 20 subfolders holding one file: 840 folders, more than a report lists.
	for i := range 40 {
		top := tr.addDir(0, fmt.Sprintf("top%02d", i))
		for j := range 20 {
			sub := tr.addDir(top, fmt.Sprintf("sub%02d", j))
			tr.addFile(sub, "f", uint64(1000*(i+1)+j), time.Time{})
		}
	}
	res := tr.result()
	if len(res.Folders) != MaxFolders {
		t.Fatalf("folders = %d", len(res.Folders))
	}
	if res.Folders[0].Path != "/" || res.FolderCount != 840 || res.FileCount != 800 {
		t.Fatalf("root = %+v, counts %d %d", res.Folders[0], res.FolderCount, res.FileCount)
	}
	listed := map[string]bool{}
	for i, f := range res.Folders {
		if i > 0 && f.SizeBytes > res.Folders[i-1].SizeBytes {
			t.Fatalf("not largest first at %d", i)
		}
		if f.Path != "/" {
			parent := filepath.ToSlash(filepath.Dir(f.Path))
			if !listed[parent] {
				t.Fatalf("%s is listed before or without its parent %s", f.Path, parent)
			}
		}
		listed[f.Path] = true
	}
	if !listed["/top39/sub19"] || listed["/top00/sub00"] {
		t.Fatal("the largest folders must be kept and the smallest dropped")
	}
}

func TestTiesListTheShallowerFolderFirst(t *testing.T) {
	tr := newTree("/", "/")
	a := tr.addDir(0, "a")
	b := tr.addDir(a, "b")
	tr.addFile(b, "only", 100, time.Time{})
	res := tr.result()
	got := []string{}
	for _, f := range res.Folders {
		got = append(got, f.Path)
	}
	if strings.Join(got, ",") != "/,/a,/a/b" {
		t.Fatalf("order = %v", got)
	}
}

func TestLargestFilesAreKeptWithFullPaths(t *testing.T) {
	tr := newTree(`D:\`, `\`)
	data := tr.addDir(0, "Data")
	for i := range 500 {
		tr.addFile(data, fmt.Sprintf("file%03d.bak", i), uint64(i), time.Time{})
	}
	res := tr.result()
	if len(res.Files) != MaxFiles {
		t.Fatalf("files = %d", len(res.Files))
	}
	if res.Files[0].Path != `D:\Data\file499.bak` || res.Files[0].SizeBytes != 499 || res.Files[MaxFiles-1].SizeBytes != 450 {
		t.Fatalf("files = %+v ... %+v", res.Files[0], res.Files[MaxFiles-1])
	}
}

func TestOrphansAndCyclesHangBelowTheRoot(t *testing.T) {
	tr := newTree("/", "/")
	x := tr.addDir(0, "x")
	y := tr.addDir(x, "y")
	tr.dirs[x].parent = y // a cycle
	tr.addFile(y, "f", 10, time.Time{})
	z := tr.addDir(0, "z")
	tr.dirs[z].parent = 999 // a parent that does not exist
	tr.addFile(z, "g", 5, time.Time{})
	res := tr.result()
	if res.Folders[0].SizeBytes != 15 {
		t.Fatalf("everything still counts on the root: %+v", res.Folders[0])
	}
}

func TestCleanPathKeepsPathsValidAndShort(t *testing.T) {
	if got := cleanPath("a\xffb"); got != "a\uFFFDb" {
		t.Fatalf("invalid UTF-8: %q", got)
	}
	long := strings.Repeat("é", MaxPathLength) // two bytes each
	got := cleanPath(long)
	if len(got) > MaxPathLength || !strings.HasPrefix(long, got) {
		t.Fatalf("length %d", len(got))
	}
}

func TestFitShrinksAReportUnderTheLimit(t *testing.T) {
	r := &agentv1.StorageScanReport{ScanId: newUUID()}
	for i := range MaxFolders {
		r.Folders = append(r.Folders, &agentv1.StorageFolder{Path: strings.Repeat("x", 1000) + fmt.Sprint(i), SizeBytes: 1})
	}
	for i := range MaxFiles {
		r.Files = append(r.Files, &agentv1.StorageFile{Path: strings.Repeat("y", 1000) + fmt.Sprint(i)})
	}
	Fit(r, 100_000)
	if proto.Size(r) > 100_000 || len(r.Files) != 0 || len(r.Folders) == 0 {
		t.Fatalf("size %d, files %d, folders %d", proto.Size(r), len(r.Files), len(r.Folders))
	}
	if !scanIDPattern.MatchString(newUUID()) {
		t.Fatal("scan ids must match the spool pattern")
	}
}

func TestWalkReadsAFolderTree(t *testing.T) {
	root := t.TempDir()
	must := func(err error) {
		t.Helper()
		if err != nil {
			t.Fatal(err)
		}
	}
	must(os.MkdirAll(filepath.Join(root, "logs", "old"), 0o755))
	must(os.WriteFile(filepath.Join(root, "logs", "old", "a.log"), make([]byte, 200_000), 0o644))
	must(os.WriteFile(filepath.Join(root, "logs", "b.log"), make([]byte, 10_000), 0o644))
	must(os.WriteFile(filepath.Join(root, "top.txt"), []byte("x"), 0o644))
	if runtime.GOOS != "windows" {
		must(os.Symlink(filepath.Join(root, "logs"), filepath.Join(root, "link")))
	}
	sep := string(os.PathSeparator)
	tr := newTree(root, sep)
	lister, err := platformLister(root)
	must(err)
	incomplete, err := walk(context.Background(), tr, lister)
	must(err)
	if incomplete != "" {
		t.Fatalf("incomplete: %s", incomplete)
	}
	res := tr.result()
	if res.FileCount < 3 || res.FolderCount != 2 {
		t.Fatalf("counts: files %d folders %d (a link must not be entered)", res.FileCount, res.FolderCount)
	}
	byPath := map[string]Folder{}
	for _, f := range res.Folders {
		byPath[f.Path] = f
	}
	old := byPath[filepath.Join(root, "logs", "old")]
	if old.SizeBytes < 200_000 || old.FileCount != 1 {
		t.Fatalf("old = %+v", old)
	}
	if res.Files[0].Path != filepath.Join(root, "logs", "old", "a.log") || res.Files[0].ModifiedAt.IsZero() {
		t.Fatalf("largest file = %+v", res.Files[0])
	}
}

func TestWalkStopsOnTheDeadline(t *testing.T) {
	tr := newTree("/", "/")
	ctx, cancel := context.WithCancel(context.Background())
	cancel()
	_, err := walk(ctx, tr, func(string) ([]entry, error) { return nil, nil })
	if err == nil {
		t.Fatal("a cancelled walk must return the context error")
	}
}

func TestWalkNotesUnreadableFolders(t *testing.T) {
	tr := newTree("/", "/")
	list := func(path string) ([]entry, error) {
		if path == "/" {
			return []entry{{name: "locked", dir: true}, {name: "ok", dir: true}, {name: "mnt", dir: true, skip: true}}, nil
		}
		if path == "/locked" {
			return nil, os.ErrPermission
		}
		return []entry{{name: "f", size: 7}}, nil
	}
	incomplete, err := walk(context.Background(), tr, list)
	if err != nil || incomplete != errUnreadable {
		t.Fatalf("incomplete = %q, err = %v", incomplete, err)
	}
	if res := tr.result(); res.FolderCount != 2 || res.Folders[0].SizeBytes != 7 {
		t.Fatalf("result = %+v", res)
	}
}
