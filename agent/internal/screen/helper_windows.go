//go:build windows

package screen

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"runtime"
	"sync"
	"time"
	"unicode/utf16"

	"golang.org/x/sys/windows"
)

// RunHelper serves one remote control session from inside the Windows session: it reads browser frames from in and writes frames for
// the browser to out, until in closes. It captures and injects on one locked OS thread, which follows the input desktop (sign-in screen,
// UAC) as it changes. Run by "fleeto-agent remote-helper", started by Launch as SYSTEM in the chosen session.
func RunHelper(ctx context.Context, in io.Reader, out io.Writer, sessionID uint32, logger *slog.Logger) error {
	// Physical pixels on every monitor, whatever the display scaling.
	procSetProcessDpiAwarenessContext.Call(dpiPerMonitorV2)

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

	var writeMu sync.Mutex
	write := func(frame []byte) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		return WriteFrame(out, frame)
	}

	// The banner lives on its own desktop thread, because a thread that owns windows cannot follow the input desktop. The clipboard is
	// served by a process of its own that runs as the signed-in user (0.3.0 step 4).
	ui := startDesktopUI(write, logger, false)
	defer ui.stop()
	if !ui.running.Load() {
		// Say it at once, instead of only when a technician expects a banner.
		data, _ := json.Marshal(NoticeBody{Message: "This endpoint could not start the banner of its Windows session. " +
			"The screen, mouse and keyboard work."})
		_ = write(append([]byte{FrameNotice}, data...))
	}

	done := make(chan error, 1)
	go func() {
		runtime.LockOSThread()
		defer runtime.UnlockOSThread()
		h := &helper{write: write, logger: logger, session: sessionID, keyboard: NewKeyboard(), selected: -2, ui: ui}
		done <- h.loop(ctx, frames)
	}()
	err := <-done
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

type helper struct {
	write    func([]byte) error
	logger   *slog.Logger
	session  uint32
	keyboard *Keyboard
	ui       *desktopUI

	desktop     windows.Handle
	desktopName string
	monitors    []Monitor
	monitorsAt  time.Time
	// selected is the monitor index shown, -1 for all; -2 until the browser chose.
	selected int
	started  bool

	capture     capturer
	encoder     Encoder
	forceFull   bool
	frame       uint32
	awaiting    bool
	sentAt      time.Time
	sentBytes   int
	ackAt       time.Time
	lastCapture time.Time
	buttons     int

	// H.264 (0.3.0 step 5): the codecs every browser decodes, the codec in use, the stream, and why the tiles are used when the browsers
	// asked for H.264. videoBroken keeps a helper whose encoder failed on tiles.
	codecs      []string
	codec       string
	video       *videoStream
	videoBroken bool
	fallback    string
	// What the last FrameInfo said about the encoder and the capture, so a change is told.
	toldEncoder string
	toldCapture string
}

