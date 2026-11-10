//go:build windows

package remote

import (
	"errors"
	"os"
	"unsafe"

	"golang.org/x/sys/windows"
)

// A part file of an upload (security review of 0.3.0 step 7, hardened in the security review of 0.9.0). The agent writes it as SYSTEM into a folder the
// technician chose, which a local user may be able to write in. The folder is opened without following a link on any component of its
// path (safedir_windows.go), and the part file is created, resumed, renamed and removed relative to that handle, never by a path.

type partFile struct {
	dir  *safeDir
	part string
	file *os.File
}

func openPart(dir, partName string, resume bool) (*partFile, bool, error) {
	d, err := openSafeDir(dir)
	if err != nil {
		return nil, false, err
	}
	p := &partFile{dir: d, part: partName}
	if resume {
		if f, err := d.openPlainForWrite(partName, windows.FILE_SHARE_READ); err == nil {
			p.file = f
			return p, true, nil
		}
	}
	// Whatever has the name now is removed (a link is removed itself, never its target), then the file is created new.
	if sub, err := d.openChild(partName); err == nil {
		sub.close()
		d.close()
		return nil, false, errors.New("a folder has the name of the file being uploaded")
	}
	if err := d.remove(partName); err != nil && !errors.Is(err, os.ErrNotExist) {
		d.close()
		return nil, false, err
	}
	f, err := d.createShared(partName, windows.FILE_SHARE_READ)
	if err != nil {
		d.close()
		return nil, false, err
	}
	p.file = f
	return p, false, nil
}

// fileDispositionInfo is the FILE_INFO_BY_HANDLE_CLASS that marks a file for deletion when its last handle closes.
const fileDispositionInfo = 4

// deleteOnClose marks an open file for deletion through its handle.
func deleteOnClose(f *os.File) {
	del := uint32(1)
	_ = windows.SetFileInformationByHandle(windows.Handle(f.Fd()), fileDispositionInfo, (*byte)(unsafe.Pointer(&del)), 4)
}

// commit gives the part file its final name in the same folder, replacing a file of that name (a link is replaced itself). The part
// file is opened again relative to the folder and must still be a plain file.
func (p *partFile) commit(destName string) error {
	defer p.release()
	f, err := p.dir.openPlainForWrite(p.part, 0)
	if err != nil {
		_ = p.dir.remove(p.part)
		return err
	}
	defer f.Close()
	if err := renameHandle(windows.Handle(f.Fd()), destName, true, p.dir.child(p.part)); err != nil {
		deleteOnClose(f)
		return err
	}
	return nil
}

// discard removes the part file.
func (p *partFile) discard() {
	defer p.release()
	if p.dir != nil {
		_ = p.dir.remove(p.part)
	}
}

func (p *partFile) release() {
	p.dir.close()
	p.dir = nil
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
