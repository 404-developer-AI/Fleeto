//go:build linux

package screen

import (
	"errors"
	"unicode/utf16"

	"github.com/jezek/xgb"
	"github.com/jezek/xgb/randr"
	"github.com/jezek/xgb/shape"
	"github.com/jezek/xgb/xproto"
)

// Windows of remote control on X11 (0.3.0 step 6): the banner that names the technicians and the consent prompt. Both are drawn with the
// core X protocol and a core font, which every X server has, so nothing else has to be installed on the endpoint.

// Colors of the brand: primary teal #0F766E with white text; the consent prompt uses stone neutrals.
var (
	colorTeal      = [3]uint8{0x0F, 0x76, 0x6E}
	colorWhite     = [3]uint8{0xFF, 0xFF, 0xFF}
	colorStone50   = [3]uint8{0xFA, 0xFA, 0xF9}
	colorStone300  = [3]uint8{0xD6, 0xD3, 0xD1}
	colorStone900  = [3]uint8{0x1C, 0x19, 0x17}
	fontCandidates = []string{
		"-misc-fixed-medium-r-normal--18-*-*-*-*-*-iso10646-1",
		"-misc-fixed-medium-r-normal--15-*-*-*-*-*-iso10646-1",
		"-*-fixed-medium-r-*-*-14-*-*-*-*-*-iso10646-1",
		"fixed",
	}
)

// xui draws text on windows of one screen.
type xui struct {
	conn    *xgb.Conn
	screen  *xproto.ScreenInfo
	font    xproto.Font
	charW   int
	ascent  int
	descent int
}

func newXUI(conn *xgb.Conn, screen *xproto.ScreenInfo) (*xui, error) {
	font, err := xproto.NewFontId(conn)
	if err != nil {
		return nil, err
	}
	for _, name := range fontCandidates {
		if xproto.OpenFontChecked(conn, font, uint16(len(name)), name).Check() != nil {
			continue
		}
		info, err := xproto.QueryFont(conn, xproto.Fontable(font)).Reply()
		if err != nil {
			xproto.CloseFont(conn, font)
			continue
		}
		return &xui{conn: conn, screen: screen, font: font, charW: max(int(info.MaxBounds.CharacterWidth), 6),
			ascent: int(info.FontAscent), descent: int(info.FontDescent)}, nil
	}
	return nil, errors.New("the X server has no core font to draw with")
}

// color allocates a color in the default colormap.
func (u *xui) color(c [3]uint8) uint32 {
	reply, err := xproto.AllocColor(u.conn, u.screen.DefaultColormap, uint16(c[0])*257, uint16(c[1])*257, uint16(c[2])*257).Reply()
	if err != nil {
		if c == colorWhite || c == colorStone50 {
			return u.screen.WhitePixel
		}
		return u.screen.BlackPixel
	}
	return reply.Pixel
}

// gc creates a graphics context that draws text in fg on bg with the font.
func (u *xui) gc(drawable xproto.Drawable, fg, bg uint32) (xproto.Gcontext, error) {
	gc, err := xproto.NewGcontextId(u.conn)
	if err != nil {
		return 0, err
	}
	xproto.CreateGC(u.conn, gc, drawable, xproto.GcForeground|xproto.GcBackground|xproto.GcFont, []uint32{fg, bg, uint32(u.font)})
	return gc, nil
}

// text draws one line with its baseline at y; characters outside the Basic Multilingual Plane show as "?".
func (u *xui) text(drawable xproto.Drawable, gc xproto.Gcontext, x, y int, s string) {
	chars := toChar2b(s)
	for len(chars) > 0 {
		n := min(len(chars), 255)
		xproto.ImageText16(u.conn, byte(n), drawable, gc, int16(x), int16(y), chars[:n])
		x += n * u.charW
		chars = chars[n:]
	}
}

func (u *xui) width(s string) int { return len(toChar2b(s)) * u.charW }

func (u *xui) lineHeight() int { return u.ascent + u.descent }

func toChar2b(s string) []xproto.Char2b {
	var out []xproto.Char2b
	for _, r := range s {
		if r > 0xFFFF || utf16.IsSurrogate(r) {
			r = '?'
		}
		out = append(out, xproto.Char2b{Byte1: byte(r >> 8), Byte2: byte(r)})
	}
	return out
}

