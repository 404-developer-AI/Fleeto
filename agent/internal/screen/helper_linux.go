//go:build linux

package screen

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"sync"
	"time"

	"github.com/jezek/xgb"
	"github.com/jezek/xgb/randr"
	"github.com/jezek/xgb/xfixes"
	"github.com/jezek/xgb/xproto"
	"github.com/jezek/xgb/xtest"
)

// The remote control helper on X11 (0.3.0 step 6). Started by the agent as root, it drops to nobody (startX11Child), then serves the
// screen of the console display: capture (GetImage with the cursor from XFIXES drawn in), change detection and tiles as on Windows,
// monitors from RandR, mouse and keyboard through XTEST with the keyboard plan of keys.go, and the banner. H.264 is Windows only.

const (
	// bannerRaise is how often the banner is put on top again.
	bannerRaise = 2 * time.Second
	// mappingRefresh is how old the keyboard mapping may be before a key reads it again (the user may switch layouts).
	mappingRefresh = time.Second
	// spareSettle lets clients read a changed keyboard mapping before the key that uses it arrives.
	spareSettle = 25 * time.Millisecond
)

// RunHelper serves one remote control session on the X display the agent names in its first frame, until in closes.
func RunHelper(ctx context.Context, in io.Reader, out io.Writer, _ uint32, logger *slog.Logger) error {
	conn, body, err := startX11Child(in)
	if err != nil {
		return err
	}
	defer conn.Close()

	var writeMu sync.Mutex
	write := func(frame []byte) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		return WriteFrame(out, frame)
	}
	h, err := newXHelper(conn, write, logger, body)
	if err != nil {
		data, _ := json.Marshal(NoticeBody{Message: "The screen of this endpoint cannot be shown: " + err.Error()})
		_ = write(append([]byte{FrameNotice}, data...))
		return err
	}
	defer h.cleanup()

	frames := make(chan []byte, 64)
	readErr := make(chan error, 1)
	go func() {
		for {
			frame, err := ReadFrame(in)
			if err != nil {
				readErr <- err
				close(frames)
				return
			}
			frames <- frame
		}
	}()
	events := make(chan xgb.Event, 64)
	go func() {
		for {
			ev, xerr := conn.WaitForEvent()
			if ev == nil && xerr == nil {
				close(events) // the connection closed
				return
			}
			if ev != nil {
				events <- ev
			}
		}
	}()

	err = h.loop(ctx, frames, events)
	logger.Info("the remote control helper is stopping", "error", err)
	select {
	case readErr := <-readErr:
		if errors.Is(readErr, io.EOF) {
			return nil
		}
		return readErr
	default:
	}
	return err
}

type xHelper struct {
	conn     *xgb.Conn
	write    func([]byte) error
	logger   *slog.Logger
	setup    *xproto.SetupInfo
	screen   *xproto.ScreenInfo
	root     xproto.Window
	cursor   bool
	monitors []Monitor
	randr    bool
	ui       *xui
	banner   xBanner
	names    []string
	keyboard *Keyboard
	layout   *xLayout
	layoutAt time.Time
	// spares are keycodes this helper mapped to a character (Unicode fallback), to give back when it stops.
	spares    map[uint8]rune
	spareNext int

	// width and height are the size of the X screen now (it changes with the resolution; the setup keeps the size at connection).
	width, height int

	selected    int
	started     bool
	monitorsAt  time.Time
	encoder     Encoder
	forceFull   bool
	frame       uint32
	awaiting    bool
	sentAt      time.Time
	sentBytes   int
	lastCapture time.Time
	buttons     int
	img         Image
}

