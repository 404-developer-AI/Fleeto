//go:build windows

package screen

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"runtime"
	"sync"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// The consent prompt on Windows (0.8.0, decided 2026-11-04): a window in the style of Fleeto, drawn with plain Win32 and GDI by
// "fleeto-agent remote-consent", which the agent service starts as the user signed in on the Windows session, on its default desktop
// (spawnAsSessionUser). A white card, centred on the primary monitor and on top of every window, with a teal bar along the top, the title,
// the technician in bold, the message of the policy, a teal bar that counts down with the seconds left, and Deny and Allow. Deny is the
// default and has the focus: Enter and Escape refuse, closing the window refuses, only a click on Allow (or Space with the focus on Allow)
// allows. Every text is drawn with DrawText as plain text (DT_NOPREFIX), never as markup.

var (
	dwmapi = windows.NewLazySystemDLL("dwmapi.dll")

	procCreatePen              = gdi32.NewProc("CreatePen")
	procRoundRect              = gdi32.NewProc("RoundRect")
	procGetStockObject         = gdi32.NewProc("GetStockObject")
	procCreateCompatibleBitmap = gdi32.NewProc("CreateCompatibleBitmap")
	procSetForegroundWindow    = user32.NewProc("SetForegroundWindow")
	procKillTimer              = user32.NewProc("KillTimer")
	procLoadCursorW            = user32.NewProc("LoadCursorW")
	procSetCursor              = user32.NewProc("SetCursor")
	procSetProcessDPIAware     = user32.NewProc("SetProcessDPIAware")
	procDwmSetWindowAttribute  = dwmapi.NewProc("DwmSetWindowAttribute")
)

const (
	wmSetCursor   = 0x0020
	wmEraseBkgnd  = 0x0014
	wmKeyDown     = 0x0100
	wmKeyUp       = 0x0101
	wmMouseMove   = 0x0200
	wmLButtonDown = 0x0201
	wmLButtonUp   = 0x0202

	vkTab    = 0x09
	vkReturn = 0x0D
	vkEscape = 0x1B
	vkSpace  = 0x20
	vkLeft   = 0x25
	vkRight  = 0x27

	wsExAppWindow = 0x00040000
	csDropShadow  = 0x00020000
	csHRedraw     = 0x0002
	csVRedraw     = 0x0001
	swShow        = 5
	swpNoMove     = 0x0002
	swpNoSize     = 0x0001
	smCYScreen    = 1
	idcArrow      = 32512
	idcHand       = 32649
	nullBrush     = 5

	dtWordBreak   = 0x0010
	dtNoPrefix    = 0x0800
	dtEndEllipsis = 0x8000
	dtEditControl = 0x2000

	fwNormal = 400
	fwBold   = 700

	dwmaWindowCornerPreference = 33
	dwmCornerRound             = 2
	// dpiPerMonitorAwareV2 is DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2, (HANDLE)-4.
	dpiPerMonitorAwareV2 = ^uintptr(3)

	consentTimer = 7

	// Colors of the brand as COLORREF (0x00BBGGRR).
	rgbWhite      = 0x00FFFFFF
	rgbTeal       = 0x006E760F // #0F766E
	rgbTealHover  = 0x00595E11 // #115E59
	rgbTealTint   = 0x00F1FBCC // #CCFBF1
	rgbInk        = 0x0017191C // #1C1917
	rgbBody       = 0x003C4044 // #44403C
	rgbMuted      = 0x006C7178 // #78716C
	rgbBorder     = 0x00E4E5E7 // #E7E5E4
	rgbBorderDeep = 0x00D1D3D6 // #D6D3D1
	rgbStoneHover = 0x00F4F5F5 // #F5F5F4
)

// Button indexes of the prompt.
const (
	buttonNone  = -1
	buttonDeny  = 0
	buttonAllow = 1
)

