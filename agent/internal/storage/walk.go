package storage

import (
	"context"
	"time"
)

// entry is one item of a folder as the platform lister reports it.
type entry struct {
	name string
	dir  bool
	// skip is a folder the walk must not enter: a link, junction, mount point or another volume.
	skip     bool
	size     uint64
	modified time.Time
}

// lister reads the items of one folder.
type lister func(path string) ([]entry, error)

// errUnreadable is the note of a walk that skipped folders it could not read.
const errUnreadable = "some folders could not be read"

// walk reads the tree below the root folder into t, depth first without recursion. A folder that cannot be read is skipped.
func walk(ctx context.Context, t *tree, list lister) (incomplete string, err error) {
	type item struct {
		index int32
		path  string
	}
	stack := []item{{0, t.root}}
	unreadable := false
	for len(stack) > 0 {
		if err := ctx.Err(); err != nil {
			return "", err
		}
		cur := stack[len(stack)-1]
		stack = stack[:len(stack)-1]
		entries, err := list(cur.path)
		if err != nil {
			unreadable = true
			continue
		}
		for _, e := range entries {
			switch {
			case e.skip:
				continue
			case e.dir:
				i := t.addDir(cur.index, e.name)
				t.dirs[i].size += e.size
				stack = append(stack, item{i, join(cur.path, e.name, t.separator)})
			default:
				t.addFile(cur.index, e.name, e.size, e.modified)
			}
		}
	}
	if unreadable {
		return errUnreadable, nil
	}
	return "", nil
}
