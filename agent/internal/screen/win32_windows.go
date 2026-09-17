//go:build windows

package screen

import (
	"errors"
	"sync"
	"syscall"
	"unsafe"

	"golang.org/x/sys/windows"
)

// The Win32 calls of screen capture and input that golang.org/x/sys/windows does not wrap.

var (
	user32 = windows.NewLazySystemDLL("user32.dll")
	gdi32  = windows.NewLazySystemDLL("gdi32.dll")
	sasDLL = windows.NewLazySystemDLL("sas.dll")

	procOpenInputDesktop              = user32.NewProc("OpenInputDesktop")
	procGetUserObjectInformationW     = user32.NewProc("GetUserObjectInformationW")
	procSetThreadDesktop              = user32.NewProc("SetThreadDesktop")
	procCloseDesktop                  = user32.NewProc("CloseDesktop")
	procGetDC                         = user32.NewProc("GetDC")
	procReleaseDC                     = user32.NewProc("ReleaseDC")
	procEnumDisplayMonitors           = user32.NewProc("EnumDisplayMonitors")
	procGetMonitorInfoW               = user32.NewProc("GetMonitorInfoW")
	procSendInput                     = user32.NewProc("SendInput")
	procVkKeyScanExW                  = user32.NewProc("VkKeyScanExW")
	procMapVirtualKeyExW              = user32.NewProc("MapVirtualKeyExW")
	procGetKeyboardLayout             = user32.NewProc("GetKeyboardLayout")
	procGetForegroundWindow           = user32.NewProc("GetForegroundWindow")
	procGetWindowThreadProcessId      = user32.NewProc("GetWindowThreadProcessId")
	procGetCursorInfo                 = user32.NewProc("GetCursorInfo")
	procGetIconInfo                   = user32.NewProc("GetIconInfo")
	procDrawIconEx                    = user32.NewProc("DrawIconEx")
	procSetProcessDpiAwarenessContext = user32.NewProc("SetProcessDpiAwarenessContext")
	procGetSystemMetrics              = user32.NewProc("GetSystemMetrics")
	procCreateCompatibleDC            = gdi32.NewProc("CreateCompatibleDC")
	procCreateDIBSection              = gdi32.NewProc("CreateDIBSection")
	procSelectObject                  = gdi32.NewProc("SelectObject")
	procBitBlt                        = gdi32.NewProc("BitBlt")
	procDeleteObject                  = gdi32.NewProc("DeleteObject")
	procDeleteDC                      = gdi32.NewProc("DeleteDC")
	procSendSAS                       = sasDLL.NewProc("SendSAS")
)

const (
	desktopGenericAll = 0x10000000
	uoiName           = 2

	smXVirtualScreen  = 76
	smYVirtualScreen  = 77
	smCXVirtualScreen = 78
	smCYVirtualScreen = 79

	srcCopy         = 0x00CC0020
	biRGB           = 0
	dibRGBColors    = 0
	diNormal        = 3
	cursorShowing   = 1
	monitorPrimary  = 1
	mapVKToVSCEx    = 4
	dpiPerMonitorV2 = ^uintptr(3) // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = (HANDLE)-4

	inputMouse    = 0
	inputKeyboard = 1

	mouseMove       = 0x0001
	mouseLeftDown   = 0x0002
	mouseLeftUp     = 0x0004
	mouseRightDown  = 0x0008
	mouseRightUp    = 0x0010
	mouseMiddleDown = 0x0020
	mouseMiddleUp   = 0x0040
	mouseWheel      = 0x0800
	mouseHWheel     = 0x1000
	mouseVirtual    = 0x4000
	mouseAbsolute   = 0x8000
	wheelDelta      = 120

	keyExtended = 0x0001
	keyUp       = 0x0002
	keyUnicode  = 0x0004
	keyScanCode = 0x0008
)

type rect struct{ Left, Top, Right, Bottom int32 }

type point struct{ X, Y int32 }

type monitorInfoEx struct {
	Size    uint32
	Monitor rect
	Work    rect
	Flags   uint32
	Device  [32]uint16
}

type bitmapInfoHeader struct {
	Size          uint32
	Width         int32
	Height        int32
	Planes        uint16
	BitCount      uint16
	Compression   uint32
	SizeImage     uint32
	XPelsPerMeter int32
	YPelsPerMeter int32
	ClrUsed       uint32
	ClrImportant  uint32
}

type cursorInfo struct {
	Size     uint32
	Flags    uint32
	Cursor   windows.Handle
	Position point
}

type iconInfo struct {
	Icon     int32
	XHotspot uint32
	YHotspot uint32
	Mask     windows.Handle
	Color    windows.Handle
}

// mouseInput and keyInput are the MOUSEINPUT and KEYBDINPUT members of INPUT; rawInput has the size of INPUT on 64-bit Windows.
type mouseInput struct {
	DX, DY    int32
	MouseData uint32
	Flags     uint32
	Time      uint32
	ExtraInfo uintptr
}

type keyInput struct {
	VK        uint16
	Scan      uint16
	Flags     uint32
	Time      uint32
	ExtraInfo uintptr
}

type rawInput struct {
	Type  uint32
	Union mouseInput
}