// RunConsent asks the person at the screen of this Windows session and writes the answer.
func RunConsent(ctx context.Context, in io.Reader, out io.Writer, logger *slog.Logger) error {
	ask, err := readConsentAsk(in)
	if err != nil {
		return err
	}
	answer, err := showConsentWindow(ctx, ask, in)
	if err != nil {
		logger.Warn("the consent prompt could not be shown", "error", err)
		return err
	}
	return writeConsentAnswer(out, answer)
}

// consentLayoutWin is where the parts of the prompt stand, in pixels of the window at its DPI.
type consentLayoutWin struct {
	W, H                   int32
	Accent                 rect
	Brand, Title, Tech     rect
	Message, Bar, TimeLeft rect
	Deny, Allow            rect
	Radius                 int32
}

// layoutConsentWindow places the parts for a scale (pixels per 96 DPI), the measured height of the message and the measured widths of
// the two labels. It is pure, so it is tested without a screen.
func layoutConsentWindow(scale func(int32) int32, messageHeight, denyTextW, allowTextW int32) consentLayoutWin {
	pad := scale(28)
	l := consentLayoutWin{W: scale(460), Radius: scale(8)}
	inner := l.W - 2*pad
	l.Accent = rect{0, 0, l.W, scale(6)}
	y := l.Accent.Bottom + scale(22)
	l.Brand = rect{pad, y, pad + inner, y + scale(16)}
	y = l.Brand.Bottom + scale(6)
	l.Title = rect{pad, y, pad + inner, y + scale(26)}
	y = l.Title.Bottom + scale(10)
	l.Tech = rect{pad, y, pad + inner, y + scale(22)}
	y = l.Tech.Bottom + scale(4)
	l.Message = rect{pad, y, pad + inner, y + max(messageHeight, scale(20))}
	y = l.Message.Bottom + scale(20)
	l.Bar = rect{pad, y, pad + inner, y + scale(6)}
	y = l.Bar.Bottom + scale(8)
	l.TimeLeft = rect{pad, y, pad + inner, y + scale(18)}
	y = l.TimeLeft.Bottom + scale(22)
	buttonH := scale(36)
	allowW := max(scale(104), allowTextW+scale(40))
	denyW := max(scale(104), denyTextW+scale(40))
	l.Allow = rect{l.W - pad - allowW, y, l.W - pad, y + buttonH}
	l.Deny = rect{l.Allow.Left - scale(10) - denyW, y, l.Allow.Left - scale(10), y + buttonH}
	l.H = l.Allow.Bottom + pad
	return l
}

// hit is the button under a point, or buttonNone.
func (l consentLayoutWin) hit(x, y int32) int {
	switch {
	case x >= l.Deny.Left && x < l.Deny.Right && y >= l.Deny.Top && y < l.Deny.Bottom:
		return buttonDeny
	case x >= l.Allow.Left && x < l.Allow.Right && y >= l.Allow.Top && y < l.Allow.Bottom:
		return buttonAllow
	}
	return buttonNone
}

// consentWindow is the state of the one prompt of this process; it is used on the window's thread only.
type consentWindow struct {
	ask      ConsentAskBody
	hwnd     uintptr
	layout   consentLayoutWin
	dpi      int32
	fonts    struct{ brand, title, tech, body, small, button uintptr }
	deadline time.Time
	total    time.Duration
	focus    int
	hover    int
	pressed  int
	answer   string
}

var (
	activeConsent     *consentWindow
	consentProcOnce   sync.Once
	consentWindowProc uintptr
)

