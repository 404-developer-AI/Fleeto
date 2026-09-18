//go:build windows

package screen

import (
	"strings"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Reading the clipboard the way a normal application does (0.3.0 step 4). A program that copies files often puts them on the clipboard
// through OLE: the Win32 clipboard then holds a "DataObject" marker only, and the files themselves are made when someone asks for them.
// Windows Explorer on the test endpoint does exactly that, so the plain clipboard functions find nothing. When they do, the helper asks
// the data object itself, which renders the files and the text on demand.

var (
	ole32                 = windows.NewLazySystemDLL("ole32.dll")
	procOleInitialize     = ole32.NewProc("OleInitialize")
	procOleUninitialize   = ole32.NewProc("OleUninitialize")
	procOleGetClipboard   = ole32.NewProc("OleGetClipboard")
	procReleaseStgMedium  = ole32.NewProc("ReleaseStgMedium")
	procGetWindowThreadPI = user32.NewProc("GetWindowThreadProcessId")
)

const (
	dvAspectContent = 1
	tymedHGlobal    = 1
	// releaseSlot and getDataSlot are the places of Release and GetData in the IDataObject vtable (IUnknown first).
	releaseSlot = 2
	getDataSlot = 3
)

// formatEtc is FORMATETC: what is asked of a data object.
type formatEtc struct {
	Format uint16
	Ptd    uintptr
	Aspect uint32
	Index  int32
	Tymed  uint32
}

// stgMedium is STGMEDIUM: how the answer is delivered.
type stgMedium struct {
	Tymed uint32
	_     uint32
	Union uintptr
	Unk   uintptr
}

// oleClipboard reads the files and the text of the clipboard through its data object. It must not run while the clipboard is open.
func oleClipboard() (paths []string, text string, hasText bool) {
	var object uintptr
	if hr, _, _ := procOleGetClipboard.Call(uintptr(unsafe.Pointer(&object))); hr != 0 || object == 0 {
		return nil, "", false
	}
	defer callMethod(object, releaseSlot)

	if medium, ok := getData(object, cfHDrop); ok {
		paths = dropNamesFrom(medium.Union)
		procReleaseStgMedium.Call(uintptr(unsafe.Pointer(&medium)))
	}
	if medium, ok := getData(object, cfUnicodeText); ok {
		text, hasText = textFrom(medium.Union)
		procReleaseStgMedium.Call(uintptr(unsafe.Pointer(&medium)))
	}
	return paths, text, hasText
}

// getData asks the data object to render one format as memory.
func getData(object uintptr, format uint16) (stgMedium, bool) {
	request := formatEtc{Format: format, Aspect: dvAspectContent, Index: -1, Tymed: tymedHGlobal}
	var medium stgMedium
	vtable := *(*uintptr)(globalPointer(object))
	method := *(*uintptr)(globalPointer(vtable + getDataSlot*unsafe.Sizeof(uintptr(0))))
	hr, _, _ := syscall.SyscallN(method, object, uintptr(unsafe.Pointer(&request)), uintptr(unsafe.Pointer(&medium)))
	if hr != 0 || medium.Tymed != tymedHGlobal || medium.Union == 0 {
		return stgMedium{}, false
	}
	return medium, true
}

func callMethod(object uintptr, slot uintptr) {
	vtable := *(*uintptr)(globalPointer(object))
	method := *(*uintptr)(globalPointer(vtable + slot*unsafe.Sizeof(uintptr(0))))
	syscall.SyscallN(method, object)
}

// dropNamesFrom reads the file names of an HDROP.
func dropNamesFrom(drop uintptr) []string {
	count, _, _ := procDragQueryFileW.Call(drop, 0xFFFFFFFF, 0, 0)
	var paths []string
	for i := uintptr(0); i < count && len(paths) < MaxCopiedFiles; i++ {
		n, _, _ := procDragQueryFileW.Call(drop, i, 0, 0)
		if n == 0 {
			continue
		}
		buffer := make([]uint16, n+1)
		procDragQueryFileW.Call(drop, i, uintptr(unsafe.Pointer(&buffer[0])), n+1)
		paths = append(paths, windows.UTF16ToString(buffer))
	}
	return paths
}

// textFrom reads UTF-16 text out of global memory, at most a little over the limit so a longer text is refused whole.
func textFrom(handle uintptr) (string, bool) {
	pointer, _, _ := procGlobalLock.Call(handle)
	if pointer == 0 {
		return "", false
	}
	defer procGlobalUnlock.Call(handle)
	size, _, _ := procGlobalSize.Call(handle)
	units := min(int(size/2), MaxClipboardBytes+1)
	if units <= 0 {
		return "", false
	}
	return textFromUTF16(unsafe.Slice((*uint16)(globalPointer(pointer)), units)), true
}

// clipboardOwnerName is the program that owns the clipboard now, for the log: it tells which program a copy came from.
func clipboardOwnerName() string {
	owner, _, _ := procGetClipboardOwner.Call()
	if owner == 0 {
		return ""
	}
	var pid uint32
	procGetWindowThreadPI.Call(owner, uintptr(unsafe.Pointer(&pid)))
	if pid == 0 {
		return ""
	}
	process, err := windows.OpenProcess(windows.PROCESS_QUERY_LIMITED_INFORMATION, false, pid)
	if err != nil {
		return ""
	}
	defer windows.CloseHandle(process)
	var buffer [windows.MAX_PATH]uint16
	size := uint32(len(buffer))
	if err := windows.QueryFullProcessImageName(process, 0, &buffer[0], &size); err != nil {
		return ""
	}
	path := windows.UTF16ToString(buffer[:size])
	if index := strings.LastIndexAny(path, `\/`); index >= 0 {
		return path[index+1:]
	}
	return path
}
