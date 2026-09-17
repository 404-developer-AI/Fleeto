//go:build windows

package screen

import (
	"encoding/binary"
	"errors"
	"log/slog"
	"os"
	"path/filepath"
	"runtime"
	"sync"
	"sync/atomic"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// The helper's desktop thread (0.3.0 step 4): the banner that names the technicians and the clipboard of the Windows session. It runs on its
// own locked OS thread attached to the Default desktop, because a thread that owns windows cannot follow the input desktop the way the
// capture thread does. The banner is a small click-through window at the top of the primary monitor; the clipboard is watched with a
// clipboard format listener, so a copy on the endpoint reaches the technicians at once.

var (
	kernel32 = windows.NewLazySystemDLL("kernel32.dll")
	shell32  = windows.NewLazySystemDLL("shell32.dll")

	procRegisterClassExW              = user32.NewProc("RegisterClassExW")
	procCreateWindowExW               = user32.NewProc("CreateWindowExW")
	procDefWindowProcW                = user32.NewProc("DefWindowProcW")
	procDestroyWindow                 = user32.NewProc("DestroyWindow")
	procGetMessageW                   = user32.NewProc("GetMessageW")
	procTranslateMessage              = user32.NewProc("TranslateMessage")
	procDispatchMessageW              = user32.NewProc("DispatchMessageW")
	procPostMessageW                  = user32.NewProc("PostMessageW")
	procPostQuitMessage               = user32.NewProc("PostQuitMessage")
	procShowWindow                    = user32.NewProc("ShowWindow")
	procSetWindowPos                  = user32.NewProc("SetWindowPos")
	procInvalidateRect                = user32.NewProc("InvalidateRect")
	procBeginPaint                    = user32.NewProc("BeginPaint")
	procEndPaint                      = user32.NewProc("EndPaint")
	procFillRect                      = user32.NewProc("FillRect")
	procDrawTextW                     = user32.NewProc("DrawTextW")
	procGetClientRect                 = user32.NewProc("GetClientRect")
	procSetTimer                      = user32.NewProc("SetTimer")
	procSetLayeredWindowAttributes    = user32.NewProc("SetLayeredWindowAttributes")
	procGetDpiForSystem               = user32.NewProc("GetDpiForSystem")
	procOpenDesktopW                  = user32.NewProc("OpenDesktopW")
	procAddClipboardFormatListener    = user32.NewProc("AddClipboardFormatListener")
	procRemoveClipboardFormatListener = user32.NewProc("RemoveClipboardFormatListener")
	procOpenClipboard                 = user32.NewProc("OpenClipboard")
	procCloseClipboard                = user32.NewProc("CloseClipboard")
	procEmptyClipboard                = user32.NewProc("EmptyClipboard")
	procGetClipboardData              = user32.NewProc("GetClipboardData")
	procSetClipboardData              = user32.NewProc("SetClipboardData")
	procIsClipboardFormatAvailable    = user32.NewProc("IsClipboardFormatAvailable")
	procGetClipboardOwner             = user32.NewProc("GetClipboardOwner")
	procRegisterClipboardFormatW      = user32.NewProc("RegisterClipboardFormatW")
	procCreateSolidBrush              = gdi32.NewProc("CreateSolidBrush")
	procCreateFontW                   = gdi32.NewProc("CreateFontW")
	procSetTextColor                  = gdi32.NewProc("SetTextColor")
	procSetBkMode                     = gdi32.NewProc("SetBkMode")
	procGetModuleHandleW              = kernel32.NewProc("GetModuleHandleW")
	procGlobalAlloc                   = kernel32.NewProc("GlobalAlloc")
	procGlobalFree                    = kernel32.NewProc("GlobalFree")
	procGlobalLock                    = kernel32.NewProc("GlobalLock")
	procGlobalUnlock                  = kernel32.NewProc("GlobalUnlock")
	procGlobalSize                    = kernel32.NewProc("GlobalSize")
	procDragQueryFileW                = shell32.NewProc("DragQueryFileW")
)

const (
	wmDestroy         = 0x0002
	wmPaint           = 0x000F
	wmClose           = 0x0010
	wmTimer           = 0x0113
	wmClipboardUpdate = 0x031D
	wmApp             = 0x8000

	wsPopup          = 0x80000000
	wsExTopmost      = 0x00000008
	wsExTransparent  = 0x00000020
	wsExToolWindow   = 0x00000080
	wsExLayered      = 0x00080000
	wsExNoActivate   = 0x08000000
	swHide           = 0
	swShowNoActivate = 4
	swpNoActivate    = 0x0010
	swpShowWindow    = 0x0040
	hwndTopmost      = ^uintptr(0) // (HWND)-1
	lwaAlpha         = 0x2

	dtCenter     = 0x0001
	dtVCenter    = 0x0004
	dtSingleLine = 0x0020
	dtCalcRect   = 0x0400
	transparent  = 1
	fwSemiBold   = 600

	cfUnicodeText  = 13
	cfHDrop        = 15
	gmemMoveable   = 0x0002
	dropEffectCopy = 1

	smCXScreen = 0

	desktopAccess = 0x0001 | 0x0002 | 0x0040 | 0x0080 | 0x0100 // read objects, create window, enumerate, write objects, switch

	bannerTimer = 1
	// bannerColor is the primary teal of the brand (#0F766E) as a COLORREF (0x00BBGGRR); the text is white.
	bannerColor = 0x006E760F
	textColor   = 0x00FFFFFF
)

type wndClassEx struct {
	Size       uint32
	Style      uint32
	WndProc    uintptr
	ClsExtra   int32
	WndExtra   int32
	Instance   uintptr
	Icon       uintptr
	Cursor     uintptr
	Background uintptr
	MenuName   *uint16
	ClassName  *uint16
	IconSm     uintptr
}

type winMsg struct {
	Hwnd     uintptr
	Message  uint32
	WParam   uintptr
	LParam   uintptr
	Time     uint32
	Pt       point
	LPrivate uint32
}

type paintStruct struct {
	HDC       uintptr
	Erase     int32
	Paint     rect
	Restore   int32
	IncUpdate int32
	Reserved  [32]byte
}

// desktopUI is the desktop thread of one helper process.
type desktopUI struct {
	write  func([]byte) error
	logger *slog.Logger

	hwnd     uintptr
	font     uintptr
	brush    uintptr
	commands chan func()
	running  atomic.Bool

	// Used on the desktop thread only.
	names        []string
	placedFiles  bool
	offeredFiles bool
	dropEffect   uintptr
}

// activeUI is the desktop thread the window procedure serves; a helper process has one.
var activeUI atomic.Pointer[desktopUI]

var (
	windowProcOnce sync.Once
	windowProc     uintptr
)

// startDesktopUI starts the desktop thread. When it cannot (no desktop, no window station), remote control works without a banner and
// clipboard, and the technician is told why.
func startDesktopUI(write func([]byte) error, logger *slog.Logger) *desktopUI {
	ui := &desktopUI{write: write, logger: logger, commands: make(chan func(), 64)}
	ready := make(chan error, 1)
	go func() {
		runtime.LockOSThread()
		// The thread exits with its window; it is never reused for other goroutines.
		if err := ui.create(); err != nil {
			ready <- err
			return
		}
		ui.running.Store(true)
		ready <- nil
		ui.loop()
	}()
	select {
	case err := <-ready:
		if err != nil {
			logger.Warn("the banner and clipboard are not available", "error", err)
		}
	case <-time.After(5 * time.Second):
		logger.Warn("the banner and clipboard did not start in time")
	}
	return ui
}

func (ui *desktopUI) create() error {
	// A new OS thread starts on the process's startup desktop (winsta0\default); attach explicitly anyway.
	name, _ := windows.UTF16PtrFromString("Default")
	if desk, _, _ := procOpenDesktopW.Call(uintptr(unsafe.Pointer(name)), 0, 0, desktopAccess); desk != 0 {
		if err := setThreadDesktop(windows.Handle(desk)); err != nil {
			closeDesktop(windows.Handle(desk))
		}
	}
	windowProcOnce.Do(func() { windowProc = windows.NewCallback(wndProc) })
	instance, _, _ := procGetModuleHandleW.Call(0)
	className, _ := windows.UTF16PtrFromString("FleetoRemoteControlBanner")
	class := wndClassEx{WndProc: windowProc, Instance: instance, ClassName: className}
	class.Size = uint32(unsafe.Sizeof(class))
	procRegisterClassExW.Call(uintptr(unsafe.Pointer(&class))) // fails harmlessly when a previous thread registered it

	ui.brush, _, _ = procCreateSolidBrush.Call(bannerColor)
	dpi := uintptr(96)
	if procGetDpiForSystem.Find() == nil {
		if v, _, _ := procGetDpiForSystem.Call(); v != 0 {
			dpi = v
		}
	}
	face, _ := windows.UTF16PtrFromString("Segoe UI")
	height := -int32(10 * dpi / 72)
	ui.font, _, _ = procCreateFontW.Call(uintptr(height), 0, 0, 0, fwSemiBold, 0, 0, 0, 1, 0, 0, 5, 0, uintptr(unsafe.Pointer(face)))

	activeUI.Store(ui)
	hwnd, _, err := procCreateWindowExW.Call(wsExTopmost|wsExToolWindow|wsExNoActivate|wsExLayered|wsExTransparent,
		uintptr(unsafe.Pointer(className)), 0, wsPopup, 0, 0, 1, 1, 0, 0, instance, 0)
	if hwnd == 0 {
		activeUI.Store(nil)
		return fmtCallError("CreateWindowEx", err)
	}
	ui.hwnd = hwnd
	procSetLayeredWindowAttributes.Call(hwnd, 0, 235, lwaAlpha)
	if ok, _, err := procAddClipboardFormatListener.Call(hwnd); ok == 0 {
		ui.logger.Warn("the endpoint clipboard is not watched", "error", fmtCallError("AddClipboardFormatListener", err))
	}
	format, _ := windows.UTF16PtrFromString("Preferred DropEffect")
	ui.dropEffect, _, _ = procRegisterClipboardFormatW.Call(uintptr(unsafe.Pointer(format)))
	return nil
}

func (ui *desktopUI) loop() {
	var m winMsg
	for {
		r, _, _ := procGetMessageW.Call(uintptr(unsafe.Pointer(&m)), 0, 0, 0)
		if int32(r) <= 0 {
			break
		}
		procTranslateMessage.Call(uintptr(unsafe.Pointer(&m)))
		procDispatchMessageW.Call(uintptr(unsafe.Pointer(&m)))
	}
	ui.running.Store(false)
	activeUI.CompareAndSwap(ui, nil)
	procDeleteObject.Call(ui.font)
	procDeleteObject.Call(ui.brush)
}

// do runs a function on the desktop thread.
func (ui *desktopUI) do(fn func()) bool {
	if !ui.running.Load() {
		return false
	}
	select {
	case ui.commands <- fn:
		procPostMessageW.Call(ui.hwnd, wmApp, 0, 0)
		return true
	default:
		return false
	}
}

// setBanner shows the banner with these names, or hides it.
func (ui *desktopUI) setBanner(names []string) {
	names = append([]string(nil), names...)
	ui.do(func() {
		ui.names = names
		ui.layoutBanner()
	})
}

// doWait runs a function on the desktop thread and waits until it ran, so input that follows (Ctrl+V after the clipboard text) finds the
// clipboard already set.
func (ui *desktopUI) doWait(fn func()) bool {
	done := make(chan struct{})
	if !ui.do(func() {
		defer close(done)
		fn()
	}) {
		return false
	}
	select {
	case <-done:
	case <-time.After(2 * time.Second):
	}
	return true
}

// setText puts text on the endpoint clipboard.
func (ui *desktopUI) setText(text string) {
	if !ui.doWait(func() {
		if err := ui.putClipboard(cfUnicodeText, utf16Text(text), false); err != nil {
			ui.notice("The text could not be placed on the endpoint clipboard: " + err.Error())
		}
	}) {
		ui.notice("The endpoint clipboard is not available in this Windows session.")
	}
}

// placeFiles puts staged files on the endpoint clipboard, marked to be copied (not moved) when pasted.
func (ui *desktopUI) placeFiles(paths []string) {
	data, err := dropFiles(paths)
	if err != nil {
		ui.notice("The files could not be placed on the endpoint clipboard: " + err.Error())
		return
	}
	if !ui.doWait(func() {
		if err := ui.putClipboard(cfHDrop, data, true); err != nil {
			ui.notice("The files could not be placed on the endpoint clipboard: " + err.Error())
			return
		}
		ui.placedFiles = true
	}) {
		ui.notice("The endpoint clipboard is not available in this Windows session.")
	}
}

// stop closes the banner, and takes pasted files off the clipboard: they are deleted with the session.
func (ui *desktopUI) stop() {
	if !ui.running.Load() {
		return
	}
	procPostMessageW.Call(ui.hwnd, wmClose, 0, 0)
	deadline := time.Now().Add(2 * time.Second)
	for ui.running.Load() && time.Now().Before(deadline) {
		time.Sleep(10 * time.Millisecond)
	}
}

func (ui *desktopUI) notice(message string) {
	_ = ui.write(jsonFrame(FrameNotice, NoticeBody{Message: message}))
}

func wndProc(hwnd, message, wParam, lParam uintptr) uintptr {
	ui := activeUI.Load()
	if ui == nil || (ui.hwnd != 0 && hwnd != ui.hwnd) {
		r, _, _ := procDefWindowProcW.Call(hwnd, message, wParam, lParam)
		return r
	}
	switch message {
	case wmApp:
		for {
			select {
			case fn := <-ui.commands:
				fn()
				continue
			default:
			}
			break
		}
		return 0
	case wmPaint:
		ui.paint(hwnd)
		return 0
	case wmTimer:
		if len(ui.names) > 0 {
			ui.layoutBanner() // stay on top and centred when the resolution changes
		}
		return 0
	case wmClipboardUpdate:
		ui.clipboardChanged(hwnd)
		return 0
	case wmClose:
		if ui.placedFiles && ui.ownsClipboard(hwnd) && openClipboard(hwnd) {
			procEmptyClipboard.Call()
			procCloseClipboard.Call()
		}
		procRemoveClipboardFormatListener.Call(hwnd)
		procDestroyWindow.Call(hwnd)
		return 0
	case wmDestroy:
		procPostQuitMessage.Call(0)
		return 0
	}
	r, _, _ := procDefWindowProcW.Call(hwnd, message, wParam, lParam)
	return r
}

// layoutBanner sizes the banner to its text at the top centre of the primary monitor, or hides it without names.
func (ui *desktopUI) layoutBanner() {
	text := bannerText(ui.names)
	if text == "" {
		procShowWindow.Call(ui.hwnd, swHide)
		return
	}
	width, height := ui.measure(text)
	screenWidth := systemMetric(smCXScreen)
	x := max((screenWidth-width)/2, 0)
	procSetWindowPos.Call(ui.hwnd, hwndTopmost, uintptr(int32(x)), 0, uintptr(width), uintptr(height), swpNoActivate|swpShowWindow)
	procInvalidateRect.Call(ui.hwnd, 0, 1)
	procSetTimer.Call(ui.hwnd, bannerTimer, 2000, 0)
}

func (ui *desktopUI) measure(text string) (int, int) {
	textUTF16, _ := windows.UTF16FromString(text)
	dc, _, _ := procGetDC.Call(ui.hwnd)
	if dc == 0 {
		return 400, 32
	}
	defer procReleaseDC.Call(ui.hwnd, dc)
	old, _, _ := procSelectObject.Call(dc, ui.font)
	var r rect
	procDrawTextW.Call(dc, uintptr(unsafe.Pointer(&textUTF16[0])), ^uintptr(0), uintptr(unsafe.Pointer(&r)), dtCalcRect|dtSingleLine)
	procSelectObject.Call(dc, old)
	padding := int(r.Bottom-r.Top) / 2
	return int(r.Right-r.Left) + 4*padding, int(r.Bottom-r.Top) + 2*padding
}

func (ui *desktopUI) paint(hwnd uintptr) {
	var ps paintStruct
	dc, _, _ := procBeginPaint.Call(hwnd, uintptr(unsafe.Pointer(&ps)))
	if dc == 0 {
		return
	}
	defer procEndPaint.Call(hwnd, uintptr(unsafe.Pointer(&ps)))
	var r rect
	procGetClientRect.Call(hwnd, uintptr(unsafe.Pointer(&r)))
	procFillRect.Call(dc, uintptr(unsafe.Pointer(&r)), ui.brush)
	text := bannerText(ui.names)
	if text == "" {
		return
	}
	textUTF16, _ := windows.UTF16FromString(text)
	old, _, _ := procSelectObject.Call(dc, ui.font)
	procSetTextColor.Call(dc, textColor)
	procSetBkMode.Call(dc, transparent)
	procDrawTextW.Call(dc, uintptr(unsafe.Pointer(&textUTF16[0])), ^uintptr(0), uintptr(unsafe.Pointer(&r)), dtCenter|dtVCenter|dtSingleLine)
	procSelectObject.Call(dc, old)
}

func (ui *desktopUI) ownsClipboard(hwnd uintptr) bool {
	owner, _, _ := procGetClipboardOwner.Call()
	return owner == hwnd
}

// openClipboard opens the clipboard, waiting a moment when another program holds it.
func openClipboard(hwnd uintptr) bool {
	for i := 0; i < 10; i++ {
		if ok, _, _ := procOpenClipboard.Call(hwnd); ok != 0 {
			return true
		}
		time.Sleep(20 * time.Millisecond)
	}
	return false
}

// putClipboard replaces the clipboard with one format. Files are marked as a copy, so pasting never moves the staged files away.
func (ui *desktopUI) putClipboard(format uintptr, data []byte, files bool) error {
	if !openClipboard(ui.hwnd) {
		return errors.New("another program holds the clipboard")
	}
	defer procCloseClipboard.Call()
	procEmptyClipboard.Call()
	if err := setClipboardBytes(format, data); err != nil {
		return err
	}
	if files && ui.dropEffect != 0 {
		effect := binary.LittleEndian.AppendUint32(nil, dropEffectCopy)
		_ = setClipboardBytes(ui.dropEffect, effect)
	}
	if !files {
		ui.placedFiles = false
	}
	return nil
}

func setClipboardBytes(format uintptr, data []byte) error {
	handle, _, err := procGlobalAlloc.Call(gmemMoveable, uintptr(len(data)))
	if handle == 0 {
		return fmtCallError("GlobalAlloc", err)
	}
	pointer, _, err := procGlobalLock.Call(handle)
	if pointer == 0 {
		procGlobalFree.Call(handle)
		return fmtCallError("GlobalLock", err)
	}
	copy(unsafe.Slice((*byte)(globalPointer(pointer)), len(data)), data)
	procGlobalUnlock.Call(handle)
	if r, _, err := procSetClipboardData.Call(format, handle); r == 0 {
		procGlobalFree.Call(handle)
		return fmtCallError("SetClipboardData", err)
	}
	return nil // the clipboard owns the memory now
}

// clipboardChanged reads what another program copied and sends it to the technicians: the text, and the files as an offer to download.
func (ui *desktopUI) clipboardChanged(hwnd uintptr) {
	if ui.ownsClipboard(hwnd) {
		return // what a technician placed; never echoed back
	}
	if !openClipboard(hwnd) {
		return
	}
	var (
		paths   []string
		text    string
		hasText bool
	)
	if ok, _, _ := procIsClipboardFormatAvailable.Call(cfHDrop); ok != 0 {
		paths = dropFileNames(hwnd)
	}
	if ok, _, _ := procIsClipboardFormatAvailable.Call(cfUnicodeText); ok != 0 {
		text, hasText = clipboardText()
	}
	procCloseClipboard.Call()

	if len(paths) > 0 || ui.offeredFiles {
		files := make([]CopiedFile, 0, len(paths))
		for _, p := range paths {
			info, err := os.Stat(p)
			if err != nil {
				continue
			}
			files = append(files, CopiedFile{Path: p, Name: filepath.Base(p), Size: info.Size(), Dir: info.IsDir()})
		}
		ui.offeredFiles = len(files) > 0
		_ = ui.write(jsonFrame(FrameCopiedFiles, CopiedFilesBody{Files: files}))
	}
	if hasText {
		if len(text) > MaxClipboardBytes {
			ui.notice("The text copied on the endpoint is larger than the 512 KB remote control synchronises. Use Files in remote background instead.")
			return
		}
		_ = ui.write(append([]byte{FrameClipboard}, text...))
	}
}

func dropFileNames(uintptr) []string {
	handle, _, _ := procGetClipboardData.Call(cfHDrop)
	if handle == 0 {
		return nil
	}
	count, _, _ := procDragQueryFileW.Call(handle, 0xFFFFFFFF, 0, 0)
	var paths []string
	for i := uintptr(0); i < count && len(paths) < MaxCopiedFiles; i++ {
		n, _, _ := procDragQueryFileW.Call(handle, i, 0, 0)
		if n == 0 {
			continue
		}
		buffer := make([]uint16, n+1)
		procDragQueryFileW.Call(handle, i, uintptr(unsafe.Pointer(&buffer[0])), n+1)
		paths = append(paths, windows.UTF16ToString(buffer))
	}
	return paths
}

func clipboardText() (string, bool) {
	handle, _, _ := procGetClipboardData.Call(cfUnicodeText)
	if handle == 0 {
		return "", false
	}
	pointer, _, _ := procGlobalLock.Call(handle)
	if pointer == 0 {
		return "", false
	}
	defer procGlobalUnlock.Call(handle)
	size, _, _ := procGlobalSize.Call(handle)
	// Read at most a little over the limit; a longer text is refused whole, never cut.
	units := min(int(size/2), MaxClipboardBytes+1)
	return textFromUTF16(unsafe.Slice((*uint16)(globalPointer(pointer)), units)), true
}

// globalPointer turns the address GlobalLock returns into a pointer. The memory belongs to Windows, not the Go heap, so the garbage
// collector never moves it.
func globalPointer(address uintptr) unsafe.Pointer {
	return *(*unsafe.Pointer)(unsafe.Pointer(&address))
}
