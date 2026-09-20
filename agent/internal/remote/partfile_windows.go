//go:build windows

package remote

import (
	"errors"
	"os"
	"path/filepath"
	"strings"
	"unsafe"

	"golang.org/x/sys/windows"
)

// A part file of an upload (security review of 0.3.0 step 7). The agent writes it as SYSTEM into a folder the technician chose, which a
// local user may be able to write in. The folder must not be a link (junction or symbolic link), the part file is created new or opened
// without following a reparse point, and the handle must name the place the agent meant; otherwise nothing is written.

type partFile struct {
	dir  string
	part string
	file *os.File
}

func openPart(dir, partName string, resume bool) (*partFile, bool, error) {
	if err := notReparsePoint(dir); err != nil {
		return nil, false, err
	}
	path := filepath.Join(dir, partName)
	p := &partFile{dir: dir, part: partName}
	if resume {
		if f, err := openWithoutReparse(path, windows.OPEN_EXISTING); err == nil {
			if info, err := f.Stat(); err == nil && info.Mode().IsRegular() && samePath(f, path) {
				p.file = f
				return p, true, nil
			}
			_ = f.Close()
		}
	}
	// Whatever has the name now is removed (a link is removed itself, never its target), then the file is created new.
	if info, err := os.Lstat(path); err == nil {
		if info.IsDir() {
			return nil, false, errors.New("a folder has the name of the file being uploaded")
		}
		if err := os.Remove(path); err != nil {
			return nil, false, err
		}
	}
	f, err := openWithoutReparse(path, windows.CREATE_NEW)
	if err != nil {
		return nil, false, err
	}
	if !samePath(f, path) {
		// The folder was swapped for a link after the check: the new, empty file is somewhere else. It is deleted through its own handle
		// (never by a path, which the link could redirect again), and nothing is written.
		deleteOnClose(f)
		_ = f.Close()
		return nil, false, errors.New("the folder changed while the upload started; try again")
	}
	p.file = f
	return p, false, nil
}

// openWithoutReparse opens a file for writing; a reparse point is opened itself, not followed.
func openWithoutReparse(path string, disposition uint32) (*os.File, error) {
	name, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return nil, err
	}
	h, err := windows.CreateFile(name, windows.GENERIC_WRITE|windows.GENERIC_READ|windows.DELETE, windows.FILE_SHARE_READ, nil, disposition,
		windows.FILE_ATTRIBUTE_NORMAL|windows.FILE_FLAG_OPEN_REPARSE_POINT, 0)
	if err != nil {
		return nil, &os.PathError{Op: "open", Path: path, Err: err}
	}
	var info windows.ByHandleFileInformation
	if err := windows.GetFileInformationByHandle(h, &info); err != nil || info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
		info.NumberOfLinks != 1 {
		_ = windows.CloseHandle(h)
		return nil, &os.PathError{Op: "open", Path: path, Err: errors.New("not a plain file")}
	}
	return os.NewFile(uintptr(h), path), nil
}

// fileDispositionInfo is the FILE_INFO_BY_HANDLE_CLASS that marks a file for deletion when its last handle closes.
const fileDispositionInfo = 4

// deleteOnClose marks an open file for deletion through its handle.
func deleteOnClose(f *os.File) {
	del := uint32(1)
	_ = windows.SetFileInformationByHandle(windows.Handle(f.Fd()), fileDispositionInfo, (*byte)(unsafe.Pointer(&del)), 4)
}

// samePath reports whether an open file is at the path the agent meant, with every link on the way resolved.
func samePath(f *os.File, path string) bool {
	final, err := finalPath(windows.Handle(f.Fd()))
	if err != nil {
		return false
	}
	want, err := filepath.Abs(path)
	if err != nil {
		return false
	}
	return strings.EqualFold(filepath.Clean(final), filepath.Clean(want))
}

func finalPath(h windows.Handle) (string, error) {
	buf := make([]uint16, windows.MAX_LONG_PATH)
	n, err := windows.GetFinalPathNameByHandle(h, &buf[0], uint32(len(buf)), 0)
	if err != nil {
		return "", err
	}
	p := windows.UTF16ToString(buf[:n])
	if rest, ok := strings.CutPrefix(p, `\\?\UNC\`); ok {
		return `\\` + rest, nil
	}
	return strings.TrimPrefix(p, `\\?\`), nil
}

// notReparsePoint fails for a folder that is a junction or a symbolic link.
func notReparsePoint(dir string) error {
	name, err := windows.UTF16PtrFromString(dir)
	if err != nil {
		return err
	}
	attrs, err := windows.GetFileAttributes(name)
	if err != nil {
		return &os.PathError{Op: "open", Path: dir, Err: err}
	}
	if attrs&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 {
		return errors.New("the folder is a link; upload into the folder it points to")
	}
	return nil
}

func (p *partFile) commit(destName string) error {
	part := filepath.Join(p.dir, p.part)
	dest := filepath.Join(p.dir, destName)
	if err := notReparsePoint(p.dir); err != nil {
		_ = os.Remove(part)
		return err
	}
	if err := os.Rename(part, dest); err != nil {
		_ = os.Remove(part)
		return err
	}
	return nil
}

func (p *partFile) discard() {
	_ = os.Remove(filepath.Join(p.dir, p.part))
}

// openNoFollow opens a file for reading only when it is a plain file (not a reparse point): for paths that come from the user of a session.
func openNoFollow(path string) (*os.File, error) {
	name, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return nil, err
	}
	h, err := windows.CreateFile(name, windows.GENERIC_READ, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE|windows.FILE_SHARE_DELETE, nil,
		windows.OPEN_EXISTING, windows.FILE_ATTRIBUTE_NORMAL|windows.FILE_FLAG_OPEN_REPARSE_POINT, 0)
	if err != nil {
		return nil, &os.PathError{Op: "open", Path: path, Err: err}
	}
	var info windows.ByHandleFileInformation
	if err := windows.GetFileInformationByHandle(h, &info); err != nil ||
		info.FileAttributes&(windows.FILE_ATTRIBUTE_REPARSE_POINT|windows.FILE_ATTRIBUTE_DIRECTORY) != 0 {
		_ = windows.CloseHandle(h)
		return nil, &os.PathError{Op: "open", Path: path, Err: errors.New("not a plain file")}
	}
	return os.NewFile(uintptr(h), path), nil
}
