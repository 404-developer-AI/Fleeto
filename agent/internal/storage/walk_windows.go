//go:build windows

package storage

import (
	"encoding/binary"
	"errors"
	"strings"
	"unicode/utf16"

	"golang.org/x/sys/windows"
)

const (
	fileListDirectory = 0x0001
	// Offsets in FILE_ID_BOTH_DIR_INFO.
	dirInfoLastWrite  = 24
	dirInfoAllocation = 48
	dirInfoAttributes = 56
	dirInfoNameLength = 60
	dirInfoName       = 104
)

// windowsLister reads a folder with GetFileInformationByHandleEx, which gives the size on disk of every item without opening it.
// Reparse points (junctions, symbolic links, mount points) are never entered; a reparse file with data (deduplication, cloud
// files) is counted with the space it takes.
func windowsLister(path string) ([]entry, error) {
	p, err := windows.UTF16PtrFromString(longPath(path))
	if err != nil {
		return nil, err
	}
	h, err := windows.CreateFile(p, fileListDirectory, windows.FILE_SHARE_READ|windows.FILE_SHARE_WRITE|windows.FILE_SHARE_DELETE, nil,
		windows.OPEN_EXISTING, windows.FILE_FLAG_BACKUP_SEMANTICS|windows.FILE_FLAG_OPEN_REPARSE_POINT, 0)
	if err != nil {
		return nil, err
	}
	defer windows.CloseHandle(h)
	buf := make([]byte, 64*1024)
	class := uint32(windows.FileIdBothDirectoryRestartInfo)
	var out []entry
	for {
		err := windows.GetFileInformationByHandleEx(h, class, &buf[0], uint32(len(buf)))
		if errors.Is(err, windows.ERROR_NO_MORE_FILES) {
			return out, nil
		}
		if err != nil {
			return out, err
		}
		class = windows.FileIdBothDirectoryInfo
		out = appendDirInfo(out, buf)
	}
}

// appendDirInfo decodes a buffer of FILE_ID_BOTH_DIR_INFO records.
func appendDirInfo(out []entry, buf []byte) []entry {
	for off := 0; off+dirInfoName <= len(buf); {
		rec := buf[off:]
		next := int(binary.LittleEndian.Uint32(rec))
		nameBytes := int(binary.LittleEndian.Uint32(rec[dirInfoNameLength:]))
		if dirInfoName+nameBytes > len(rec) {
			return out
		}
		units := make([]uint16, nameBytes/2)
		for i := range units {
			units[i] = binary.LittleEndian.Uint16(rec[dirInfoName+i*2:])
		}
		name := string(utf16.Decode(units))
		attrs := binary.LittleEndian.Uint32(rec[dirInfoAttributes:])
		if name != "." && name != ".." {
			isDir := attrs&windows.FILE_ATTRIBUTE_DIRECTORY != 0
			out = append(out, entry{
				name:     name,
				dir:      isDir,
				skip:     isDir && attrs&windows.FILE_ATTRIBUTE_REPARSE_POINT != 0,
				size:     binary.LittleEndian.Uint64(rec[dirInfoAllocation:]),
				modified: filetime(binary.LittleEndian.Uint64(rec[dirInfoLastWrite:])),
			})
		}
		if next == 0 {
			return out
		}
		off += next
	}
	return out
}

// longPath prefixes a local path with \\?\ so paths over 260 characters work.
func longPath(path string) string {
	if strings.HasPrefix(path, `\\`) {
		return path
	}
	return `\\?\` + path
}

// platformLister is the lister of a walk of this volume.
func platformLister(string) (lister, error) { return windowsLister, nil }