func newXHelper(conn *xgb.Conn, write func([]byte) error, logger *slog.Logger, body X11Body) (*xHelper, error) {
	setup := xproto.Setup(conn)
	screen := setup.DefaultScreen(conn)
	if err := checkPixelFormat(setup, screen); err != nil {
		return nil, err
	}
	if err := xtest.Init(conn); err != nil {
		return nil, errors.New("the X server has no XTEST extension, so the mouse and keyboard cannot be used")
	}
	h := &xHelper{conn: conn, write: write, logger: logger, setup: setup, screen: screen, root: screen.Root, keyboard: NewKeyboard(),
		spares: map[uint8]rune{}, selected: -2}
	if xfixes.Init(conn) == nil {
		if _, err := xfixes.QueryVersion(conn, 4, 0).Reply(); err == nil {
			h.cursor = true
		}
	}
	if randr.Init(conn) == nil {
		if v, err := randr.QueryVersion(conn, 1, 5).Reply(); err == nil && (v.MajorVersion > 1 || v.MinorVersion >= 5) {
			h.randr = true
		}
	}
	if ui, err := newXUI(conn, screen); err == nil {
		h.ui = ui
		h.banner.ui = ui
	} else {
		logger.Warn("the banner cannot be drawn", "error", err)
		data, _ := json.Marshal(NoticeBody{Message: "This endpoint could not draw the banner on its screen. The screen, mouse and keyboard work."})
		_ = write(append([]byte{FrameNotice}, data...))
	}
	h.refreshMonitors(true)
	logger.Info("remote control helper serves an X display", "display", body.Display, "session", body.Session,
		"width", screen.WidthInPixels, "height", screen.HeightInPixels, "cursor", h.cursor, "randr", h.randr)
	return h, nil
}

// checkPixelFormat accepts the only format the tiles read: 32 bits a pixel, blue first (a 24-bit TrueColor screen of a little-endian
// server, which is what Xorg gives on every PC).
func checkPixelFormat(setup *xproto.SetupInfo, screen *xproto.ScreenInfo) error {
	if setup.ImageByteOrder != xproto.ImageOrderLSBFirst {
		return errors.New("the X server sends images in an order this endpoint does not read")
	}
	for _, f := range setup.PixmapFormats {
		if f.Depth == screen.RootDepth {
			if f.BitsPerPixel == 32 && (screen.RootDepth == 24 || screen.RootDepth == 32) {
				return nil
			}
			return fmt.Errorf("the X screen has %d-bit color, and remote control needs 24-bit color", screen.RootDepth)
		}
	}
	return errors.New("the X screen has an unknown pixel format")
}

func (h *xHelper) loop(ctx context.Context, frames <-chan []byte, events <-chan xgb.Event) error {
	timer := time.NewTimer(0)
	defer timer.Stop()
	raise := time.NewTicker(bannerRaise)
	defer raise.Stop()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case frame, ok := <-frames:
			if !ok {
				return nil
			}
			h.handle(frame)
		case ev, ok := <-events:
			if !ok {
				return errors.New("the X display closed")
			}
			h.event(ev)
			continue
		case <-raise.C:
			h.banner.keepOnTop()
			continue
		case <-timer.C:
		}
		wait := idlePoll
		if h.started {
			wait = h.maybeCapture()
		}
		if !timer.Stop() {
			select {
			case <-timer.C:
			default:
			}
		}
		timer.Reset(wait)
	}
}

func (h *xHelper) event(ev xgb.Event) {
	switch e := ev.(type) {
	case xproto.ExposeEvent:
		if e.Window == h.banner.win && e.Count == 0 {
			h.banner.draw()
		}
	case xproto.MappingNotifyEvent:
		h.layout = nil
	case randr.ScreenChangeNotifyEvent:
		h.refreshMonitors(true)
	}
}

func (h *xHelper) cleanup() {
	h.inject(h.keyboard.ReleaseAll())
	h.pointer(PointerBody{X: -1, Y: -1, Buttons: 0})
	h.restoreSpares()
	h.banner.destroy()
	// A round trip, so everything above reaches the server before the connection closes.
	_, _ = xproto.GetInputFocus(h.conn).Reply()
}

func (h *xHelper) handle(frame []byte) {
	if len(frame) == 0 {
		return
	}
	body := frame[1:]
	switch frame[0] {
	case FrameStart:
		var start StartBody
		if json.Unmarshal(body, &start) != nil {
			return
		}
		h.refreshMonitors(true)
		h.selected = h.validMonitor(start.Monitor)
		h.started = true
		h.forceFull = true
		h.awaiting = false
		h.sendInfo()
		h.showBanner()
	case FrameAck:
		var ack AckBody
		if json.Unmarshal(body, &ack) == nil && h.awaiting && ack.Frame == h.frame {
			h.awaiting = false
			quality := DefaultQuality
			if h.sentBytes > largeFrameBytes && time.Since(h.sentAt) > slowAck {
				quality = LowQuality
			}
			h.encoder.Quality = quality
		}
	case FramePointer:
		var p PointerBody
		if json.Unmarshal(body, &p) == nil {
			h.pointer(p)
		}
	case FrameKey:
		var k KeyBody
		if json.Unmarshal(body, &k) == nil {
			h.inject(h.keyboard.Key(k, h.currentLayout()))
		}
	case FrameType:
		var t TypeBody
		if json.Unmarshal(body, &t) == nil {
			h.inject(h.keyboard.Type(t.Text, h.currentLayout()))
		}
	case FrameReleaseKeys:
		h.inject(h.keyboard.ReleaseAll())
		if h.buttons != 0 {
			h.pointer(PointerBody{X: -1, Y: -1, Buttons: 0})
		}
	case FrameBanner:
		var banner BannerBody
		if json.Unmarshal(body, &banner) == nil {
			h.names = banner.Names
			h.showBanner()
		}
	}
}

