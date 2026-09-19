//go:build linux

package screen

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
	"unicode/utf8"

	"github.com/jezek/xgb"
	"github.com/jezek/xgb/xfixes"
	"github.com/jezek/xgb/xproto"
)

// The clipboard of a remote control session on X11 (0.3.0 step 6), served as the user of the session by "fleeto-agent remote-clipboard".
// It watches the CLIPBOARD selection (XFIXES tells when its owner changes) and reads what was copied: files as text/uri-list or GNOME's
// x-special/gnome-copied-files, otherwise text. Text from the technician and pasted files are offered by owning the selection. Large data
// travels in pieces (INCR) both ways. It never touches the screen or the keyboard.

const (
	// incrChunk is the size of one piece of an INCR transfer, well inside the request size every X server takes.
	incrChunk = 64 * 1024
	// readTimeout ends a read of the clipboard whose owner does not answer.
	readTimeout = 3 * time.Second
	// servingTimeout ends an INCR transfer the requestor stopped taking.
	servingTimeout = 10 * time.Second
)

const (
	targetGnomeFiles = "x-special/gnome-copied-files"
	targetURIList    = "text/uri-list"
	targetPlainUTF8  = "text/plain;charset=utf-8"
)

// RunClipboardAgent serves the clipboard of the display the agent names in its first frame, until in closes.
func RunClipboardAgent(ctx context.Context, in io.Reader, out io.Writer, logger *slog.Logger) error {
	conn, _, err := startX11Child(in)
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
	c, err := newXClipboard(conn, write, logger)
	if err != nil {
		return err
	}

	frames := make(chan []byte, 16)
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
				close(events)
				return
			}
			if ev != nil {
				events <- ev
			}
		}
	}()
	// Only what is copied from now on is offered, as on Windows: what was on the clipboard before the session is not the technician's.
	ticker := time.NewTicker(time.Second)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return nil
		case err := <-readErr:
			if errors.Is(err, io.EOF) {
				return nil
			}
			return err
		case frame, ok := <-frames:
			if !ok {
				return nil
			}
			c.handle(frame)
		case ev, ok := <-events:
			if !ok {
				return errors.New("the X display closed")
			}
			c.event(ev)
		case <-ticker.C:
			c.expire()
		}
	}
}

// xClipboard is the state of the clipboard process.
type xClipboard struct {
	conn   *xgb.Conn
	write  func([]byte) error
	logger *slog.Logger
	win    xproto.Window
	atoms  map[string]xproto.Atom

	// What this process offers while it owns the selection.
	owned bool
	text  string
	files []string

	// The read in progress: its stage ("targets" or a target name), the data gathered by INCR, and whether another change came meanwhile.
	reading  string
	incr     bool
	gathered []byte
	started  time.Time
	again    bool

	serving map[servingKey]*servingTransfer
}

type servingKey struct {
	requestor xproto.Window
	property  xproto.Atom
}

type servingTransfer struct {
	target xproto.Atom
	data   []byte
	at     time.Time
}

func newXClipboard(conn *xgb.Conn, write func([]byte) error, logger *slog.Logger) (*xClipboard, error) {
	if err := xfixes.Init(conn); err != nil {
		return nil, errors.New("the X server has no XFIXES extension, so the clipboard cannot be watched")
	}
	if _, err := xfixes.QueryVersion(conn, 4, 0).Reply(); err != nil {
		return nil, err
	}
	screen := xproto.Setup(conn).DefaultScreen(conn)
	win, err := xproto.NewWindowId(conn)
	if err != nil {
		return nil, err
	}
	if err := xproto.CreateWindowChecked(conn, 0, win, screen.Root, -10, -10, 1, 1, 0, xproto.WindowClassInputOutput, 0,
		xproto.CwEventMask, []uint32{xproto.EventMaskPropertyChange}).Check(); err != nil {
		return nil, err
	}
	c := &xClipboard{conn: conn, write: write, logger: logger, win: win, atoms: map[string]xproto.Atom{}, serving: map[servingKey]*servingTransfer{}}
	for _, name := range []string{"CLIPBOARD", "TARGETS", "TIMESTAMP", "MULTIPLE", "UTF8_STRING", "STRING", "TEXT", "INCR", "ATOM",
		"FLEETO_CLIPBOARD", targetPlainUTF8, targetURIList, targetGnomeFiles} {
		reply, err := xproto.InternAtom(conn, false, uint16(len(name)), name).Reply()
		if err != nil {
			return nil, err
		}
		c.atoms[name] = reply.Atom
	}
	if err := xfixes.SelectSelectionInputChecked(conn, win, c.atoms["CLIPBOARD"], xfixes.SelectionEventMaskSetSelectionOwner|
		xfixes.SelectionEventMaskSelectionWindowDestroy|xfixes.SelectionEventMaskSelectionClientClose).Check(); err != nil {
		return nil, err
	}
	return c, nil
}

