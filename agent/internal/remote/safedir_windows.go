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

// safeDir is a folder opened without following a link on the way (safedir.go). Every component is opened with NtCreateFile relative to
// the handle of the folder before it and with FILE_OPEN_REPARSE_POINT, so a junction or symbolic link is opened itself and refused,
// never followed.
type safeDir struct {
	f    *os.File
	path string
}

const (
	// reparseTagNameSurrogate marks a reparse point that names another place: junctions, mount points and symbolic links. Other reparse
	// points (OneDrive placeholders, deduplicated files) stay where they are and are opened normally.
	reparseTagNameSurrogate = 0x20000000
	shareAll                = windows.FILE_SHARE_READ | windows.FILE_SHARE_WRITE | windows.FILE_SHARE_DELETE
	dirAccess               = windows.FILE_LIST_DIRECTORY | windows.FILE_TRAVERSE | windows.FILE_READ_ATTRIBUTES | windows.SYNCHRONIZE
)

func (d *safeDir) handle() windows.Handle { return windows.Handle(d.f.Fd()) }

func (d *safeDir) close() {
	if d != nil && d.f != nil {
		_ = d.f.Close()
		d.f = nil
	}
}

func (d *safeDir) child(name string) string { return filepath.Join(d.path, name) }

// openSafeDir opens an absolute, cleaned folder path one component at a time from the root of its volume.
func openSafeDir(path string) (*safeDir, error) {
	vol := filepath.VolumeName(path)
	if vol == "" {
		return nil, errors.New("the path must start with a drive or a network share")
	}
	root := vol + `\`
	name, err := windows.UTF16PtrFromString(root)
	if err != nil {
		return nil, err
	}
	h, err := windows.CreateFile(name, dirAccess, shareAll, nil, windows.OPEN_EXISTING, windows.FILE_FLAG_BACKUP_SEMANTICS, 0)
	if err != nil {
		return nil, &os.PathError{Op: "open", Path: root, Err: err}
	}
	d := &safeDir{f: os.NewFile(uintptr(h), root), path: root}
	for _, part := range strings.Split(path[len(vol):], `\`) {
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

// ntCreate calls NtCreateFile for a name relative to the folder.
func (d *safeDir) ntCreate(name string, access, share, disposition, options uint32) (windows.Handle, error) {
	uname, err := windows.NewNTUnicodeString(name)
	if err != nil {
		return 0, &os.PathError{Op: "open", Path: d.child(name), Err: err}
	}
	oa := windows.OBJECT_ATTRIBUTES{RootDirectory: d.handle(), ObjectName: uname, Attributes: windows.OBJ_CASE_INSENSITIVE}
	oa.Length = uint32(unsafe.Sizeof(oa))
	var h windows.Handle
	var iosb windows.IO_STATUS_BLOCK
	err = windows.NtCreateFile(&h, access|windows.FILE_READ_ATTRIBUTES|windows.SYNCHRONIZE, &oa, &iosb, nil, windows.FILE_ATTRIBUTE_NORMAL,
		share, disposition, options|windows.FILE_SYNCHRONOUS_IO_NONALERT|windows.FILE_OPEN_FOR_BACKUP_INTENT, 0, 0)
	if err != nil {
		return 0, &os.PathError{Op: "open", Path: d.child(name), Err: ntErrno(err)}
	}
	return h, nil
}

func ntErrno(err error) error {
	if s, ok := err.(windows.NTStatus); ok {
		return s.Errno()
	}
	return err
}

type attributeTagInfo struct {
	FileAttributes uint32
	ReparseTag     uint32
}

func attributeTag(h windows.Handle) (attributeTagInfo, error) {
	var info attributeTagInfo
	err := windows.GetFileInformationByHandleEx(h, windows.FileAttributeTagInfo, (*byte)(unsafe.Pointer(&info)), uint32(unsafe.Sizeof(info)))
	return info, err
}

func sameFile(a, b windows.Handle) bool {
	var ia, ib windows.ByHandleFileInformation
	if windows.GetFileInformationByHandle(a, &ia) != nil || windows.GetFileInformationByHandle(b, &ib) != nil {
		return false
	}
	return ia.VolumeSerialNumber == ib.VolumeSerialNumber && ia.FileIndexHigh == ib.FileIndexHigh && ia.FileIndexLow == ib.FileIndexLow
}

// openNoLink opens a name in the folder without following a link: a junction or symbolic link is refused. A reparse point that is not a
// link is opened again normally (so its own driver serves it) and must still be the same file.
func (d *safeDir) openNoLink(name string, access, share, disposition, options uint32) (windows.Handle, uint32, error) {
	h, err := d.ntCreate(name, access, share, disposition, options|windows.FILE_OPEN_REPARSE_POINT)
	if err != nil {
		return 0, 0, err
	}
	info, err := attributeTag(h)
	if err != nil {
		_ = windows.CloseHandle(h)
		return 0, 0, &os.PathError{Op: "open", Path: d.child(name), Err: err}
	}
	if info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 {
		if info.ReparseTag&reparseTagNameSurrogate != 0 {
			_ = windows.CloseHandle(h)
			return 0, 0, &linkError{path: d.child(name)}
		}
		again, err := d.ntCreate(name, access, share, windows.FILE_OPEN, options)
		if err != nil {
			_ = windows.CloseHandle(h)
			return 0, 0, err
		}
		same := sameFile(h, again)
		_ = windows.CloseHandle(h)
		if !same {
			_ = windows.CloseHandle(again)
			return 0, 0, &linkError{path: d.child(name)}
		}
		h = again
	}
	return h, info.FileAttributes, nil
}

// openChild opens a folder in this folder; a link gives a *linkError, a file errNotDir.
func (d *safeDir) openChild(name string) (*safeDir, error) {
	h, attrs, err := d.openNoLink(name, dirAccess, shareAll, windows.FILE_OPEN, 0)
	if err != nil {
		return nil, err
	}
	if attrs&windows.FILE_ATTRIBUTE_DIRECTORY == 0 {
		_ = windows.CloseHandle(h)
		return nil, errNotDir
	}
	return &safeDir{f: os.NewFile(uintptr(h), d.child(name)), path: d.child(name)}, nil
}

// names lists the folder.
func (d *safeDir) names() ([]string, error) {
	entries, err := d.f.ReadDir(-1)
	if err != nil {
		return nil, err
	}
	names := make([]string, len(entries))
	for i, e := range entries {
		names[i] = e.Name()
	}
	return names, nil
}

// mkdir creates a new folder in this folder and returns it opened.
func (d *safeDir) mkdir(name string) (*safeDir, error) {
	h, err := d.ntCreate(name, dirAccess, shareAll, windows.FILE_CREATE, windows.FILE_DIRECTORY_FILE|windows.FILE_OPEN_REPARSE_POINT)
	if err != nil {
		return nil, err
	}
	return &safeDir{f: os.NewFile(uintptr(h), d.child(name)), path: d.child(name)}, nil
}

// create creates a new file in this folder; an existing name, also a link, fails.
func (d *safeDir) create(name string, _ os.FileMode) (*os.File, error) {
	return d.createShared(name, shareAll)
}

func (d *safeDir) createShared(name string, share uint32) (*os.File, error) {
	h, err := d.ntCreate(name, windows.GENERIC_READ|windows.GENERIC_WRITE|windows.DELETE, share, windows.FILE_CREATE,
		windows.FILE_NON_DIRECTORY_FILE|windows.FILE_OPEN_REPARSE_POINT)
	if err != nil {
		return nil, err
	}
	return os.NewFile(uintptr(h), d.child(name)), nil
}

// openRead opens a regular file in this folder for reading; a link gives a *linkError.
func (d *safeDir) openRead(name string) (*os.File, error) {
	h, attrs, err := d.openNoLink(name, windows.GENERIC_READ, shareAll, windows.FILE_OPEN, windows.FILE_NON_DIRECTORY_FILE)
	if err != nil {
		return nil, err
	}
	if attrs&windows.FILE_ATTRIBUTE_DIRECTORY != 0 {
		_ = windows.CloseHandle(h)
		return nil, errNotPlain
	}
	return os.NewFile(uintptr(h), d.child(name)), nil
}

// openPlainForWrite opens an existing regular file with one link in this folder for writing, to resume an upload.
func (d *safeDir) openPlainForWrite(name string, share uint32) (*os.File, error) {
	h, err := d.ntCreate(name, windows.GENERIC_READ|windows.GENERIC_WRITE|windows.DELETE, share, windows.FILE_OPEN,
		windows.FILE_NON_DIRECTORY_FILE|windows.FILE_OPEN_REPARSE_POINT)
	if err != nil {
		return nil, err
	}
	var info windows.ByHandleFileInformation
	if err := windows.GetFileInformationByHandle(h, &info); err != nil || info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
		info.NumberOfLinks != 1 {
		_ = windows.CloseHandle(h)
		return nil, errNotPlain
	}
	return os.NewFile(uintptr(h), d.child(name)), nil
}

// discardCreated deletes a file this folder created, through its own handle, and closes it.
func (d *safeDir) discardCreated(f *os.File, _ string) {
	deleteOnClose(f)
	_ = f.Close()
}

// remove deletes a file, an empty folder or a link (the link itself) in this folder.
func (d *safeDir) remove(name string) error {
	h, err := d.ntCreate(name, windows.DELETE, shareAll, windows.FILE_OPEN, windows.FILE_OPEN_REPARSE_POINT)
	if err != nil {
		return err
	}
	defer windows.CloseHandle(h)
	return deleteHandle(h, d.child(name))
}

// fileDispositionInformationEx is FILE_DISPOSITION_INFORMATION_EX.
type fileDispositionInformationEx struct{ Flags uint32 }

// deleteHandle marks an open file for deletion, as os.Remove would: POSIX semantics and read-only files included where Windows supports
// it, the classic disposition otherwise.
func deleteHandle(h windows.Handle, path string) error {
	ex := fileDispositionInformationEx{Flags: windows.FILE_DISPOSITION_DELETE | windows.FILE_DISPOSITION_POSIX_SEMANTICS |
		windows.FILE_DISPOSITION_IGNORE_READONLY_ATTRIBUTE}
	var iosb windows.IO_STATUS_BLOCK
	err := windows.NtSetInformationFile(h, &iosb, (*byte)(unsafe.Pointer(&ex)), uint32(unsafe.Sizeof(ex)), windows.FileDispositionInformationEx)
	switch err {
	case nil:
		return nil
	case windows.STATUS_INVALID_INFO_CLASS, windows.STATUS_INVALID_PARAMETER, windows.STATUS_NOT_SUPPORTED:
		del := byte(1)
		if err := windows.NtSetInformationFile(h, &iosb, &del, 1, windows.FileDispositionInformation); err != nil {
			return &os.PathError{Op: "remove", Path: path, Err: ntErrno(err)}
		}
		return nil
	default:
		return &os.PathError{Op: "remove", Path: path, Err: ntErrno(err)}
	}
}

// rename gives an item of this folder (a link is renamed itself) a new name in the same folder.
func (d *safeDir) rename(oldName, newName string, replace bool) error {
	h, err := d.ntCreate(oldName, windows.DELETE, shareAll, windows.FILE_OPEN, windows.FILE_OPEN_REPARSE_POINT)
	if err != nil {
		return err
	}
	defer windows.CloseHandle(h)
	return renameHandle(h, newName, replace, d.child(oldName))
}

// renameHandle renames an open file within its own folder: a bare name with no root directory keeps it there.
func renameHandle(h windows.Handle, newName string, replace bool, path string) error {
	name, err := windows.UTF16FromString(newName)
	if err != nil {
		return err
	}
	name = name[:len(name)-1]
	// FILE_RENAME_INFORMATION: ReplaceIfExists (padded), RootDirectory, FileNameLength, FileName.
	type header struct {
		ReplaceIfExists uint32
		RootDirectory   windows.Handle
		FileNameLength  uint32
	}
	offset := unsafe.Offsetof(header{}.FileNameLength) + unsafe.Sizeof(uint32(0)) // FileName follows without padding
	buf := make([]byte, offset+uintptr(len(name))*2+2)
	hdr := (*header)(unsafe.Pointer(&buf[0]))
	if replace {
		hdr.ReplaceIfExists = 1
	}
	hdr.FileNameLength = uint32(len(name) * 2)
	copy(unsafe.Slice((*uint16)(unsafe.Pointer(&buf[offset])), len(name)), name)
	var iosb windows.IO_STATUS_BLOCK
	if err := windows.NtSetInformationFile(h, &iosb, &buf[0], uint32(len(buf)), windows.FileRenameInformation); err != nil {
		return &os.LinkError{Op: "rename", Old: path, New: newName, Err: ntErrno(err)}
	}
	return nil
}

// entryIsLink reports whether a listed item is a junction or symbolic link, which Go reports as irregular for a junction.
func entryIsLink(path string, mode os.FileMode) bool {
	if mode&(os.ModeSymlink|os.ModeIrregular) == 0 {
		return false
	}
	name, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return false
	}
	h, err := windows.CreateFile(name, windows.FILE_READ_ATTRIBUTES, shareAll, nil, windows.OPEN_EXISTING,
		windows.FILE_FLAG_BACKUP_SEMANTICS|windows.FILE_FLAG_OPEN_REPARSE_POINT, 0)
	if err != nil {
		return mode&os.ModeSymlink != 0
	}
	defer windows.CloseHandle(h)
	info, err := attributeTag(h)
	return err == nil && info.FileAttributes&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0 && info.ReparseTag&reparseTagNameSurrogate != 0
}