func (h *xHelper) showBanner() {
	if h.ui == nil {
		return
	}
	if err := h.banner.set(h.names, h.primary()); err != nil {
		h.logger.Warn("could not show the banner", "error", err)
	}
}

// refreshMonitors reads the monitors from RandR, or takes the whole screen as one.
func (h *xHelper) refreshMonitors(force bool) {
	if !force && time.Since(h.monitorsAt) < monitorRefresh {
		return
	}
	h.monitorsAt = time.Now()
	h.width, h.height = int(h.screen.WidthInPixels), int(h.screen.HeightInPixels)
	if geometry, err := xproto.GetGeometry(h.conn, xproto.Drawable(h.root)).Reply(); err == nil {
		h.width, h.height = int(geometry.Width), int(geometry.Height)
	}
	found := h.readMonitors()
	changed := len(found) != len(h.monitors)
	for i := 0; !changed && i < len(found); i++ {
		changed = found[i] != h.monitors[i]
	}
	h.monitors = found
	if changed && h.started && !force {
		h.selected = h.validMonitor(h.selected)
		h.forceFull = true
		h.sendInfo()
		h.showBanner()
	}
}

func (h *xHelper) readMonitors() []Monitor {
	whole := Monitor{Index: 0, Name: "Screen", Width: h.width, Height: h.height, Primary: true}
	if !h.randr {
		return []Monitor{whole}
	}
	reply, err := randr.GetMonitors(h.conn, h.root, true).Reply()
	if err != nil || len(reply.Monitors) == 0 {
		return []Monitor{whole}
	}
	var out []Monitor
	primary := false
	for i, m := range reply.Monitors {
		name := fmt.Sprintf("Monitor %d", i+1)
		if atom, err := xproto.GetAtomName(h.conn, m.Name).Reply(); err == nil && atom.Name != "" {
			name = atom.Name
		}
		out = append(out, Monitor{Index: i, Name: name, X: int(m.X), Y: int(m.Y), Width: int(m.Width), Height: int(m.Height), Primary: m.Primary})
		primary = primary || m.Primary
	}
	if !primary {
		out[0].Primary = true
	}
	return out
}

func (h *xHelper) validMonitor(requested int) int {
	if requested == -1 && len(h.monitors) > 1 {
		return -1
	}
	if requested >= 0 && requested < len(h.monitors) {
		return requested
	}
	for _, m := range h.monitors {
		if m.Primary {
			return m.Index
		}
	}
	return 0
}

func (h *xHelper) primary() Monitor {
	for _, m := range h.monitors {
		if m.Primary {
			return m
		}
	}
	return Monitor{Width: h.width, Height: h.height}
}

// area is the rectangle shown, in pixels of the X screen.
func (h *xHelper) area() Monitor {
	if h.selected >= 0 && h.selected < len(h.monitors) {
		return h.monitors[h.selected]
	}
	return Monitor{Index: -1, Width: h.width, Height: h.height}
}

func (h *xHelper) sendInfo() {
	area := h.area()
	info := InfoBody{Monitors: h.monitors, Monitor: h.selected, Width: area.Width, Height: area.Height, Codec: CodecTiles, Capture: "x11"}
	if info.Monitors == nil {
		info.Monitors = []Monitor{}
	}
	data, _ := json.Marshal(info)
	_ = h.write(append([]byte{FrameInfo}, data...))
}

