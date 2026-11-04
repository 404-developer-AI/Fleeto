//go:build linux

package screen

import (
	"context"
	"errors"
	"io"
	"log/slog"
	"time"

	"github.com/jezek/xgb"
	"github.com/jezek/xgb/xproto"
)

// The consent prompt on X11 (0.3.0 step 6, restyled in 0.8.0), run by "fleeto-agent remote-consent" as the user of the session: a white
// card in the middle of the primary monitor in the style of Fleeto, with a teal bar on top, the title, the technician, the message of the
// policy, a teal bar that counts down, and Deny and Allow. Deny is the default, as on Windows: Enter and Escape refuse. Without an answer
// in time, access is granted (decided 2026-09-16).

const (
	consentWidthChars = 52
	consentMargin     = 24
	consentButtonMinW = 110
	consentButtonH    = 34
	consentAccentH    = 5
	consentBarH       = 6
	escapeKeysym      = 0xFF1B
	returnKeysym      = 0xFF0D
	kpEnterKeysym     = 0xFF8D
)

var (
	colorTealTint = [3]uint8{0xCC, 0xFB, 0xF1}
	colorStone500 = [3]uint8{0x78, 0x71, 0x6C}
	colorStone700 = [3]uint8{0x44, 0x40, 0x3C}
)

// RunConsent asks the person at the screen and writes the answer.
func RunConsent(ctx context.Context, in io.Reader, out io.Writer, logger *slog.Logger) error {
	conn, _, err := startX11Child(in)
	if err != nil {
		return err
	}
	defer conn.Close()
	ask, err := readConsentAsk(in)
	if err != nil {
		return err
	}
	answer, err := showConsent(ctx, conn, ask)
	if err != nil {
		logger.Warn("the consent prompt could not be shown", "error", err)
		return err
	}
	return writeConsentAnswer(out, answer)
}

// consentLayout is where the parts of the prompt stand, in pixels of the window.
type consentLayout struct {
	w, h          int
	line          int
	titleY, techY int
	messageY      int
	message       []string
	barY, leftY   int
	deny, allow   xproto.Rectangle
}

func layoutConsent(ui *xui, ask ConsentAskBody) consentLayout {
	l := consentLayout{line: ui.lineHeight() + 4}
	l.message = wrapText(ask.Texts.Message, consentWidthChars)
	l.w = consentWidthChars*ui.charW + 2*consentMargin
	y := consentAccentH + consentMargin
	l.titleY = y + ui.ascent
	y += l.line + 6
	l.techY = y + ui.ascent
	y += l.line
	l.messageY = y + ui.ascent
	y += len(l.message)*l.line + 14
	l.barY = y
	y += consentBarH + 6
	l.leftY = y + ui.ascent
	y += l.line + 16
	denyW := max(consentButtonMinW, ui.width(ask.Texts.Deny)+32)
	allowW := max(consentButtonMinW, ui.width(ask.Texts.Allow)+32)
	l.allow = xproto.Rectangle{X: int16(l.w - consentMargin - allowW), Y: int16(y), Width: uint16(allowW), Height: consentButtonH}
	l.deny = xproto.Rectangle{X: l.allow.X - 10 - int16(denyW), Y: int16(y), Width: uint16(denyW), Height: consentButtonH}
	l.h = y + consentButtonH + consentMargin
	return l
}