func showConsentWindow(ctx context.Context, ask ConsentAskBody, in io.Reader) (string, error) {
	runtime.LockOSThread()
	defer runtime.UnlockOSThread()

	// Per-monitor DPI awareness (v2), or the system DPI on Windows before 10 1703, so the text is sharp and the sizes follow the scale.
	if procSetProcessDpiAwarenessContext.Find() != nil {
		procSetProcessDPIAware.Call()
	} else if ok, _, _ := procSetProcessDpiAwarenessContext.Call(dpiPerMonitorAwareV2); ok == 0 {
		procSetProcessDPIAware.Call()
	}
	dpi := int32(96)
	if procGetDpiForSystem.Find() == nil {
		if v, _, _ := procGetDpiForSystem.Call(); v != 0 {
			dpi = int32(v)
		}
	}

	w := &consentWindow{ask: ask, dpi: dpi, focus: buttonDeny, hover: buttonNone, pressed: buttonNone, answer: "refused"}
	w.total = time.Duration(ask.Seconds) * time.Second
	face, _ := windows.UTF16PtrFromString("Segoe UI")
	font := func(px, weight int32) uintptr {
		f, _, _ := procCreateFontW.Call(uintptr(-w.scale(px)), 0, 0, 0, uintptr(weight), 0, 0, 0, 1, 0, 0, 5, 0, uintptr(unsafe.Pointer(face)))
		return f
	}
	w.fonts.brand = font(12, fwSemiBold)
	w.fonts.title = font(19, fwSemiBold)
	w.fonts.tech = font(15, fwBold)
	w.fonts.body = font(14, fwNormal)
	w.fonts.small = font(12, fwNormal)
	w.fonts.button = font(14, fwSemiBold)
	defer func() {
		for _, f := range []uintptr{w.fonts.brand, w.fonts.title, w.fonts.tech, w.fonts.body, w.fonts.small, w.fonts.button} {
			if f != 0 {
				procDeleteObject.Call(f)
			}
		}
	}()

	inner := w.scale(460) - 2*w.scale(28)
	messageH := w.measure(w.fonts.body, ask.Texts.Message, inner, dtWordBreak|dtNoPrefix|dtEditControl)
	denyW := w.measureLine(w.fonts.button, ask.Texts.Deny)
	allowW := w.measureLine(w.fonts.button, ask.Texts.Allow)
	w.layout = layoutConsentWindow(w.scale, messageH, denyW, allowW)

	consentProcOnce.Do(func() { consentWindowProc = windows.NewCallback(consentWndProc) })
	instance, _, _ := procGetModuleHandleW.Call(0)
	className, _ := windows.UTF16PtrFromString("FleetoConsentPrompt")
	cursor, _, _ := procLoadCursorW.Call(0, idcArrow)
	class := wndClassEx{Style: csDropShadow | csHRedraw | csVRedraw, WndProc: consentWindowProc, Instance: instance, ClassName: className,
		Cursor: cursor}
	class.Size = uint32(unsafe.Sizeof(class))
	procRegisterClassExW.Call(uintptr(unsafe.Pointer(&class)))

	screenW, screenH := int32(systemMetric(smCXScreen)), int32(systemMetric(smCYScreen))
	x, y := max((screenW-w.layout.W)/2, 0), max((screenH-w.layout.H)/2, 0)
	title, _ := windows.UTF16PtrFromString(ask.Texts.Title)
	activeConsent = w
	defer func() { activeConsent = nil }()
	hwnd, _, err := procCreateWindowExW.Call(wsExTopmost|wsExAppWindow, uintptr(unsafe.Pointer(className)), uintptr(unsafe.Pointer(title)),
		wsPopup, uintptr(x), uintptr(y), uintptr(w.layout.W), uintptr(w.layout.H), 0, 0, instance, 0)
	if hwnd == 0 {
		return "", fmtCallError("CreateWindowEx", err)
	}
	w.hwnd = hwnd
	if procDwmSetWindowAttribute.Find() == nil {
		corner := uint32(dwmCornerRound)
		procDwmSetWindowAttribute.Call(hwnd, dwmaWindowCornerPreference, uintptr(unsafe.Pointer(&corner)), 4)
	}
	w.deadline = time.Now().Add(w.total)
	procShowWindow.Call(hwnd, swShow)
	procSetForegroundWindow.Call(hwnd)
	procSetTimer.Call(hwnd, consentTimer, 200, 0)

	// The agent closing the question (its context ended, or it stopped) closes the window; the answer is then never read.
	go func() {
		_, _ = io.Copy(io.Discard, in)
		procPostMessageW.Call(hwnd, wmClose, 0, 0)
	}()
	go func() {
		<-ctx.Done()
		procPostMessageW.Call(hwnd, wmClose, 0, 0)
	}()

	var m winMsg
	for {
		r, _, _ := procGetMessageW.Call(uintptr(unsafe.Pointer(&m)), 0, 0, 0)
		if int32(r) <= 0 {
			break
		}
		procTranslateMessage.Call(uintptr(unsafe.Pointer(&m)))
		procDispatchMessageW.Call(uintptr(unsafe.Pointer(&m)))
	}
	if ctx.Err() != nil {
		return "", ctx.Err()
	}
	if w.answer == "" {
		return "", errors.New("the consent window closed without an answer")
	}
	return w.answer, nil
}