func (h *xHelper) maybeCapture() time.Duration {
	h.refreshMonitors(false)
	now := time.Now()
	if h.awaiting {
		if now.Sub(h.sentAt) < ackTimeout {
			return captureInterval
		}
		h.awaiting = false
		h.forceFull = true
	}
	if wait := captureInterval - now.Sub(h.lastCapture); wait > 0 {
		return wait
	}
	h.lastCapture = now
	area := h.area()
	if area.Width <= 0 || area.Height <= 0 {
		return idlePoll
	}
	img, err := h.grab(area)
	if err != nil {
		h.logger.Debug("could not capture the X screen", "error", err)
		return idlePoll
	}
	tiles, err := h.encoder.Encode(img, h.forceFull)
	if err != nil || (len(tiles) == 0 && !h.forceFull) {
		return captureInterval
	}
	h.forceFull = false
	h.frame++
	updates, err := Updates(h.frame, tiles)
	if err != nil {
		h.encoder.Reset()
		return captureInterval
	}
	total := 0
	for _, update := range updates {
		if err := h.write(update); err != nil {
			return idlePoll
		}
		total += len(update)
	}
	h.awaiting = true
	h.sentAt = now
	h.sentBytes = total
	return captureInterval
}

// grab captures the area of the root window and draws the cursor into it.
func (h *xHelper) grab(area Monitor) (*Image, error) {
	reply, err := xproto.GetImage(h.conn, xproto.ImageFormatZPixmap, xproto.Drawable(h.root), int16(area.X), int16(area.Y),
		uint16(area.Width), uint16(area.Height), 0xFFFFFFFF).Reply()
	if err != nil {
		return nil, err
	}
	size := area.Width * area.Height * 4
	if len(reply.Data) < size {
		return nil, errors.New("the X server sent a short image")
	}
	h.img.Width, h.img.Height = area.Width, area.Height
	h.img.Pix = reply.Data[:size]
	if h.cursor {
		h.drawCursor(area)
	}
	return &h.img, nil
}

// drawCursor blends the cursor image (premultiplied ARGB) into the captured image.
func (h *xHelper) drawCursor(area Monitor) {
	c, err := xfixes.GetCursorImage(h.conn).Reply()
	if err != nil || c.Width == 0 || c.Height == 0 || len(c.CursorImage) < int(c.Width)*int(c.Height) {
		return
	}
	left := int(c.X) - int(c.Xhot) - area.X
	top := int(c.Y) - int(c.Yhot) - area.Y
	img := &h.img
	for cy := 0; cy < int(c.Height); cy++ {
		y := top + cy
		if y < 0 || y >= img.Height {
			continue
		}
		for cx := 0; cx < int(c.Width); cx++ {
			x := left + cx
			if x < 0 || x >= img.Width {
				continue
			}
			argb := c.CursorImage[cy*int(c.Width)+cx]
			a := argb >> 24
			if a == 0 {
				continue
			}
			p := (y*img.Width + x) * 4
			inv := 255 - a
			img.Pix[p] = byte(argb&0xFF + uint32(img.Pix[p])*inv/255)
			img.Pix[p+1] = byte((argb>>8)&0xFF + uint32(img.Pix[p+1])*inv/255)
			img.Pix[p+2] = byte((argb>>16)&0xFF + uint32(img.Pix[p+2])*inv/255)
		}
	}
}

// pointer moves the mouse and presses or releases the buttons that changed. X and Y are pixels of the shown image; a negative position
// only changes the buttons. Wheel notches are buttons 4 to 7 on X11.
func (h *xHelper) pointer(p PointerBody) {
	if p.X >= 0 && p.Y >= 0 {
		area := h.area()
		x := min(max(p.X, 0), max(area.Width-1, 0)) + area.X
		y := min(max(p.Y, 0), max(area.Height-1, 0)) + area.Y
		xtest.FakeInput(h.conn, xproto.MotionNotify, 0, 0, h.root, int16(x), int16(y), 0)
	}
	for _, b := range []struct {
		mask   int
		button byte
	}{{1, 1}, {2, 3}, {4, 2}} {
		was, is := h.buttons&b.mask != 0, p.Buttons&b.mask != 0
		if was != is {
			kind := byte(xproto.ButtonRelease)
			if is {
				kind = xproto.ButtonPress
			}
			xtest.FakeInput(h.conn, kind, b.button, 0, h.root, 0, 0, 0)
		}
	}
	h.buttons = p.Buttons & 7
	h.wheel(clampWheel(p.Wheel), 4, 5)
	h.wheel(clampWheel(p.HWheel), 7, 6)
}

