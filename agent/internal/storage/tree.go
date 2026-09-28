// Package storage scans the fixed volumes of the endpoint for storage analysis (0.6.0): which folders and files take the
// space. A scan aggregates the whole tree in memory, one entry per folder and none per file, and reports only the largest
// folders and files, so the report stays small whatever the size of the volume.
package storage

import (
	"container/heap"
	"sort"
	"strings"
	"time"
	"unicode/utf8"
)

const (
	// MaxFolders is the number of folders one report lists.
	MaxFolders = 300
	// MaxFiles is the number of files one report lists.
	MaxFiles = 50
	// MaxPathLength bounds a reported path, in bytes.
	MaxPathLength = 1024
	// fileCandidates is how many files the scan keeps before the ones that are never listed (NTFS metadata) are left out.
	fileCandidates = MaxFiles * 2
)

// Folder is one folder of a report: its path and everything below it.
type Folder struct {
	Path        string
	SizeBytes   uint64
	FileCount   uint64
	FolderCount uint64
}

// File is one file of a report.
type File struct {
	Path       string
	SizeBytes  uint64
	ModifiedAt time.Time
}

// Result is what a scan of one volume found.
type Result struct {
	Folders     []Folder
	Files       []File
	FileCount   uint64
	FolderCount uint64
}

// dir is one folder while the tree is built. Sizes are its own until rollUp adds its subfolders.
type dir struct {
	parent  int32
	name    string
	size    uint64
	files   uint64
	folders uint64
	// hidden folders are counted in their parent but never listed (NTFS metadata such as $Extend).
	hidden bool
}

// fileEntry is a candidate for the largest files.
type fileEntry struct {
	size     uint64
	parent   int32
	name     string
	modified time.Time
}

type fileHeap []fileEntry

func (h fileHeap) Len() int           { return len(h) }
func (h fileHeap) Less(i, j int) bool { return h[i].size < h[j].size }
func (h fileHeap) Swap(i, j int)      { h[i], h[j] = h[j], h[i] }
func (h *fileHeap) Push(x any)        { *h = append(*h, x.(fileEntry)) }
func (h *fileHeap) Pop() any {
	old := *h
	n := len(old)
	x := old[n-1]
	*h = old[:n-1]
	return x
}

// tree collects folders and the largest files of one volume. Index 0 is the volume root.
type tree struct {
	root      string
	separator string
	dirs      []dir
	files     fileHeap
	keepFiles int
}

func newTree(root, separator string) *tree {
	return &tree{root: root, separator: separator, dirs: []dir{{parent: -1}}, keepFiles: fileCandidates}
}

// addDir adds a folder below parent and returns its index.
func (t *tree) addDir(parent int32, name string) int32 {
	t.dirs = append(t.dirs, dir{parent: parent, name: name})
	return int32(len(t.dirs) - 1)
}

// addFile counts a file in its folder and keeps it when it is among the largest.
func (t *tree) addFile(parent int32, name string, size uint64, modified time.Time) {
	d := &t.dirs[parent]
	d.size += size
	d.files++
	if len(t.files) < t.keepFiles {
		heap.Push(&t.files, fileEntry{size: size, parent: parent, name: name, modified: modified})
		return
	}
	if size > t.files[0].size {
		t.files[0] = fileEntry{size: size, parent: parent, name: name, modified: modified}
		heap.Fix(&t.files, 0)
	}
}

// fixParents attaches every folder whose parent chain does not reach the root (a missing parent or a cycle) to the root, and
// returns the depth of every folder.
func (t *tree) fixParents() []int32 {
	const unknown, visiting = -1, -2
	depth := make([]int32, len(t.dirs))
	for i := range depth {
		depth[i] = unknown
	}
	depth[0] = 0
	t.dirs[0].parent = -1
	var chain []int32
	for i := range t.dirs {
		if depth[i] != unknown {
			continue
		}
		chain = chain[:0]
		cur := int32(i)
		for depth[cur] == unknown {
			depth[cur] = visiting
			chain = append(chain, cur)
			p := t.dirs[cur].parent
			if p < 0 || int(p) >= len(t.dirs) || depth[p] == visiting {
				// Orphan or cycle: the folder hangs below the root.
				t.dirs[cur].parent = 0
				p = 0
			}
			cur = p
		}
		base := depth[cur]
		for k := len(chain) - 1; k >= 0; k-- {
			base++
			depth[chain[k]] = base
		}
	}
	return depth
}