func (w *consentWindow) scale(v int32) int32 { return v * w.dpi / 96 }

// measure is the height of a text wrapped to a width.
func (w *consentWindow) measure(font uintptr, text string, width int32, flags uintptr) int32 {
	r := rect{0, 0, width, 0}
	w.calc(font, text, &r, flags)
	return r.Bottom - r.Top
}

// measureLine is the width of one line.
func (w *consentWindow) measureLine(font uintptr, text string) int32 {
	r := rect{}
	w.calc(font, text, &r, dtSingleLine|dtNoPrefix)
	return r.Right - r.Left
}

func (w *consentWindow) calc(font uintptr, text string, r *rect, flags uintptr) {
	dc, _, _ := procGetDC.Call(0)
	if dc == 0 {
		return
	}
	defer procReleaseDC.Call(0, dc)
	old, _, _ := procSelectObject.Call(dc, font)
	defer procSelectObject.Call(dc, old)
	textUTF16, err := windows.UTF16FromString(text)
	if err != nil || len(textUTF16) < 2 {
		return
	}
	procDrawTextW.Call(dc, uintptr(unsafe.Pointer(&textUTF16[0])), uintptr(len(textUTF16)-1), uintptr(unsafe.Pointer(r)), flags|dtCalcRect)
}

// finish ends the prompt with an answer.
func (w *consentWindow) finish(answer string) {
	if w.hwnd == 0 {
		return
	}
	w.answer = answer
	procKillTimer.Call(w.hwnd, consentTimer)
	hwnd := w.hwnd
	w.hwnd = 0
	procDestroyWindow.Call(hwnd)
}

func (w *consentWindow) activate(button int) {
	switch button {
	case buttonAllow:
		w.finish("allowed")
	case buttonDeny:
		w.finish("refused")
	}
}