// wheel clicks the wheel button for each notch: positive (away from the user, or right) is the first button.
func (h *xHelper) wheel(steps int, positive, negative byte) {
	button := positive
	if steps < 0 {
		button, steps = negative, -steps
	}
	for i := 0; i < steps; i++ {
		xtest.FakeInput(h.conn, xproto.ButtonPress, button, 0, h.root, 0, 0, 0)
		xtest.FakeInput(h.conn, xproto.ButtonRelease, button, 0, h.root, 0, 0, 0)
	}
}

func clampWheel(steps int) int { return min(max(steps, -10), 10) }

// currentLayout is the keyboard mapping of the X server for the active group, read again when it may have changed.
func (h *xHelper) currentLayout() Layout {
	if h.layout != nil && time.Since(h.layoutAt) < mappingRefresh {
		return h.layout
	}
	first, last := h.setup.MinKeycode, h.setup.MaxKeycode
	reply, err := xproto.GetKeyboardMapping(h.conn, first, byte(last-first+1)).Reply()
	if err != nil {
		if h.layout != nil {
			return h.layout
		}
		return newXLayout(uint8(first), 1, nil, 0)
	}
	group := 0
	if pointer, err := xproto.QueryPointer(h.conn, h.root).Reply(); err == nil {
		group = int(pointer.Mask>>13) & 3
	}
	syms := make([]uint32, len(reply.Keysyms))
	for i, ks := range reply.Keysyms {
		syms[i] = uint32(ks)
	}
	h.layout = newXLayout(uint8(first), int(reply.KeysymsPerKeycode), syms, min(group, 1))
	h.layoutAt = time.Now()
	return h.layout
}

// inject sends planned keyboard events through XTEST.
func (h *xHelper) inject(planned []Input) {
	for _, in := range planned {
		switch {
		case in.Unicode != 0:
			h.unicode(in.Unicode, in.Up)
		case in.VK != 0:
			h.key(uint8(in.VK), !in.Up)
		default:
			if code, ok := xKeycode(in.Scan); ok {
				h.key(code, !in.Up)
			}
		}
	}
}

func (h *xHelper) key(code uint8, down bool) {
	kind := byte(xproto.KeyRelease)
	if down {
		kind = xproto.KeyPress
	}
	xtest.FakeInput(h.conn, kind, code, 0, h.root, 0, 0, 0)
}

// unicode types a character no key has: a spare keycode is mapped to it, and pressed once clients read the new mapping.
func (h *xHelper) unicode(r rune, up bool) {
	for code, mapped := range h.spares {
		if mapped == r {
			h.key(code, !up)
			return
		}
	}
	if up {
		return
	}
	layout, ok := h.currentLayout().(*xLayout)
	if !ok {
		return
	}
	free := layout.spareKeycodes()
	var candidates []uint8
	for _, code := range free {
		if _, used := h.spares[code]; !used {
			candidates = append(candidates, code)
		}
	}
	for code := range h.spares {
		candidates = append(candidates, code)
	}
	if len(candidates) == 0 {
		h.logger.Debug("no spare keycode to type a character with")
		return
	}
	code := candidates[h.spareNext%len(candidates)]
	h.spareNext++
	per := max(layout.perCode, 1)
	syms := make([]xproto.Keysym, per)
	for i := range syms {
		syms[i] = xproto.Keysym(runeKeysym(r))
	}
	if err := xproto.ChangeKeyboardMappingChecked(h.conn, 1, xproto.Keycode(code), byte(per), syms).Check(); err != nil {
		h.logger.Debug("could not map a spare keycode", "error", err)
		return
	}
	h.spares[code] = r
	h.layout = nil
	time.Sleep(spareSettle)
	h.key(code, true)
}

// restoreSpares gives the keycodes this helper mapped back their empty mapping.
func (h *xHelper) restoreSpares() {
	if len(h.spares) == 0 {
		return
	}
	per := 1
	if h.layout != nil {
		per = max(h.layout.perCode, 1)
	}
	empty := make([]xproto.Keysym, per)
	for code := range h.spares {
		xproto.ChangeKeyboardMapping(h.conn, 1, xproto.Keycode(code), byte(per), empty)
	}
	h.spares = map[uint8]rune{}
}