func (c *xClipboard) handle(frame []byte) {
	if len(frame) == 0 {
		return
	}
	switch frame[0] {
	case FrameClipboard:
		text := string(frame[1:])
		if len(frame)-1 > MaxClipboardBytes || !utf8.ValidString(text) {
			return
		}
		c.own(text, nil)
	case FramePlaceFiles:
		var place PlaceFilesBody
		if json.Unmarshal(frame[1:], &place) == nil && len(place.Paths) > 0 {
			c.own(strings.Join(place.Paths, "\n"), place.Paths)
		}
	}
}

// own takes the selection with this text and files.
func (c *xClipboard) own(text string, files []string) {
	c.text, c.files = text, files
	xproto.SetSelectionOwner(c.conn, c.win, c.atoms["CLIPBOARD"], xproto.TimeCurrentTime)
	owner, err := xproto.GetSelectionOwner(c.conn, c.atoms["CLIPBOARD"]).Reply()
	c.owned = err == nil && owner.Owner == c.win
	if !c.owned {
		c.logger.Warn("could not take the clipboard of the session")
	}
}

func (c *xClipboard) event(ev xgb.Event) {
	switch e := ev.(type) {
	case xfixes.SelectionNotifyEvent:
		if e.Owner == c.win {
			return // our own text or files
		}
		c.startRead(e.Timestamp)
	case xproto.SelectionNotifyEvent:
		if e.Requestor == c.win {
			c.converted(e)
		}
	case xproto.PropertyNotifyEvent:
		if e.Window == c.win && e.Atom == c.atoms["FLEETO_CLIPBOARD"] && e.State == xproto.PropertyNewValue && c.incr {
			c.gatherPiece()
			return
		}
		if e.State == xproto.PropertyDelete {
			c.servePiece(servingKey{e.Window, e.Atom})
		}
	case xproto.SelectionRequestEvent:
		if e.Owner == c.win {
			c.serve(e)
		}
	case xproto.SelectionClearEvent:
		if e.Owner == c.win {
			c.owned, c.text, c.files = false, "", nil
		}
	}
}

// startRead asks the owner of the selection which targets it offers.
func (c *xClipboard) startRead(at xproto.Timestamp) {
	if c.reading != "" {
		c.again = true
		return
	}
	owner, err := xproto.GetSelectionOwner(c.conn, c.atoms["CLIPBOARD"]).Reply()
	if err != nil || owner.Owner == 0 || owner.Owner == c.win {
		return
	}
	c.convert("TARGETS", at)
}

func (c *xClipboard) convert(target string, at xproto.Timestamp) {
	c.reading, c.incr, c.gathered, c.started = target, false, nil, time.Now()
	xproto.ConvertSelection(c.conn, c.win, c.atoms["CLIPBOARD"], c.atoms[target], c.atoms["FLEETO_CLIPBOARD"], at)
}

// converted takes the answer of the owner to a conversion.
func (c *xClipboard) converted(e xproto.SelectionNotifyEvent) {
	if c.reading == "" {
		return
	}
	if e.Property == 0 {
		c.finish(nil)
		return
	}
	reply, err := xproto.GetProperty(c.conn, true, c.win, e.Property, 0, 0, uint32(MaxClipboardBytes/4+1024)).Reply()
	if err != nil {
		c.finish(nil)
		return
	}
	if reply.Type == c.atoms["INCR"] {
		// The owner sends it in pieces; each one arrives as a new value of the property, which was deleted just now.
		c.incr = true
		return
	}
	if reply.BytesAfter > 0 {
		c.finish(nil) // more than the clipboard synchronises
		return
	}
	c.finish(reply)
}

// gatherPiece reads one piece of an INCR transfer; an empty piece ends it.
func (c *xClipboard) gatherPiece() {
	reply, err := xproto.GetProperty(c.conn, true, c.win, c.atoms["FLEETO_CLIPBOARD"], 0, 0, uint32(incrChunk)).Reply()
	if err != nil {
		c.finish(nil)
		return
	}
	if len(reply.Value) == 0 {
		c.incr = false
		c.finish(&xproto.GetPropertyReply{Format: 8, Value: c.gathered})
		return
	}
	c.gathered = append(c.gathered, reply.Value...)
	if len(c.gathered) > MaxClipboardBytes {
		c.incr = false
		c.finish(nil)
	}
}

// finish handles the result of the current stage and starts the next one.
func (c *xClipboard) finish(reply *xproto.GetPropertyReply) {
	stage := c.reading
	c.reading, c.incr, c.gathered = "", false, nil
	switch {
	case stage == "TARGETS" && reply != nil && reply.Format == 32:
		offered := map[xproto.Atom]bool{}
		for i := 0; i+4 <= len(reply.Value); i += 4 {
			offered[xproto.Atom(binary.LittleEndian.Uint32(reply.Value[i:]))] = true
		}
		for _, target := range []string{targetGnomeFiles, targetURIList, "UTF8_STRING", "STRING"} {
			if offered[c.atoms[target]] {
				c.convert(target, xproto.TimeCurrentTime)
				return
			}
		}
		c.deliver(nil, "", false)
	case stage == targetGnomeFiles || stage == targetURIList:
		if reply != nil {
			c.deliver(pathsFromURIList(string(reply.Value)), "", false)
		}
	case stage == "UTF8_STRING" && reply != nil:
		c.deliver(nil, strings.ToValidUTF8(string(reply.Value), "?"), true)
	case stage == "STRING" && reply != nil:
		c.deliver(nil, latin1(reply.Value), true)
	}
	if c.again {
		c.again = false
		c.startRead(xproto.TimeCurrentTime)
	}
}