// consentWndProc serves the prompt. A panic here would unwind into Windows: it refuses instead.
func consentWndProc(hwnd, message, wParam, lParam uintptr) (result uintptr) {
	w := activeConsent
	defer func() {
		if r := recover(); r != nil {
			result = 0
			if w != nil {
				w.finish("refused")
			}
		}
	}()
	if w == nil || (w.hwnd != 0 && hwnd != w.hwnd) {
		r, _, _ := procDefWindowProcW.Call(hwnd, message, wParam, lParam)
		return r
	}
	x, y := int32(int16(lParam&0xFFFF)), int32(int16((lParam>>16)&0xFFFF))
	switch message {
	case wmEraseBkgnd:
		return 1
	case wmPaint:
		w.paint(hwnd)
		return 0
	case wmTimer:
		if wParam == consentTimer {
			if time.Now().After(w.deadline) {
				w.finish("timeout")
				return 0
			}
			// Stay on top of windows that were raised later, without taking the focus away.
			procSetWindowPos.Call(hwnd, hwndTopmost, 0, 0, 0, 0, swpNoMove|swpNoSize|swpNoActivate)
			procInvalidateRect.Call(hwnd, 0, 0)
		}
		return 0
	case wmSetCursor:
		if lParam&0xFFFF == 1 && w.hover != buttonNone { // HTCLIENT over a button
			hand, _, _ := procLoadCursorW.Call(0, idcHand)
			procSetCursor.Call(hand)
			return 1
		}
	case wmMouseMove:
		if hover := w.layout.hit(x, y); hover != w.hover {
			w.hover = hover
			procInvalidateRect.Call(hwnd, 0, 0)
		}
		return 0
	case wmLButtonDown:
		w.pressed = w.layout.hit(x, y)
		return 0
	case wmLButtonUp:
		if button := w.layout.hit(x, y); button != buttonNone && button == w.pressed {
			w.activate(button)
		}
		w.pressed = buttonNone
		return 0
	case wmKeyDown:
		switch wParam {
		case vkEscape, vkReturn:
			// Enter refuses whatever has the focus, so a stray Enter never allows a session.
			w.finish("refused")
		case vkTab, vkLeft, vkRight:
			w.focus = 1 - w.focus
			procInvalidateRect.Call(hwnd, 0, 0)
		}
		return 0
	case wmKeyUp:
		if wParam == vkSpace {
			w.activate(w.focus)
		}
		return 0
	case wmClose:
		w.finish("refused")
		return 0
	case wmDestroy:
		procPostQuitMessage.Call(0)
		return 0
	}
	r, _, _ := procDefWindowProcW.Call(hwnd, message, wParam, lParam)
	return r
}

func (w *consentWindow) paint(hwnd uintptr) {
	var ps paintStruct
	dc, _, _ := procBeginPaint.Call(hwnd, uintptr(unsafe.Pointer(&ps)))
	if dc == 0 {
		return
	}
	defer procEndPaint.Call(hwnd, uintptr(unsafe.Pointer(&ps)))
	l := w.layout
	// Drawn off screen first, so the countdown never flickers.
	mem, _, _ := procCreateCompatibleDC.Call(dc)
	if mem == 0 {
		return
	}
	defer procDeleteDC.Call(mem)
	bitmap, _, _ := procCreateCompatibleBitmap.Call(dc, uintptr(l.W), uintptr(l.H))
	if bitmap == 0 {
		return
	}
	defer procDeleteObject.Call(bitmap)
	oldBitmap, _, _ := procSelectObject.Call(mem, bitmap)
	defer procSelectObject.Call(mem, oldBitmap)
	procSetBkMode.Call(mem, transparent)

	fillRect(mem, rect{0, 0, l.W, l.H}, rgbBorder)
	fillRect(mem, rect{1, 1, l.W - 1, l.H - 1}, rgbWhite)
	fillRect(mem, l.Accent, rgbTeal)

	brand := l.Brand
	drawText(mem, w.fonts.brand, rgbTeal, "FLEETO", &brand, dtSingleLine|dtNoPrefix)
	title := l.Title
	drawText(mem, w.fonts.title, rgbInk, w.ask.Texts.Title, &title, dtSingleLine|dtNoPrefix|dtEndEllipsis)
	tech := l.Tech
	drawText(mem, w.fonts.tech, rgbInk, w.ask.Technician, &tech, dtSingleLine|dtNoPrefix|dtEndEllipsis)
	message := l.Message
	drawText(mem, w.fonts.body, rgbBody, w.ask.Texts.Message, &message, dtWordBreak|dtNoPrefix|dtEditControl)

	remaining := max(time.Until(w.deadline), 0)
	roundRect(mem, l.Bar, w.scale(6), rgbTealTint, rgbTealTint, 1)
	if width := int32(float64(l.Bar.Right-l.Bar.Left) * float64(remaining) / float64(max(w.total, time.Second))); width > w.scale(6) {
		roundRect(mem, rect{l.Bar.Left, l.Bar.Top, l.Bar.Left + width, l.Bar.Bottom}, w.scale(6), rgbTeal, rgbTeal, 1)
	}
	left := int((remaining + time.Second - 1) / time.Second)
	timeLeft := l.TimeLeft
	drawText(mem, w.fonts.small, rgbMuted, ConsentTimeLeft(w.ask.Texts, left), &timeLeft, dtSingleLine|dtNoPrefix)

	// Deny: outlined; Allow: filled teal. The button with the focus has a teal ring.
	denyFill := uint32(rgbWhite)
	if w.hover == buttonDeny {
		denyFill = rgbStoneHover
	}
	allowFill := uint32(rgbTeal)
	if w.hover == buttonAllow {
		allowFill = rgbTealHover
	}
	focused := l.Deny
	if w.focus == buttonAllow {
		focused = l.Allow
	}
	ring := w.scale(3)
	roundRect(mem, rect{focused.Left - ring, focused.Top - ring, focused.Right + ring, focused.Bottom + ring}, l.Radius+ring, 0, rgbTeal, w.scale(2))
	roundRect(mem, l.Deny, l.Radius, denyFill, rgbBorderDeep, 1)
	roundRect(mem, l.Allow, l.Radius, allowFill, allowFill, 1)
	deny := l.Deny
	drawText(mem, w.fonts.button, rgbInk, w.ask.Texts.Deny, &deny, dtCenter|dtVCenter|dtSingleLine|dtNoPrefix|dtEndEllipsis)
	allow := l.Allow
	drawText(mem, w.fonts.button, rgbWhite, w.ask.Texts.Allow, &allow, dtCenter|dtVCenter|dtSingleLine|dtNoPrefix|dtEndEllipsis)

	procBitBlt.Call(dc, 0, 0, uintptr(l.W), uintptr(l.H), mem, 0, 0, srcCopy)
}