func sendInputs(inputs []rawInput) error {
	if len(inputs) == 0 {
		return nil
	}
	n, _, err := procSendInput.Call(uintptr(len(inputs)), uintptr(unsafe.Pointer(&inputs[0])), unsafe.Sizeof(inputs[0]))
	if int(n) != len(inputs) {
		return fmtCallError("SendInput", err)
	}
	return nil
}

func keyboardInput(k keyInput) rawInput {
	in := rawInput{Type: inputKeyboard}
	*(*keyInput)(unsafe.Pointer(&in.Union)) = k
	return in
}

func fmtCallError(name string, err error) error {
	var errno syscall.Errno
	if errors.As(err, &errno) && errno != 0 {
		return errors.New(name + " failed: " + errno.Error())
	}
	return errors.New(name + " failed")
}

// openInputDesktop opens the desktop that receives input now (Default, Winlogon for the sign-in screen and UAC, Screen-saver).
func openInputDesktop() (windows.Handle, string, error) {
	h, _, err := procOpenInputDesktop.Call(0, 0, desktopGenericAll)
	if h == 0 {
		return 0, "", fmtCallError("OpenInputDesktop", err)
	}
	var name [256]uint16
	var needed uint32
	ok, _, err := procGetUserObjectInformationW.Call(h, uoiName, uintptr(unsafe.Pointer(&name[0])), uintptr(len(name)*2), uintptr(unsafe.Pointer(&needed)))
	if ok == 0 {
		procCloseDesktop.Call(h)
		return 0, "", fmtCallError("GetUserObjectInformation", err)
	}
	return windows.Handle(h), windows.UTF16ToString(name[:]), nil
}

func setThreadDesktop(h windows.Handle) error {
	if ok, _, err := procSetThreadDesktop.Call(uintptr(h)); ok == 0 {
		return fmtCallError("SetThreadDesktop", err)
	}
	return nil
}

func closeDesktop(h windows.Handle) {
	if h != 0 {
		procCloseDesktop.Call(uintptr(h))
	}
}

func systemMetric(index int) int {
	v, _, _ := procGetSystemMetrics.Call(uintptr(index))
	return int(int32(v))
}

// virtualScreen is the rectangle of all monitors together.
func virtualScreen() Monitor {
	return Monitor{Index: -1, Name: "All monitors", X: systemMetric(smXVirtualScreen), Y: systemMetric(smYVirtualScreen),
		Width: systemMetric(smCXVirtualScreen), Height: systemMetric(smCYVirtualScreen)}
}

var (
	monitorMu       sync.Mutex
	monitorFound    []Monitor
	monitorCallback = windows.NewCallback(func(hmon, hdc, lprc, lparam uintptr) uintptr {
		info := monitorInfoEx{}
		info.Size = uint32(unsafe.Sizeof(info))
		if ok, _, _ := procGetMonitorInfoW.Call(hmon, uintptr(unsafe.Pointer(&info))); ok != 0 {
			monitorFound = append(monitorFound, Monitor{
				Index: len(monitorFound), Name: windows.UTF16ToString(info.Device[:]),
				X: int(info.Monitor.Left), Y: int(info.Monitor.Top),
				Width: int(info.Monitor.Right - info.Monitor.Left), Height: int(info.Monitor.Bottom - info.Monitor.Top),
				Primary: info.Flags&monitorPrimary != 0,
			})
		}
		return 1
	})
)

// monitors lists the displays of the desktop the thread is attached to.
func monitors() []Monitor {
	monitorMu.Lock()
	defer monitorMu.Unlock()
	monitorFound = nil
	procEnumDisplayMonitors.Call(0, 0, monitorCallback, 0)
	out := make([]Monitor, len(monitorFound))
	copy(out, monitorFound)
	return out
}

// windowsLayout is the keyboard layout of the window that has the focus on the endpoint, or of the helper thread when no window has it
// (the sign-in screen).
type windowsLayout struct{ hkl uintptr }

func activeLayout() windowsLayout {
	var thread uintptr
	if hwnd, _, _ := procGetForegroundWindow.Call(); hwnd != 0 {
		thread, _, _ = procGetWindowThreadProcessId.Call(hwnd, 0)
	}
	hkl, _, _ := procGetKeyboardLayout.Call(thread)
	return windowsLayout{hkl: hkl}
}

func (l windowsLayout) KeyFor(r rune) (uint16, Modifier, bool) {
	if r > 0xFFFF {
		return 0, 0, false
	}
	v, _, _ := procVkKeyScanExW.Call(uintptr(r), l.hkl)
	result := int16(v)
	if result == -1 {
		return 0, 0, false
	}
	vk := uint16(result & 0xFF)
	state := (result >> 8) & 0xFF
	if state&^0x07 != 0 {
		// Hankaku or other states cannot be pressed with the plain modifiers.
		return 0, 0, false
	}
	var mods Modifier
	if state&1 != 0 {
		mods |= ModShift
	}
	if state&2 != 0 {
		mods |= ModCtrl
	}
	if state&4 != 0 {
		mods |= ModAlt
	}
	return vk, mods, true
}

func (l windowsLayout) ScanFor(vk uint16) Scan {
	v, _, _ := procMapVirtualKeyExW.Call(uintptr(vk), mapVKToVSCEx, l.hkl)
	return Scan{Code: uint16(v & 0xFF), Extended: v&0xFF00 == 0xE000 || v&0xFF00 == 0xE100}
}
