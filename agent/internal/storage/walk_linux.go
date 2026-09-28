//go:build linux

package storage

import (
	"os"
	"time"

	"golang.org/x/sys/unix"
)

// linuxLister returns a lister that stays on the device of the mount point: other mounts (including /proc, /sys and /run) are
// never entered, symbolic links are never followed, and sizes are the blocks a file takes on disk.
func linuxLister(root string) (lister, error) {
	var st unix.Stat_t
	if err := unix.Lstat(root, &st); err != nil {
		return nil, err
	}
	dev := st.Dev
	return func(path string) ([]entry, error) {
		fd, err := unix.Open(path, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
		if err != nil {
			return nil, err
		}
		f := os.NewFile(uintptr(fd), path)
		defer f.Close()
		names, err := f.Readdirnames(-1)
		if err != nil && len(names) == 0 {
			return nil, err
		}
		out := make([]entry, 0, len(names))
		for _, name := range names {
			var st unix.Stat_t
			if err := unix.Fstatat(fd, name, &st, unix.AT_SYMLINK_NOFOLLOW); err != nil {
				continue
			}
			e := entry{name: name, size: uint64(st.Blocks) * 512, modified: time.Unix(st.Mtim.Unix())}
			// A symbolic link is counted as the file it is; its target is not.
			if st.Mode&unix.S_IFMT == unix.S_IFDIR {
				e.dir = true
				e.skip = st.Dev != dev
			}
			out = append(out, e)
		}
		return out, nil
	}, nil
}

// platformLister is the lister of a walk of this volume.
func platformLister(root string) (lister, error) { return linuxLister(root) }