func fillRect(dc uintptr, r rect, color uint32) {
	brush, _, _ := procCreateSolidBrush.Call(uintptr(color))
	if brush == 0 {
		return
	}
	procFillRect.Call(dc, uintptr(unsafe.Pointer(&r)), brush)
	procDeleteObject.Call(brush)
}

// roundRect draws a rounded rectangle with a border of the given width; fill 0 with a border other than black draws the border only.
func roundRect(dc uintptr, r rect, radius int32, fill, border uint32, width int32) {
	pen, _, _ := procCreatePen.Call(0, uintptr(max(width, 1)), uintptr(border))
	var brush uintptr
	if fill == 0 {
		brush, _, _ = procGetStockObject.Call(nullBrush)
	} else {
		brush, _, _ = procCreateSolidBrush.Call(uintptr(fill))
	}
	oldPen, _, _ := procSelectObject.Call(dc, pen)
	oldBrush, _, _ := procSelectObject.Call(dc, brush)
	procRoundRect.Call(dc, uintptr(r.Left), uintptr(r.Top), uintptr(r.Right), uintptr(r.Bottom), uintptr(radius), uintptr(radius))
	procSelectObject.Call(dc, oldBrush)
	procSelectObject.Call(dc, oldPen)
	procDeleteObject.Call(pen)
	if fill != 0 {
		procDeleteObject.Call(brush)
	}
}

func drawText(dc, font uintptr, color uint32, text string, r *rect, flags uintptr) {
	textUTF16, err := windows.UTF16FromString(text)
	if err != nil || len(textUTF16) < 2 {
		return
	}
	old, _, _ := procSelectObject.Call(dc, font)
	procSetTextColor.Call(dc, uintptr(color))
	procDrawTextW.Call(dc, uintptr(unsafe.Pointer(&textUTF16[0])), uintptr(len(textUTF16)-1), uintptr(unsafe.Pointer(r)), flags)
	procSelectObject.Call(dc, old)
}
