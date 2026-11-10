//go:build windows

package update

import (
	"os"

	"golang.org/x/sys/windows"
)

// openHeld opens a staged binary for reading so that nobody can write, rename or delete it while the handle is open; running it stays
// possible, because starting a process only needs read sharing. A link is opened as itself, so what is read is never what it points to.
func openHeld(path string) (*os.File, error) {
	name, err := windows.UTF16PtrFromString(path)
	if err != nil {
		return nil, err
	}
	h, err := windows.CreateFile(name, windows.GENERIC_READ, windows.FILE_SHARE_READ, nil, windows.OPEN_EXISTING,
		windows.FILE_ATTRIBUTE_NORMAL|windows.FILE_FLAG_OPEN_REPARSE_POINT, 0)
	if err != nil {
		return nil, &os.PathError{Op: "open", Path: path, Err: err}
	}
	return os.NewFile(uintptr(h), path), nil
}