func showConsent(ctx context.Context, conn *xgb.Conn, ask ConsentAskBody) (string, error) {
	setup := xproto.Setup(conn)
	screen := setup.DefaultScreen(conn)
	ui, err := newXUI(conn, screen)
	if err != nil {
		return "", err
	}
	l := layoutConsent(ui, ask)
	area := primaryMonitorOf(conn, screen)
	x, y := area.X+(area.Width-l.w)/2, area.Y+(area.Height-l.h)/2

	white, ink, body, muted := ui.color(colorWhite), ui.color(colorStone900), ui.color(colorStone700), ui.color(colorStone500)
	border, teal, tint := ui.color(colorStone300), ui.color(colorTeal), ui.color(colorTealTint)
	win, err := ui.createPopup(x, y, l.w, l.h, white, xproto.EventMaskExposure|xproto.EventMaskButtonPress|xproto.EventMaskKeyPress)
	if err != nil {
		return "", err
	}
	defer xproto.DestroyWindow(conn, win)
	d := xproto.Drawable(win)
	title, _ := ui.gc(d, ink, white)
	tech, _ := ui.gc(d, teal, white)
	text, _ := ui.gc(d, body, white)
	small, _ := ui.gc(d, muted, white)
	onTeal, _ := ui.gc(d, white, teal)
	outline, _ := ui.gc(d, border, white)
	fill, _ := ui.gc(d, teal, white)
	track, _ := ui.gc(d, tint, white)
	xproto.MapWindow(conn, win)
	ui.raise(win)
	// An override-redirect window gets no focus from the window manager: take the keyboard, so Enter and Escape reach the prompt.
	xproto.GrabKeyboard(conn, true, win, xproto.TimeCurrentTime, xproto.GrabModeAsync, xproto.GrabModeAsync)
	defer xproto.UngrabKeyboard(conn, xproto.TimeCurrentTime)

	total := time.Duration(ask.Seconds) * time.Second
	deadline := time.Now().Add(total)
	inner := l.w - 2*consentMargin
	draw := func() {
		xproto.ClearArea(conn, false, win, 0, 0, 0, 0)
		xproto.PolyRectangle(conn, d, outline, []xproto.Rectangle{{Width: uint16(l.w - 1), Height: uint16(l.h - 1)}})
		xproto.PolyFillRectangle(conn, d, fill, []xproto.Rectangle{{Width: uint16(l.w), Height: consentAccentH}})
		ui.boldText(d, title, consentMargin, l.titleY, ask.Texts.Title)
		ui.boldText(d, tech, consentMargin, l.techY, ask.Technician)
		for i, line := range l.message {
			ui.text(d, text, consentMargin, l.messageY+i*l.line, line)
		}
		remaining := max(time.Until(deadline), 0)
		left := int((remaining + time.Second - 1) / time.Second)
		xproto.PolyFillRectangle(conn, d, track, []xproto.Rectangle{{X: consentMargin, Y: int16(l.barY), Width: uint16(inner), Height: consentBarH}})
		if filled := int(float64(inner) * float64(remaining) / float64(max(total, time.Second))); filled > 0 {
			xproto.PolyFillRectangle(conn, d, fill, []xproto.Rectangle{{X: consentMargin, Y: int16(l.barY), Width: uint16(filled), Height: consentBarH}})
		}
		ui.text(d, small, consentMargin, l.leftY, ConsentTimeLeft(ask.Texts, left))
		// Deny: outlined, with a teal ring because it is the default. Allow: filled teal.
		xproto.PolyRectangle(conn, d, outline, []xproto.Rectangle{{X: l.deny.X, Y: l.deny.Y, Width: l.deny.Width - 1, Height: l.deny.Height - 1}})
		xproto.PolyRectangle(conn, d, fill, []xproto.Rectangle{{X: l.deny.X - 2, Y: l.deny.Y - 2, Width: l.deny.Width + 3, Height: l.deny.Height + 3}})
		baseline := (consentButtonH + ui.ascent - ui.descent) / 2
		ui.text(d, title, int(l.deny.X)+(int(l.deny.Width)-ui.width(ask.Texts.Deny))/2, int(l.deny.Y)+baseline, ask.Texts.Deny)
		xproto.PolyFillRectangle(conn, d, fill, []xproto.Rectangle{l.allow})
		ui.boldText(d, onTeal, int(l.allow.X)+(int(l.allow.Width)-ui.width(ask.Texts.Allow))/2, int(l.allow.Y)+baseline, ask.Texts.Allow)
	}

	events := make(chan xgb.Event, 16)
	go func() {
		for {
			ev, xerr := conn.WaitForEvent()
			if ev == nil && xerr == nil {
				close(events)
				return
			}
			if ev != nil {
				// Never block the connection's reader: when the loop is busy an event is dropped, not the connection.
				select {
				case events <- ev:
				default:
				}
			}
		}
	}()
	keymap := consentKeymap(conn, setup)
	tick := time.NewTicker(time.Second)
	defer tick.Stop()
	for {
		select {
		case <-ctx.Done():
			return "", ctx.Err()
		case <-tick.C:
			if time.Now().After(deadline) {
				return "timeout", nil
			}
			ui.raise(win)
			draw()
		case ev, ok := <-events:
			if !ok {
				return "", errors.New("the X display closed")
			}
			switch e := ev.(type) {
			case xproto.ExposeEvent:
				if e.Count == 0 {
					draw()
				}
			case xproto.ButtonPressEvent:
				if inside(l.allow, e.EventX, e.EventY) {
					return "allowed", nil
				}
				if inside(l.deny, e.EventX, e.EventY) {
					return "refused", nil
				}
			case xproto.KeyPressEvent:
				switch keymap[e.Detail] {
				case escapeKeysym, returnKeysym, kpEnterKeysym:
					return "refused", nil
				}
			}
		}
	}
}

// boldText draws a line, then the same glyphs one pixel to the right without a background, so a core font reads as bold.
func (u *xui) boldText(drawable xproto.Drawable, gc xproto.Gcontext, x, y int, s string) {
	u.text(drawable, gc, x, y, s)
	chars := toChar2b(s)
	for len(chars) > 0 {
		n := min(len(chars), 254)
		items := make([]byte, 0, 2+2*n)
		items = append(items, byte(n), 0)
		for _, c := range chars[:n] {
			items = append(items, c.Byte1, c.Byte2)
		}
		xproto.PolyText16(u.conn, drawable, gc, int16(x+1), int16(y), items)
		x += n * u.charW
		chars = chars[n:]
	}
}

func inside(r xproto.Rectangle, x, y int16) bool {
	return x >= r.X && y >= r.Y && x < r.X+int16(r.Width) && y < r.Y+int16(r.Height)
}

// consentKeymap is the first keysym of every keycode, to recognize Enter and Escape.
func consentKeymap(conn *xgb.Conn, setup *xproto.SetupInfo) map[xproto.Keycode]uint32 {
	out := map[xproto.Keycode]uint32{}
	first, last := setup.MinKeycode, setup.MaxKeycode
	reply, err := xproto.GetKeyboardMapping(conn, first, byte(last-first+1)).Reply()
	if err != nil || reply.KeysymsPerKeycode == 0 {
		return out
	}
	per := int(reply.KeysymsPerKeycode)
	for i := 0; i*per < len(reply.Keysyms); i++ {
		out[first+xproto.Keycode(i)] = uint32(reply.Keysyms[i*per])
	}
	return out
}
