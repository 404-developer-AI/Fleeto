package screen

import (
	"encoding/binary"
	"errors"
	"strings"
	"unicode/utf16"
)

// Clipboard data formats of Windows, built and read without Win32 calls so they can be tested on every platform.

// dropFilesHeaderBytes is the size of DROPFILES: pFiles, pt (x, y), fNC, fWide.
const dropFilesHeaderBytes = 20

// dropFiles builds CF_HDROP data for files placed on the clipboard: the DROPFILES header with wide names, then every path as UTF-16 with a
// NUL, and a final NUL.
func dropFiles(paths []string) ([]byte, error) {
	if len(paths) == 0 {
		return nil, errors.New("no files to place")
	}
	out := make([]byte, dropFilesHeaderBytes, dropFilesHeaderBytes+64*len(paths))
	binary.LittleEndian.PutUint32(out[0:], dropFilesHeaderBytes)
	binary.LittleEndian.PutUint32(out[16:], 1) // fWide
	for _, p := range paths {
		if p == "" || strings.ContainsRune(p, 0) {
			return nil, errors.New("a file path is invalid")
		}
		out = appendUTF16(out, p)
	}
	return append(out, 0, 0), nil
}

// utf16Text is a string as NUL-terminated little-endian UTF-16, the layout of CF_UNICODETEXT.
func utf16Text(s string) []byte {
	s = strings.ReplaceAll(s, "\x00", "")
	return appendUTF16(nil, s)
}

func appendUTF16(out []byte, s string) []byte {
	for _, unit := range utf16.Encode([]rune(s)) {
		out = binary.LittleEndian.AppendUint16(out, unit)
	}
	return append(out, 0, 0)
}

// textFromUTF16 reads CF_UNICODETEXT up to its NUL or the end of the data.
func textFromUTF16(units []uint16) string {
	for i, u := range units {
		if u == 0 {
			units = units[:i]
			break
		}
	}
	return string(utf16.Decode(units))
}

// bannerText is the text of the banner on the endpoint's screen.
func bannerText(names []string) string {
	switch len(names) {
	case 0:
		return ""
	case 1:
		return "Remote control session by " + names[0]
	default:
		return "Remote control session by " + strings.Join(names[:len(names)-1], ", ") + " and " + names[len(names)-1]
	}
}