func (h *helper) loop(ctx context.Context, frames <-chan []byte) error {
	defer h.cleanup()
	timer := time.NewTimer(0)
	defer timer.Stop()
	for {
		select {
		case <-ctx.Done():
			return ctx.Err()
		case frame, ok := <-frames:
			if !ok {
				return nil
			}
			h.handle(frame)
		case <-timer.C:
		}
		h.followInputDesktop()
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

func (h *helper) cleanup() {
	h.inject(h.keyboard.ReleaseAll())
	h.pointer(PointerBody{X: -1, Y: -1, Buttons: 0})
	h.capture.reset()
	if h.video != nil {
		h.video.close()
		h.video = nil
	}
	closeDesktop(h.desktop)
}

func (h *helper) handle(frame []byte) {
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
		h.followInputDesktop()
		h.refreshMonitors(true)
		h.selected = h.validMonitor(start.Monitor)
		h.started = true
		h.forceFull = true
		h.awaiting = false
		h.codecs = start.Codecs
		h.chooseCodec()
		h.sendInfo()
	case FrameAck:
		var ack AckBody
		if json.Unmarshal(body, &ack) == nil && h.awaiting && ack.Frame == h.frame {
			h.awaiting = false
			h.ackAt = time.Now()
			delay := h.ackAt.Sub(h.sentAt)
			if h.video != nil {
				h.video.acked(h.sentBytes, delay)
			}
			quality := DefaultQuality
			if h.sentBytes > largeFrameBytes && delay > slowAck {
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
			h.inject(h.keyboard.Key(k, activeLayout()))
		}
	case FrameType:
		var t TypeBody
		if json.Unmarshal(body, &t) == nil {
			h.inject(h.keyboard.Type(t.Text, activeLayout()))
		}
	case FrameReleaseKeys:
		h.inject(h.keyboard.ReleaseAll())
		if h.buttons != 0 {
			h.pointer(PointerBody{X: -1, Y: -1, Buttons: 0})
		}
	case FrameBanner:
		var banner BannerBody
		if json.Unmarshal(body, &banner) == nil {
			h.ui.setBanner(banner.Names)
		}
	}
}

// followInputDesktop attaches the thread to the desktop that receives input now, so the sign-in screen and UAC prompts are shown and
// can be used. A desktop switch invalidates the capture and makes the next frame whole.
func (h *helper) followInputDesktop() {
	handle, name, err := openInputDesktop()
	if err != nil {
		return // switching (a secure desktop that is closing); try again next round
	}
	if name == h.desktopName && h.desktop != 0 {
		closeDesktop(handle)
		return
	}
	if err := setThreadDesktop(handle); err != nil {
		// Desktop duplication holds Direct3D objects on this thread; without them the switch may succeed.
		h.capture.reset()
		if err = setThreadDesktop(handle); err != nil {
			closeDesktop(handle)
			h.logger.Warn("could not follow the input desktop", "desktop", name, "error", err)
			return
		}
	}
	closeDesktop(h.desktop)
	h.desktop, h.desktopName = handle, name
	h.capture.reset()
	h.encoder.Reset()
	h.forceFull = true
	h.refreshMonitors(true)
	if h.started {
		h.selected = h.validMonitor(h.selected)
		h.sendInfo()
	}
}

func (h *helper) refreshMonitors(force bool) {
	if !force && time.Since(h.monitorsAt) < monitorRefresh {
		return
	}
	found := monitors()
	h.monitorsAt = time.Now()
	changed := len(found) != len(h.monitors)
	for i := 0; !changed && i < len(found); i++ {
		changed = found[i] != h.monitors[i]
	}
	h.monitors = found
	if changed && h.started && !force {
		h.selected = h.validMonitor(h.selected)
		h.forceFull = true
		h.sendInfo()
	}
}

// validMonitor keeps a requested monitor when it exists, else the primary one.
func (h *helper) validMonitor(requested int) int {
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

// area is the rectangle shown, in virtual desktop pixels.
func (h *helper) area() Monitor {
	if h.selected >= 0 && h.selected < len(h.monitors) {
		return h.monitors[h.selected]
	}
	if h.selected == -1 || len(h.monitors) == 0 {
		return virtualScreen()
	}
	return h.monitors[0]
}

// chooseCodec picks H.264 when every browser decodes it and this endpoint can encode it, and says why not when the browsers asked.
func (h *helper) chooseCodec() {
	codec := ChooseCodec(h.codecs, !h.videoBroken)
	if codec == CodecH264 && h.video == nil {
		video, err := newVideoStream(h.logger)
		if err != nil {
			h.logger.Info("remote control cannot send H.264; it sends tiles", "reason", err)
			h.videoBroken = true
			h.fallback = "This endpoint cannot encode H.264 (" + err.Error() + "). On Windows Server, install the Media Foundation feature."
			codec = CodecTiles
		} else {
			h.video = video
		}
	}
	if codec == CodecTiles && h.video != nil {
		h.video.close()
		h.video = nil
	}
	if codec != h.codec {
		h.encoder.Reset()
		h.forceFull = true
	}
	h.codec = codec
}

// videoFailed puts a helper whose H.264 encoder failed on tiles for the rest of its life.
func (h *helper) videoFailed(err error) {
	h.logger.Warn("the H.264 encoder failed; remote control continues with tiles", "error", err)
	if h.video != nil {
		h.video.close()
		h.video = nil
	}
	h.videoBroken = true
	h.fallback = "The H.264 encoder of this endpoint stopped (" + err.Error() + "). The screen continues as tiles."
	h.codec = CodecTiles
	h.encoder.Reset()
	h.forceFull = true
	h.sendInfo()
}

func (h *helper) sendInfo() {
	area := h.area()
	info := InfoBody{Monitors: h.monitors, Monitor: h.selected, Width: area.Width, Height: area.Height, Desktop: h.desktopName, Session: h.session,
		Codec: h.codec, Capture: h.capture.method}
	if h.video != nil {
		info.Encoder = h.video.kind()
	}
	if h.codec == CodecTiles && hasCodec(h.codecs, CodecH264) {
		info.Fallback = h.fallback
	}
	h.toldEncoder, h.toldCapture = info.Encoder, info.Capture
	if info.Monitors == nil {
		info.Monitors = []Monitor{}
	}
	data, _ := json.Marshal(info)
	_ = h.write(append([]byte{FrameInfo}, data...))
}

// maybeCapture sends the next frame when the browser drew the previous one and enough time passed. It returns when to look again.
func (h *helper) maybeCapture() time.Duration {
	h.refreshMonitors(false)
	now := time.Now()
	if h.awaiting {
		if now.Sub(h.sentAt) < ackTimeout {
			return captureInterval
		}
		// The browser did not draw the last frame: send a whole one, it may have lost tiles.
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
	img, err := h.capture.grab(area)
	if err != nil {
		// Most often a desktop switch in progress; the next round attaches to the new desktop.
		h.capture.reset()
		h.desktopName = ""
		return captureInterval
	}
	if h.codec == CodecH264 {
		h.sendVideo(img, now)
		return captureInterval
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
	h.tellChanges()
	return captureInterval
}

// sendVideo encodes a captured image as H.264 and sends it, unless the screen did not change and has settled.
func (h *helper) sendVideo(img *Image, captured time.Time) {
	data, key, err := h.video.frame(img, h.forceFull)
	if err != nil {
		h.videoFailed(err)
		return
	}
	if data == nil {
		return
	}
	h.forceFull = false
	h.frame++
	frame := VideoFrame{Number: h.frame, Key: key, Width: img.Width, Height: img.Height, Endpoint: time.Since(captured), Data: data}
	if !h.ackAt.IsZero() && captured.After(h.ackAt) {
		frame.Waited = captured.Sub(h.ackAt)
	}
	updates, err := VideoUpdates(frame)
	if err != nil {
		h.videoFailed(err)
		return
	}
	total := 0
	for _, update := range updates {
		if err := h.write(update); err != nil {
			return
		}
		total += len(update)
	}
	h.awaiting = true
	h.sentAt = time.Now()
	h.sentBytes = total
	h.tellChanges()
}

// tellChanges sends FrameInfo again when the encoder or the way of capturing changed, so the technician sees what runs.
func (h *helper) tellChanges() {
	encoder := ""
	if h.video != nil {
		encoder = h.video.kind()
	}
	if encoder != h.toldEncoder || h.capture.method != h.toldCapture {
		h.sendInfo()
	}
}

// pointer moves the mouse and presses or releases the buttons that changed. X and Y are pixels of the shown image; a negative position
// only changes the buttons.
func (h *helper) pointer(p PointerBody) {
	var inputs []rawInput
	if p.X >= 0 && p.Y >= 0 {
		area, screen := h.area(), virtualScreen()
		x := min(max(p.X, 0), max(area.Width-1, 0)) + area.X
		y := min(max(p.Y, 0), max(area.Height-1, 0)) + area.Y
		move := mouseInput{Flags: mouseMove | mouseAbsolute | mouseVirtual}
		if screen.Width > 1 && screen.Height > 1 {
			move.DX = int32((int64(x-screen.X)*65535 + int64(screen.Width-1)/2) / int64(screen.Width-1))
			move.DY = int32((int64(y-screen.Y)*65535 + int64(screen.Height-1)/2) / int64(screen.Height-1))
		}
		inputs = append(inputs, rawInput{Type: inputMouse, Union: move})
	}
	for _, b := range []struct {
		mask     int
		down, up uint32
	}{{1, mouseLeftDown, mouseLeftUp}, {2, mouseRightDown, mouseRightUp}, {4, mouseMiddleDown, mouseMiddleUp}} {
		was, is := h.buttons&b.mask != 0, p.Buttons&b.mask != 0
		if was != is {
			flags := b.up
			if is {
				flags = b.down
			}
			inputs = append(inputs, rawInput{Type: inputMouse, Union: mouseInput{Flags: flags}})
		}
	}
	h.buttons = p.Buttons & 7
	if p.Wheel != 0 {
		inputs = append(inputs, rawInput{Type: inputMouse, Union: mouseInput{Flags: mouseWheel, MouseData: uint32(int32(clampWheel(p.Wheel) * wheelDelta))}})
	}
	if p.HWheel != 0 {
		inputs = append(inputs, rawInput{Type: inputMouse, Union: mouseInput{Flags: mouseHWheel, MouseData: uint32(int32(clampWheel(p.HWheel) * wheelDelta))}})
	}
	_ = sendInputs(inputs)
}

func clampWheel(steps int) int { return min(max(steps, -10), 10) }

// inject sends planned keyboard events.
func (h *helper) inject(planned []Input) {
	var inputs []rawInput
	for _, in := range planned {
		switch {
		case in.Unicode != 0:
			for _, unit := range utf16.Encode([]rune{in.Unicode}) {
				k := keyInput{Scan: unit, Flags: keyUnicode}
				if in.Up {
					k.Flags |= keyUp
				}
				inputs = append(inputs, keyboardInput(k))
			}
		case in.VK != 0:
			k := keyInput{VK: in.VK, Scan: in.Scan.Code}
			if in.Scan.Extended {
				k.Flags |= keyExtended
			}
			if in.Up {
				k.Flags |= keyUp
			}
			inputs = append(inputs, keyboardInput(k))
		default:
			k := keyInput{Scan: in.Scan.Code, Flags: keyScanCode}
			if in.Scan.Extended {
				k.Flags |= keyExtended
			}
			if in.Up {
				k.Flags |= keyUp
			}
			inputs = append(inputs, keyboardInput(k))
		}
	}
	if err := sendInputs(inputs); err != nil {
		h.logger.Debug("could not inject keyboard input", "error", err)
	}
}
