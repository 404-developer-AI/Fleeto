//go:build !windows

package remote

import (
	"errors"
	"os"
	"path/filepath"
	"syscall"

	"golang.org/x/sys/unix"
)

// A part file of an upload (security review of 0.3.0 step 7). The agent writes it as root into a folder the technician chose, which a
// local user may own: the folder is opened without following a link on any component of its path (safedir_unix.go) and every name is
// opened relative to it, never following a link, so a user who plants a link named like the part file, the destination or a folder on
// the way cannot make root write, truncate or replace another file.

type partFile struct {
	dir  *safeDir
	part string
	file *os.File
}

// openPart creates the part file, or opens it to resume when it is a plain file of this service; anything else by that name is removed
// first (the link itself, never its target).
func openPart(dir, partName string, resume bool) (*partFile, bool, error) {
	d, err := openSafeDir(dir)
	if err != nil {
		return nil, false, err
	}
	fd := d.fd
	p := &partFile{dir: d, part: partName}
	if resume {
		if f, err := p.openExisting(); err == nil {
			p.file = f
			return p, true, nil
		}
	}
	if err := unix.Unlinkat(fd, partName, 0); err != nil && !errors.Is(err, unix.ENOENT) {
		d.close()
		return nil, false, &os.PathError{Op: "remove", Path: filepath.Join(dir, partName), Err: err}
	}
	file, err := unix.Openat(fd, partName, unix.O_WRONLY|unix.O_CREAT|unix.O_EXCL|unix.O_NOFOLLOW|unix.O_CLOEXEC, 0o600)
	if err != nil {
		d.close()
		return nil, false, &os.PathError{Op: "create", Path: filepath.Join(dir, partName), Err: err}
	}
	p.file = os.NewFile(uintptr(file), filepath.Join(dir, partName))
	return p, false, nil
}

// openExisting opens an earlier part file to resume it: a regular file with one link, owned by this service.
func (p *partFile) openExisting() (*os.File, error) {
	fd, err := unix.Openat(p.dir.fd, p.part, unix.O_WRONLY|unix.O_NOFOLLOW|unix.O_CLOEXEC|unix.O_NONBLOCK, 0)
	if err != nil {
		return nil, err
	}
	var st unix.Stat_t
	if err := unix.Fstat(fd, &st); err != nil || st.Mode&unix.S_IFMT != unix.S_IFREG || st.Nlink != 1 || int(st.Uid) != os.Geteuid() {
		_ = unix.Close(fd)
		return nil, errors.New("not a part file of this service")
	}
	return os.NewFile(uintptr(fd), p.part), nil
}

// commit gives the part file its final name in the same folder.
func (p *partFile) commit(destName string) error {
	defer p.release()
	if err := unix.Renameat(p.dir.fd, p.part, p.dir.fd, destName); err != nil {
		_ = unix.Unlinkat(p.dir.fd, p.part, 0)
		return &os.LinkError{Op: "rename", Old: p.part, New: destName, Err: err}
	}
	return nil
}

// discard removes the part file.
func (p *partFile) discard() {
	defer p.release()
	_ = unix.Unlinkat(p.dir.fd, p.part, 0)
}

func (p *partFile) release() {
	p.dir.close()
}

// openNoFollow opens a file for reading without following a final link and without blocking on a named pipe, and only when it is a
// regular file: for paths that come from the user of a session (files copied on the endpoint).
func openNoFollow(path string) (*os.File, error) {
	fd, err := unix.Open(path, unix.O_RDONLY|unix.O_NOFOLLOW|unix.O_NONBLOCK|unix.O_CLOEXEC, 0)
	if err != nil {
		return nil, &os.PathError{Op: "open", Path: path, Err: err}
	}
	var st unix.Stat_t
	if err := unix.Fstat(fd, &st); err != nil || st.Mode&unix.S_IFMT != unix.S_IFREG {
		_ = unix.Close(fd)
		return nil, &os.PathError{Op: "open", Path: path, Err: syscall.EINVAL}
	}
	if err := unix.SetNonblock(fd, false); err != nil {
		_ = unix.Close(fd)
		return nil, err
	}
	return os.NewFile(uintptr(fd), path), nil
}
