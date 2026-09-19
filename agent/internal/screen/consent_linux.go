//go:build linux

package screen

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"time"

	"github.com/jezek/xgb"
	"github.com/jezek/xgb/xproto"
)

// The consent prompt on X11 (0.3.0 step 6), run by "fleeto-agent remote-consent" as the user of the session: a window in the middle of
// the primary monitor with the question, Allow and Deny, and the seconds left. Deny is the default, as on Windows: Enter and Escape refuse.
// Without an answer in time, access is granted (decided 2026-09-16).

const (
	consentWidthChars = 58
	consentMargin     = 16
	consentButtonW    = 110
	consentButtonH    = 32
	escapeKeysym      = 0xFF1B
	returnKeysym      = 0xFF0D
	kpEnterKeysym     = 0xFF8D
)

// RunConsent asks the person at the screen and writes the answer.
func RunConsent(ctx context.Context, in io.Reader, out io.Writer, logger *slog.Logger) error {
	conn, _, err := startX11Child(in)
	if err != nil {
		return err
	}
	defer conn.Close()
	frame, err := ReadFrame(in)
	if err != nil {
		return err
	}
	if frame[0] != FrameConsentAsk {
		return errors.New("the consent process was not asked anything")
	}
	var ask ConsentAskBody
	if err := json.Unmarshal(frame[1:], &ask); err != nil {
		return err
	}
	answer, err := showConsent(ctx, conn, ask)
	if err != nil {
		logger.Warn("the consent prompt could not be shown", "error", err)
		return err
	}
	data, _ := json.Marshal(ConsentAnswerBody{Answer: answer})
	return WriteFrame(out, append([]byte{FrameConsentAnswer}, data...))
}

func showConsent(ctx context.Context, conn *xgb.Conn, ask ConsentAskBody) (string, error) {
	setup := xproto.Setup(conn)
	screen := setup.DefaultScreen(conn)
	ui, err := newXUI(conn, screen)
	if err != nil {
		return "", err
	}
	seconds := min(max(ask.Seconds, 10), 300)
	lines := wrapText(ConsentMessage(ask.Technician, seconds), consentWidthChars)
	line := ui.lineHeight() + 4
	w := consentWidthChars*ui.charW + 2*consentMargin
	h := consentMargin + len(lines)*line + consentMargin + consentButtonH + consentMargin + line
	area := primaryMonitorOf(conn, screen)
	x, y := area.X+(area.Width-w)/2, area.Y+(area.Height-h)/2

	bg, fg, border, teal, white := ui.color(colorStone50), ui.color(colorStone900), ui.color(colorStone300), ui.color(colorTeal), ui.color(colorWhite)
	win, err := ui.createPopup(x, y, w, h, bg, xproto.EventMaskExposure|xproto.EventMaskButtonPress|xproto.EventMaskKeyPress)
	if err != nil {
		return "", err
	}
	defer xproto.DestroyWindow(conn, win)
	text, _ := ui.gc(xproto.Drawable(win), fg, bg)
	onTeal, _ := ui.gc(xproto.Drawable(win), white, teal)
	outline, _ := ui.gc(xproto.Drawable(win), border, bg)
	fill, _ := ui.gc(xproto.Drawable(win), teal, bg)
	xproto.MapWindow(conn, win)
	ui.raise(win)
	// An override-redirect window gets no focus from the window manager: take the keyboard, so Enter and Escape reach the prompt.
	xproto.GrabKeyboard(conn, true, win, xproto.TimeCurrentTime, xproto.GrabModeAsync, xproto.GrabModeAsync)
	defer xproto.UngrabKeyboard(conn, xproto.TimeCurrentTime)

	buttonsY := consentMargin + len(lines)*line + consentMargin
	allow := xproto.Rectangle{X: int16(w - consentMargin - 2*consentButtonW - 12), Y: int16(buttonsY), Width: consentButtonW, Height: consentButtonH}
	deny := xproto.Rectangle{X: int16(w - consentMargin - consentButtonW), Y: int16(buttonsY), Width: consentButtonW, Height: consentButtonH}
	deadline := time.Now().Add(time.Duration(seconds) * time.Second)
	draw := func() {
		xproto.ClearArea(conn, false, win, 0, 0, 0, 0)
		for i, l := range lines {
			ui.text(xproto.Drawable(win), text, consentMargin, consentMargin+i*line+ui.ascent, l)
		}
		xproto.PolyFillRectangle(conn, xproto.Drawable(win), fill, []xproto.Rectangle{allow})
		ui.text(xproto.Drawable(win), onTeal, int(allow.X)+(consentButtonW-ui.width("Allow"))/2, int(allow.Y)+(consentButtonH+ui.ascent-ui.descent)/2, "Allow")
		xproto.PolyRectangle(conn, xproto.Drawable(win), outline, []xproto.Rectangle{{X: deny.X, Y: deny.Y, Width: deny.Width - 1, Height: deny.Height - 1}})
		ui.text(xproto.Drawable(win), text, int(deny.X)+(consentButtonW-ui.width("Deny"))/2, int(deny.Y)+(consentButtonH+ui.ascent-ui.descent)/2, "Deny")
		left := max(int(time.Until(deadline).Round(time.Second)/time.Second), 0)
		ui.text(xproto.Drawable(win), text, consentMargin, buttonsY+consentButtonH+consentMargin+ui.ascent,
			fmt.Sprintf("Access is granted in %d seconds without an answer.", left))
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
				events <- ev
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
				if inside(allow, e.EventX, e.EventY) {
					return "allowed", nil
				}
				if inside(deny, e.EventX, e.EventY) {
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