// createPopup creates an override-redirect window (no window manager frame, on top of everything) with a background color.
func (u *xui) createPopup(x, y, w, h int, background uint32, events uint32) (xproto.Window, error) {
	win, err := xproto.NewWindowId(u.conn)
	if err != nil {
		return 0, err
	}
	err = xproto.CreateWindowChecked(u.conn, 0, win, u.screen.Root, int16(x), int16(y), uint16(max(w, 1)), uint16(max(h, 1)), 0,
		xproto.WindowClassInputOutput, 0, xproto.CwBackPixel|xproto.CwOverrideRedirect|xproto.CwEventMask,
		[]uint32{background, 1, events}).Check()
	if err != nil {
		return 0, err
	}
	return win, nil
}

// primaryMonitorOf is the primary monitor from RandR, or the whole screen.
func primaryMonitorOf(conn *xgb.Conn, screen *xproto.ScreenInfo) Monitor {
	whole := Monitor{Width: int(screen.WidthInPixels), Height: int(screen.HeightInPixels), Primary: true}
	if randr.Init(conn) != nil {
		return whole
	}
	reply, err := randr.GetMonitors(conn, screen.Root, true).Reply()
	if err != nil || len(reply.Monitors) == 0 {
		return whole
	}
	chosen := reply.Monitors[0]
	for _, m := range reply.Monitors {
		if m.Primary {
			chosen = m
			break
		}
	}
	return Monitor{X: int(chosen.X), Y: int(chosen.Y), Width: int(chosen.Width), Height: int(chosen.Height), Primary: true}
}

func (u *xui) raise(win xproto.Window) {
	xproto.ConfigureWindow(u.conn, win, xproto.ConfigWindowStackMode, []uint32{xproto.StackModeAbove})
}

// xBanner is the banner at the top of the primary monitor that names the technicians. Clicks go through it.
type xBanner struct {
	ui    *xui
	win   xproto.Window
	gc    xproto.Gcontext
	text  string
	shown bool
}

const bannerPadding = 8

// set shows the banner with these names on the monitor, or hides it for none.
func (b *xBanner) set(names []string, monitor Monitor) error {
	text := bannerText(names)
	if text == "" {
		if b.shown {
			xproto.UnmapWindow(b.ui.conn, b.win)
			b.shown = false
		}
		b.text = ""
		return nil
	}
	w := b.ui.width(text) + 2*bannerPadding
	h := b.ui.lineHeight() + bannerPadding
	x := monitor.X + max((monitor.Width-w)/2, 0)
	y := monitor.Y
	if b.win == 0 {
		teal, white := b.ui.color(colorTeal), b.ui.color(colorWhite)
		win, err := b.ui.createPopup(x, y, w, h, teal, xproto.EventMaskExposure)
		if err != nil {
			return err
		}
		b.win = win
		// An empty input shape: the banner never takes a click from the person at the endpoint or the technician.
		if shape.Init(b.ui.conn) == nil {
			shape.Rectangles(b.ui.conn, shape.SoSet, shape.SkInput, xproto.ClipOrderingUnsorted, b.win, 0, 0, nil)
		}
		if b.gc, err = b.ui.gc(xproto.Drawable(b.win), white, teal); err != nil {
			return err
		}
	} else {
		xproto.ConfigureWindow(b.ui.conn, b.win, xproto.ConfigWindowX|xproto.ConfigWindowY|xproto.ConfigWindowWidth|xproto.ConfigWindowHeight,
			[]uint32{uint32(int32(x)), uint32(int32(y)), uint32(w), uint32(h)})
	}
	b.text = text
	if !b.shown {
		xproto.MapWindow(b.ui.conn, b.win)
		b.shown = true
	}
	b.ui.raise(b.win)
	b.draw()
	return nil
}

func (b *xBanner) draw() {
	if !b.shown || b.text == "" {
		return
	}
	xproto.ClearArea(b.ui.conn, false, b.win, 0, 0, 0, 0)
	b.ui.text(xproto.Drawable(b.win), b.gc, bannerPadding, bannerPadding/2+b.ui.ascent, b.text)
}

// keepOnTop raises the banner over windows that were raised since.
func (b *xBanner) keepOnTop() {
	if b.shown {
		b.ui.raise(b.win)
		// Drawn again too: an Expose event may have been dropped while the helper was busy.
		b.draw()
	}
}

func (b *xBanner) destroy() {
	if b.win != 0 {
		xproto.DestroyWindow(b.ui.conn, b.win)
		b.win = 0
	}
	b.shown = false
}