// result rolls sizes up to the root and selects the largest folders and files.
func (t *tree) result() Result {
	depth := t.fixParents()
	order := make([]int32, len(t.dirs))
	for i := range order {
		order[i] = int32(i)
	}
	// Deepest first, so a folder is complete before it is added to its parent.
	sort.Slice(order, func(a, b int) bool { return depth[order[a]] > depth[order[b]] })
	for _, i := range order {
		if i == 0 {
			continue
		}
		d := t.dirs[i]
		p := &t.dirs[d.parent]
		p.size += d.size
		p.files += d.files
		p.folders += d.folders + 1
	}
	// A hidden folder still counts in its parent; it and everything below it is left out of the lists.
	hidden := t.hiddenMask()

	candidates := make([]int32, 0, len(t.dirs))
	for i := range t.dirs {
		if !hidden[i] {
			candidates = append(candidates, int32(i))
		}
	}
	// Largest first; a folder is never smaller than a folder in it, and on a tie the shallower one comes first, so the parent
	// of every listed folder is listed before it.
	sort.Slice(candidates, func(a, b int) bool {
		x, y := candidates[a], candidates[b]
		if t.dirs[x].size != t.dirs[y].size {
			return t.dirs[x].size > t.dirs[y].size
		}
		if depth[x] != depth[y] {
			return depth[x] < depth[y]
		}
		return x < y
	})
	if len(candidates) > MaxFolders {
		candidates = candidates[:MaxFolders]
	}
	paths := map[int32]string{}
	res := Result{FileCount: t.dirs[0].files, FolderCount: t.dirs[0].folders}
	for _, i := range candidates {
		d := t.dirs[i]
		res.Folders = append(res.Folders, Folder{Path: t.path(i, paths), SizeBytes: d.size, FileCount: d.files, FolderCount: d.folders})
	}

	files := make([]fileEntry, len(t.files))
	copy(files, t.files)
	sort.Slice(files, func(a, b int) bool {
		if files[a].size != files[b].size {
			return files[a].size > files[b].size
		}
		return files[a].name < files[b].name
	})
	for _, f := range files {
		if len(res.Files) == MaxFiles {
			break
		}
		if hidden[f.parent] {
			continue
		}
		res.Files = append(res.Files, File{Path: cleanPath(join(t.path(f.parent, paths), f.name, t.separator)), SizeBytes: f.size, ModifiedAt: f.modified})
	}
	return res
}

// hiddenMask marks hidden folders and everything below them.
func (t *tree) hiddenMask() []bool {
	hidden := make([]bool, len(t.dirs))
	state := make([]int8, len(t.dirs)) // 0 unknown, 1 known
	state[0] = 1
	hidden[0] = t.dirs[0].hidden
	var chain []int32
	for i := range t.dirs {
		if state[i] == 1 {
			continue
		}
		chain = chain[:0]
		cur := int32(i)
		for state[cur] == 0 {
			chain = append(chain, cur)
			cur = t.dirs[cur].parent
		}
		h := hidden[cur]
		for k := len(chain) - 1; k >= 0; k-- {
			c := chain[k]
			h = h || t.dirs[c].hidden
			hidden[c] = h
			state[c] = 1
		}
	}
	return hidden
}

// path builds the full path of a folder, remembering the ones it built.
func (t *tree) path(i int32, cache map[int32]string) string {
	if p, ok := cache[i]; ok {
		return p
	}
	var p string
	if i == 0 {
		p = t.root
	} else {
		p = join(t.path(t.dirs[i].parent, cache), t.dirs[i].name, t.separator)
	}
	cache[i] = cleanPath(p)
	return cache[i]
}

func join(parent, name, separator string) string {
	if strings.HasSuffix(parent, separator) {
		return parent + name
	}
	return parent + separator + name
}

// cleanPath makes a path valid UTF-8 and at most MaxPathLength bytes, without cutting a character in half.
func cleanPath(p string) string {
	p = strings.ToValidUTF8(p, "�")
	if len(p) <= MaxPathLength {
		return p
	}
	cut := MaxPathLength
	for cut > 0 && !utf8.RuneStart(p[cut]) {
		cut--
	}
	return p[:cut]
}