// deliver tells the agent what was copied: the files (none for text), and the text.
func (c *xClipboard) deliver(paths []string, text string, hasText bool) {
	files := make([]CopiedFile, 0, len(paths))
	for _, p := range paths {
		info, err := os.Stat(p)
		if err != nil {
			continue
		}
		files = append(files, CopiedFile{Path: p, Name: filepath.Base(p), Size: info.Size(), Dir: info.IsDir()})
	}
	data, _ := json.Marshal(CopiedFilesBody{Files: files})
	_ = c.write(append([]byte{FrameCopiedFiles}, data...))
	if hasText && text != "" && len(text) <= MaxClipboardBytes {
		_ = c.write(append([]byte{FrameClipboard}, text...))
	}
}

func latin1(b []byte) string {
	var sb strings.Builder
	for _, c := range b {
		sb.WriteRune(rune(c))
	}
	return sb.String()
}

// serve answers a request for what this process offers.
func (c *xClipboard) serve(e xproto.SelectionRequestEvent) {
	property := e.Property
	if property == 0 {
		property = e.Target // an old client (ICCCM)
	}
	ok := e.Selection == c.atoms["CLIPBOARD"] && c.owned && c.answer(e.Requestor, property, e.Target)
	notify := xproto.SelectionNotifyEvent{Time: e.Time, Requestor: e.Requestor, Selection: e.Selection, Target: e.Target, Property: property}
	if !ok {
		notify.Property = 0
	}
	xproto.SendEvent(c.conn, false, e.Requestor, 0, string(notify.Bytes()))
}

// answer puts the data of a target on the requestor's property, in pieces when it is large.
func (c *xClipboard) answer(requestor xproto.Window, property, target xproto.Atom) bool {
	var data []byte
	format := byte(8)
	kind := target
	switch target {
	case c.atoms["TARGETS"]:
		names := []string{"TARGETS", "UTF8_STRING", "STRING", "TEXT", targetPlainUTF8}
		if len(c.files) > 0 {
			names = append(names, targetURIList, targetGnomeFiles)
		}
		for _, n := range names {
			data = binary.LittleEndian.AppendUint32(data, uint32(c.atoms[n]))
		}
		format, kind = 32, c.atoms["ATOM"]
	case c.atoms["UTF8_STRING"], c.atoms["TEXT"], c.atoms[targetPlainUTF8]:
		data = []byte(c.text)
		if target == c.atoms["TEXT"] {
			kind = c.atoms["UTF8_STRING"]
		}
	case c.atoms["STRING"]:
		for _, r := range c.text {
			if r > 0xFF {
				r = '?'
			}
			data = append(data, byte(r))
		}
	case c.atoms[targetURIList]:
		if len(c.files) == 0 {
			return false
		}
		data = []byte(uriList(c.files))
	case c.atoms[targetGnomeFiles]:
		if len(c.files) == 0 {
			return false
		}
		data = []byte(gnomeCopiedFiles(c.files))
	default:
		return false
	}
	if len(data) <= incrChunk {
		units := uint32(len(data)) / uint32(format/8)
		xproto.ChangeProperty(c.conn, xproto.PropModeReplace, requestor, property, kind, format, units, data)
		return true
	}
	// INCR: announce the size, then send a piece each time the requestor deleted the last one.
	xproto.ChangeWindowAttributes(c.conn, requestor, xproto.CwEventMask, []uint32{xproto.EventMaskPropertyChange})
	size := binary.LittleEndian.AppendUint32(nil, uint32(len(data)))
	xproto.ChangeProperty(c.conn, xproto.PropModeReplace, requestor, property, c.atoms["INCR"], 32, 1, size)
	c.serving[servingKey{requestor, property}] = &servingTransfer{target: kind, data: data, at: time.Now()}
	return true
}

// servePiece sends the next piece of an INCR transfer; an empty piece ends it.
func (c *xClipboard) servePiece(key servingKey) {
	t := c.serving[key]
	if t == nil {
		return
	}
	n := min(len(t.data), incrChunk)
	xproto.ChangeProperty(c.conn, xproto.PropModeReplace, key.requestor, key.property, t.target, 8, uint32(n), t.data[:n])
	t.at = time.Now()
	if n == 0 {
		delete(c.serving, key)
		return
	}
	t.data = t.data[n:]
}

// expire gives up reads and transfers that stalled.
func (c *xClipboard) expire() {
	if c.reading != "" && time.Since(c.started) > readTimeout {
		c.logger.Debug("the owner of the clipboard did not answer", "target", c.reading)
		c.reading, c.incr, c.gathered = "", false, nil
		if c.again {
			c.again = false
			c.startRead(xproto.TimeCurrentTime)
		}
	}
	for key, t := range c.serving {
		if time.Since(t.at) > servingTimeout {
			delete(c.serving, key)
		}
	}
}
