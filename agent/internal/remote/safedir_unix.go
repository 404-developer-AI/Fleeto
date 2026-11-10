//go:build !windows

package remote

import (
	"errors"
	"os"
	"path/filepath"
	"strings"

	"golang.org/x/sys/unix"
)

// safeDir is a folder opened without following a link on the way (safedir.go). Every component is opened with openat relative to the
// folder before it and O_NOFOLLOW, so a symbolic link is refused, never followed.
type safeDir struct {
	fd   int
	path string
}

func (d *safeDir) close() {
	if d != nil && d.fd >= 0 {
		_ = unix.Close(d.fd)
		d.fd = -1
	}
}

func (d *safeDir) child(name string) string { return filepath.Join(d.path, name) }

// openSafeDir opens an absolute, cleaned folder path one component at a time from the root.
func openSafeDir(path string) (*safeDir, error) {
	fd, err := unix.Open("/", unix.O_RDONLY|unix.O_DIRECTORY|unix.O_CLOEXEC, 0)
	if err != nil {
		return nil, &os.PathError{Op: "open", Path: "/", Err: err}
	}
	d := &safeDir{fd: fd, path: "/"}
	for _, part := range strings.Split(path, "/") {
		if part == "" {
			continue
		}
		next, err := d.openChild(part)
		d.close()
		if err != nil {
			return nil, err
		}
		d = next
	}
	return d, nil
}

// isLinkAt reports whether a name in the folder is a symbolic link.
func (d *safeDir) isLinkAt(name string) bool {
	var st unix.Stat_t
	return unix.Fstatat(d.fd, name, &st, unix.AT_SYMLINK_NOFOLLOW) == nil && st.Mode&unix.S_IFMT == unix.S_IFLNK
}

// openChild opens a folder in this folder; a link gives a *linkError, a file errNotDir.
func (d *safeDir) openChild(name string) (*safeDir, error) {
	fd, err := unix.Openat(d.fd, name, unix.O_RDONLY|unix.O_DIRECTORY|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0)
	if err != nil {
		if errors.Is(err, unix.ELOOP) || (errors.Is(err, unix.ENOTDIR) && d.isLinkAt(name)) {
			return nil, &linkError{path: d.child(name)}
		}
		if errors.Is(err, unix.ENOTDIR) {
			return nil, errNotDir
		}
		return nil, &os.PathError{Op: "open", Path: d.child(name), Err: err}
	}
	return &safeDir{fd: fd, path: d.child(name)}, nil
}

// names lists the folder.
func (d *safeDir) names() ([]string, error) {
	fd, err := unix.Dup(d.fd)
	if err != nil {
		return nil, err
	}
	f := os.NewFile(uintptr(fd), d.path)
	defer f.Close()
	return f.Readdirnames(-1)
}

// mkdir creates a new folder in this folder and returns it opened.
func (d *safeDir) mkdir(name string) (*safeDir, error) {
	if err := unix.Mkdirat(d.fd, name, 0o755); err != nil {
		return nil, &os.PathError{Op: "mkdir", Path: d.child(name), Err: err}
	}
	return d.openChild(name)
}

// create creates a new file in this folder; an existing name, also a link, fails.
func (d *safeDir) create(name string, perm os.FileMode) (*os.File, error) {
	fd, err := unix.Openat(d.fd, name, unix.O_WRONLY|unix.O_CREAT|unix.O_EXCL|unix.O_NOFOLLOW|unix.O_CLOEXEC, uint32(perm.Perm()))
	if err != nil {
		return nil, &os.PathError{Op: "create", Path: d.child(name), Err: err}
	}
	return os.NewFile(uintptr(fd), d.child(name)), nil
}

// openRead opens a regular file in this folder for reading without following a link or blocking on a named pipe.
func (d *safeDir) openRead(name string) (*os.File, error) {
	fd, err := unix.Openat(d.fd, name, unix.O_RDONLY|unix.O_NOFOLLOW|unix.O_NONBLOCK|unix.O_CLOEXEC, 0)
	if err != nil {
		if errors.Is(err, unix.ELOOP) {
			return nil, &linkError{path: d.child(name)}
		}
		return nil, &os.PathError{Op: "open", Path: d.child(name), Err: err}
	}
	var st unix.Stat_t
	if err := unix.Fstat(fd, &st); err != nil || st.Mode&unix.S_IFMT != unix.S_IFREG {
		_ = unix.Close(fd)
		return nil, errNotPlain
	}
	if err := unix.SetNonblock(fd, false); err != nil {
		_ = unix.Close(fd)
		return nil, err
	}
	return os.NewFile(uintptr(fd), d.child(name)), nil
}

// discardCreated deletes a file this folder created and closes it.
func (d *safeDir) discardCreated(f *os.File, name string) {
	_ = f.Close()
	_ = unix.Unlinkat(d.fd, name, 0)
}

// remove deletes a file, an empty folder or a link (the link itself) in this folder.
func (d *safeDir) remove(name string) error {
	var st unix.Stat_t
	if err := unix.Fstatat(d.fd, name, &st, unix.AT_SYMLINK_NOFOLLOW); err != nil {
		return &os.PathError{Op: "remove", Path: d.child(name), Err: err}
	}
	flags := 0
	if st.Mode&unix.S_IFMT == unix.S_IFDIR {
		flags = unix.AT_REMOVEDIR
	}
	if err := unix.Unlinkat(d.fd, name, flags); err != nil {
		return &os.PathError{Op: "remove", Path: d.child(name), Err: err}
	}
	return nil
}

// rename gives an item of this folder (a link is renamed itself) a new name in the same folder.
func (d *safeDir) rename(oldName, newName string, replace bool) error {
	if !replace {
		var st unix.Stat_t
		if err := unix.Fstatat(d.fd, newName, &st, unix.AT_SYMLINK_NOFOLLOW); err == nil {
			return &os.LinkError{Op: "rename", Old: d.child(oldName), New: newName, Err: os.ErrExist}
		}
	}
	if err := unix.Renameat(d.fd, oldName, d.fd, newName); err != nil {
		return &os.LinkError{Op: "rename", Old: d.child(oldName), New: newName, Err: err}
	}
	return nil
}

// entryIsLink reports whether a listed item is a symbolic link.
func entryIsLink(_ string, mode os.FileMode) bool { return mode&os.ModeSymlink != 0 }
