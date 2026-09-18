package screen

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// Several technicians in one remote control session (0.3.0 step 4, ARCHITECTURE.md §4 Remote control). Every technician has their own
// token, key exchange and encrypted session with the endpoint; the endpoint joins those that name the same remote session into one hub,
// which runs one helper for all of them: one screen, one monitor choice, one banner, and input from each. The hub sends the screen to
// every technician and waits for the slowest to draw a frame before the helper captures the next, except for a technician who lags far
// behind. The first technician of a session is asked for consent when the policy says so; everyone who joins later sees the screen at once
// (decided 2026-09-17).

// ConsentAnswer is what the person at the endpoint answered.
type ConsentAnswer int

const (
	// ConsentAllowed: the person allowed the session.
	ConsentAllowed ConsentAnswer = iota
	// ConsentRefused: the person refused; the session ends.
	ConsentRefused
	// ConsentTimedOut: no answer in time; access is granted (decided 2026-09-16).
	ConsentTimedOut
)

const (
	// sendQueue is how many frames wait for one technician; a technician whose connection cannot keep up that far is disconnected, so
	// the others keep their screen.
	sendQueue = 512
	// lagAllowance is how long the hub waits for a technician who has not drawn a frame that another technician already drew.
	lagAllowance = 3 * time.Second
	// pointerInterval is the shortest time between two pointer positions sent to the other technicians.
	pointerInterval = 40 * time.Millisecond
	// hubTick is how often a frame held back by a lagging technician is checked.
	hubTick = 500 * time.Millisecond
	// maxBatches bounds the folders of pasted files one session stages.
	maxBatches = 1000
)

// SessionsOptions configure the remote control sessions of one agent service.
type SessionsOptions struct {
	// Launch starts a helper in a Windows session.
	Launch Launcher
	// ConsoleSession returns the Windows session attached to the console now.
	ConsoleSession func() uint32
	// SessionExists reports whether a Windows session is still there.
	SessionExists func(session uint32) bool
	// SecureAttention sends Ctrl+Alt+Del, or says why it cannot.
	SecureAttention func() error
	// SessionUser returns the account signed in on a Windows session, "" when nobody is.
	SessionUser func(session uint32) string
	// Consent asks the person signed in on a Windows session to allow a technician; it blocks until the answer or the timeout.
	Consent func(ctx context.Context, session uint32, technician string, timeout time.Duration) (ConsentAnswer, error)
	// StagingRoot is where files pasted into a session are kept while it runs; "" where files cannot be pasted.
	StagingRoot string
	// Stage creates a folder for pasted files that only the system, administrators and the user of the Windows session can read.
	Stage  func(dir string, session uint32) error
	Logger *slog.Logger
	Now    func() time.Time
	// ConsoleCheck, LagAllowance, Tick and QueueSize override the defaults in tests.
	ConsoleCheck time.Duration
	LagAllowance time.Duration
	Tick         time.Duration
	QueueSize    int
}

// Sessions keeps the running remote control sessions of an agent service, one hub per remote session id.
type Sessions struct {
	opts SessionsOptions
	mu   sync.Mutex
	hubs map[string]*hub
}

// NewSessions returns an empty set of sessions.
func NewSessions(opts SessionsOptions) *Sessions {
	if opts.Logger == nil {
		opts.Logger = slog.New(slog.DiscardHandler)
	}
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.LagAllowance <= 0 {
		opts.LagAllowance = lagAllowance
	}
	if opts.Tick <= 0 {
		opts.Tick = hubTick
	}
	if opts.QueueSize <= 0 {
		opts.QueueSize = sendQueue
	}
	return &Sessions{opts: opts, hubs: map[string]*hub{}}
}

// JoinOptions describe one technician joining a session, from their verified token.
type JoinOptions struct {
	SessionID      string
	ParticipantID  string
	Technician     string
	WindowsSession uint32
	// ConsentRequired, ConsentTimeout and BannerVisible come from the token; only the first technician's count for the session.
	ConsentRequired bool
	ConsentTimeout  time.Duration
	BannerVisible   bool
	// ClipboardEnabled comes from the technician's own token.
	ClipboardEnabled bool
	// Send passes a frame to this technician's browser over their encrypted session.
	Send func(frame []byte) error
	// End ends this technician's session with the reason.
	End func(reason string)
	// Report records an action for the audit log.
	Report func(action, target, detail string)
}

// Count is how many sessions run; for tests.
func (s *Sessions) Count() int {
	s.mu.Lock()
	defer s.mu.Unlock()
	return len(s.hubs)
}

// Join adds a technician to the hub of their session, starting the hub for the first one.
func (s *Sessions) Join(opts JoinOptions) *Participant {
	if opts.Report == nil {
		opts.Report = func(string, string, string) {}
	}
	if opts.End == nil {
		opts.End = func(string) {}
	}
	s.mu.Lock()
	h := s.hubs[opts.SessionID]
	if h != nil {
		h.mu.Lock()
		if h.closed {
			// The last technician is leaving this very moment: the session starts anew.
			h.mu.Unlock()
			h = nil
		}
	}
	if h == nil {
		h = newHub(s, opts)
		s.hubs[opts.SessionID] = h
		h.mu.Lock()
	}
	p := &Participant{h: h, opts: opts, out: make(chan []byte, s.opts.QueueSize), done: make(chan struct{})}
	h.participants = append(h.participants, p)
	state := h.consent
	if state == consentNone {
		// The first technician decides for the session.
		h.banner = opts.BannerVisible
		if opts.ConsentRequired {
			h.consent = consentPending
		} else {
			h.consent = consentGranted
		}
	}
	if h.consent == consentGranted {
		p.granted = true
	}
	granted, pending := p.granted, h.consent == consentPending
	h.mu.Unlock()
	s.mu.Unlock()

	safego.Go(s.opts.Logger, "remote control sender", p.sendLoop)
	switch {
	case granted:
		h.welcome([]*Participant{p}, nil)
	case pending && state == consentNone:
		safego.Go(s.opts.Logger, "remote control consent", func() { h.askConsent(p) })
	case pending:
		p.enqueue(jsonFrame(FrameConsent, ConsentBody{State: "waiting", Message: "Waiting for the person at the endpoint to allow the session."}))
	}
	return p
}

const (
	consentNone = iota
	consentPending
	consentGranted
)

type hub struct {
	s          *Sessions
	id         string
	windows    uint32
	ctx        context.Context
	cancel     context.CancelFunc
	controller *Controller

	mu           sync.Mutex
	participants []*Participant
	consent      int
	banner       bool
	lastBanner   string
	info         []byte
	copied       []CopiedFile
	staging      string
	batches      int
	closed       bool

	// Flow control over every technician: the last whole frame the helper sent, how many technicians drew it, whether the helper was told.
	frame     uint32
	frameAcks int
	forwarded bool
}

func newHub(s *Sessions, first JoinOptions) *hub {
	ctx, cancel := context.WithCancel(context.Background())
	h := &hub{s: s, id: first.SessionID, windows: first.WindowsSession, ctx: ctx, cancel: cancel, forwarded: true}
	h.controller = NewController(ControllerOptions{
		Send: h.fromHelper, Launch: s.opts.Launch, Session: first.WindowsSession, ConsoleSession: s.opts.ConsoleSession,
		SessionExists: s.opts.SessionExists, SecureAttention: s.opts.SecureAttention, Logger: s.opts.Logger, Now: s.opts.Now,
		ConsoleCheck: s.opts.ConsoleCheck,
	})
	safego.Go(s.opts.Logger, "remote control flow", h.tick)
	return h
}

// fromHelper takes a frame of the helper (or a notice of the controller) and passes it to the technicians it is for.
func (h *hub) fromHelper(frame []byte) error {
	if len(frame) == 0 {
		return nil
	}
	switch frame[0] {
	case FrameInfo:
		h.mu.Lock()
		h.info = append([]byte(nil), frame...)
		to := h.grantedLocked()
		h.mu.Unlock()
		broadcast(to, frame)
	case FrameUpdate:
		number, last, ok := updateHeader(frame)
		if !ok {
			return nil
		}
		h.mu.Lock()
		to := h.grantedLocked()
		ackNow := false
		if last {
			now := h.s.opts.Now()
			h.frame, h.frameAcks, h.forwarded = number, 0, false
			for _, p := range to {
				p.awaiting, p.awaitFrame, p.awaitSince = true, number, now
			}
			if len(to) == 0 {
				h.forwarded, ackNow = true, true
			}
		}
		h.mu.Unlock()
		broadcast(to, frame)
		if ackNow {
			h.controller.Handle(h.ctx, jsonFrame(FrameAck, AckBody{Frame: number}))
		}
	case FrameClipboard:
		h.mu.Lock()
		to := h.clipboardLocked()
		h.mu.Unlock()
		broadcast(to, frame)
	case FrameCopiedFiles:
		var body CopiedFilesBody
		if json.Unmarshal(frame[1:], &body) != nil {
			return nil
		}
		// Files a technician pasted are still on the endpoint clipboard after a helper starts again, and its window no longer owns them:
		// they must never come back as a copy made on the endpoint.
		files := make([]CopiedFile, 0, len(body.Files))
		for _, f := range body.Files {
			if !underStagingRoot(f.Path, h.s.opts.StagingRoot) {
				files = append(files, f)
			}
		}
		if len(files) > MaxCopiedFiles {
			files = files[:MaxCopiedFiles]
		}
		h.mu.Lock()
		h.copied = files
		offer := h.offerLocked()
		to := h.clipboardLocked()
		h.mu.Unlock()
		broadcast(to, offer)
	default:
		h.mu.Lock()
		to := h.grantedLocked()
		h.mu.Unlock()
		broadcast(to, frame)
	}
	return nil
}

func (h *hub) grantedLocked() []*Participant {
	out := make([]*Participant, 0, len(h.participants))
	for _, p := range h.participants {
		if p.granted {
			out = append(out, p)
		}
	}
	return out
}

func (h *hub) clipboardLocked() []*Participant {
	out := make([]*Participant, 0, len(h.participants))
	for _, p := range h.participants {
		if p.granted && p.opts.ClipboardEnabled {
			out = append(out, p)
		}
	}
	return out
}

// offerLocked is the FrameClipboardFiles for the files copied on the endpoint now: files by index, folders only counted.
func (h *hub) offerLocked() []byte {
	body := ClipboardFilesBody{Files: []OfferedFile{}}
	for i, f := range h.copied {
		if f.Dir {
			body.Folders++
			continue
		}
		body.Files = append(body.Files, OfferedFile{Index: i, Name: f.Name, Size: f.Size})
	}
	return jsonFrame(FrameClipboardFiles, body)
}

// ack records that a technician drew a frame, and lets the helper send the next one when every technician drew it, or when the ones who
// did not lag behind too far.
func (h *hub) ack(p *Participant, number uint32) {
	h.mu.Lock()
	if !p.awaiting || p.awaitFrame != number {
		h.mu.Unlock()
		return
	}
	p.awaiting = false
	if number == h.frame {
		h.frameAcks++
	}
	forward := h.readyLocked()
	h.mu.Unlock()
	if forward {
		h.controller.Handle(h.ctx, jsonFrame(FrameAck, AckBody{Frame: number}))
	}
}

// readyLocked decides whether the last frame may be acknowledged to the helper, and marks it so when it may.
func (h *hub) readyLocked() bool {
	if h.forwarded {
		return false
	}
	now := h.s.opts.Now()
	waiting := 0
	for _, p := range h.participants {
		if !p.granted || !p.awaiting || p.awaitFrame != h.frame {
			continue
		}
		// Someone who already drew it must not wait for a technician far behind; nobody drew it yet: the helper's own timeout applies.
		if h.frameAcks == 0 || now.Sub(p.awaitSince) < h.s.opts.LagAllowance {
			waiting++
		}
	}
	if waiting > 0 {
		return false
	}
	h.forwarded = true
	return true
}

// tick releases a frame held back by a lagging technician.
func (h *hub) tick() {
	ticker := time.NewTicker(h.s.opts.Tick)
	defer ticker.Stop()
	for {
		select {
		case <-h.ctx.Done():
			return
		case <-ticker.C:
		}
		h.mu.Lock()
		forward, number := h.readyLocked(), h.frame
		h.mu.Unlock()
		if forward {
			h.controller.Handle(h.ctx, jsonFrame(FrameAck, AckBody{Frame: number}))
		}
	}
}

// askConsent asks the person on the Windows session to allow the first technician, then lets everyone in or ends every waiting session.
func (h *hub) askConsent(first *Participant) {
	session := h.controller.CurrentSession()
	user := ""
	if h.s.opts.SessionUser != nil {
		user = h.s.opts.SessionUser(session)
	}
	timeout := first.opts.ConsentTimeout
	if timeout <= 0 {
		timeout = 30 * time.Second
	}

	var (
		granted bool
		action  string
		detail  string
		message string
	)
	switch {
	case user == "":
		// Nobody is there to ask (the sign-in screen): access at once (decided 2026-09-17).
		granted, action = true, "consent.not_asked"
		detail = "Nobody is signed in on the Windows session."
		message = "Nobody is signed in on this Windows session, so no consent was asked."
	case h.s.opts.Consent == nil:
		action, detail = "consent.failed", "The endpoint cannot ask for consent."
		message = "This endpoint cannot ask for consent, and the policy requires it. The session was ended."
	default:
		h.mu.Lock()
		waiting := h.pendingLocked()
		h.mu.Unlock()
		broadcast(waiting, jsonFrame(FrameConsent, ConsentBody{State: "waiting", User: user, SecondsLeft: int(timeout / time.Second),
			Message: fmt.Sprintf("Waiting for %s to allow the session. Access is granted after %d seconds without an answer.", user, int(timeout/time.Second))}))
		answer, err := h.s.opts.Consent(h.ctx, session, first.opts.Technician, timeout)
		switch {
		case err != nil:
			h.s.opts.Logger.Warn("could not ask for remote control consent", "session", session, "error", err)
			action, detail = "consent.failed", "The prompt could not be shown: "+err.Error()
			message = fmt.Sprintf("The endpoint could not ask %s for consent (%s), and the policy requires it. The session was ended.", user, err)
		case answer == ConsentAllowed:
			granted, action, detail = true, "consent.granted", "Allowed by the user."
			message = user + " allowed the session."
		case answer == ConsentTimedOut:
			granted, action = true, "consent.timeout"
			detail = fmt.Sprintf("No answer within %d seconds; access granted by policy.", int(timeout/time.Second))
			message = fmt.Sprintf("%s did not answer within %d seconds, so access was granted by policy.", user, int(timeout/time.Second))
		default:
			action, detail = "consent.refused", "Refused by the user."
			message = user + " refused the remote control session."
		}
	}
	first.opts.Report(action, user, detail)

	h.mu.Lock()
	if h.closed {
		h.mu.Unlock()
		return
	}
	waiting := h.pendingLocked()
	if granted {
		h.consent = consentGranted
		for _, p := range waiting {
			p.granted = true
		}
	} else {
		// A later technician is asked again.
		h.consent = consentNone
	}
	h.mu.Unlock()
	if granted {
		h.welcome(waiting, &ConsentBody{State: "granted", User: user, Message: message})
		return
	}
	for _, p := range waiting {
		p.opts.End(message)
	}
}

func (h *hub) pendingLocked() []*Participant {
	var out []*Participant
	for _, p := range h.participants {
		if !p.granted {
			out = append(out, p)
		}
	}
	return out
}

// welcome lets technicians see the screen: the consent outcome, who is in the session, the screen as it is, the copied files, the banner,
// and the Start they sent while they waited.
func (h *hub) welcome(joined []*Participant, consent *ConsentBody) {
	h.mu.Lock()
	info, offer := h.info, []byte(nil)
	if len(h.copied) > 0 {
		offer = h.offerLocked()
	}
	starts := make([][]byte, 0, len(joined))
	for _, p := range joined {
		if p.pendingStart != nil {
			starts = append(starts, p.pendingStart)
			p.pendingStart = nil
		}
	}
	h.mu.Unlock()
	for _, p := range joined {
		if consent != nil {
			p.enqueue(jsonFrame(FrameConsent, *consent))
		}
		if info != nil {
			p.enqueue(info)
		}
		if offer != nil && p.opts.ClipboardEnabled {
			p.enqueue(offer)
		}
	}
	h.announce()
	for _, start := range starts {
		h.controller.Handle(h.ctx, start)
	}
}

// announce tells every technician who is in the session and shows the names on the banner.
func (h *hub) announce() {
	h.mu.Lock()
	granted := h.grantedLocked()
	names := make([]string, 0, len(granted))
	list := make([]ParticipantInfo, 0, len(granted))
	for _, p := range granted {
		names = append(names, p.opts.Technician)
		list = append(list, ParticipantInfo{ID: p.opts.ParticipantID, Name: p.opts.Technician})
	}
	var banner []byte
	if h.banner {
		body, _ := json.Marshal(BannerBody{Names: names})
		if string(body) != h.lastBanner {
			h.lastBanner = string(body)
			banner = append([]byte{FrameBanner}, body...)
		}
	}
	h.mu.Unlock()
	for _, p := range granted {
		mine := make([]ParticipantInfo, len(list))
		copy(mine, list)
		for i := range mine {
			mine[i].You = mine[i].ID == p.opts.ParticipantID
		}
		p.enqueue(jsonFrame(FrameParticipants, map[string]any{"participants": mine}))
	}
	if banner != nil {
		h.controller.Handle(h.ctx, banner)
	}
}

// pointer shows the other technicians where this one points.
func (h *hub) pointer(p *Participant, body []byte) {
	var pos PointerBody
	if json.Unmarshal(body, &pos) != nil || pos.X < 0 || pos.Y < 0 {
		return
	}
	now := h.s.opts.Now()
	h.mu.Lock()
	if now.Sub(p.lastPointer) < pointerInterval {
		h.mu.Unlock()
		return
	}
	p.lastPointer = now
	others := make([]*Participant, 0, len(h.participants))
	for _, o := range h.participants {
		if o != p && o.granted {
			others = append(others, o)
		}
	}
	h.mu.Unlock()
	if len(others) > 0 {
		broadcast(others, jsonFrame(FramePeerPointer, PeerPointerBody{ID: p.opts.ParticipantID, X: pos.X, Y: pos.Y}))
	}
}

// leave removes a technician. The keys they held are released; the last technician ends the helper and deletes the pasted files.
func (h *hub) leave(p *Participant) {
	h.mu.Lock()
	for i, o := range h.participants {
		if o == p {
			h.participants = append(h.participants[:i], h.participants[i+1:]...)
			break
		}
	}
	empty := len(h.participants) == 0
	if empty {
		h.closed = true
	}
	forward, number := h.readyLocked(), h.frame
	staging := h.staging
	h.mu.Unlock()

	if !empty {
		h.controller.Handle(h.ctx, []byte{FrameReleaseKeys})
		h.announce()
		if forward {
			h.controller.Handle(h.ctx, jsonFrame(FrameAck, AckBody{Frame: number}))
		}
		return
	}
	h.s.mu.Lock()
	if h.s.hubs[h.id] == h {
		delete(h.s.hubs, h.id)
	}
	h.s.mu.Unlock()
	h.cancel()
	h.controller.Close()
	if staging != "" {
		if err := os.RemoveAll(staging); err != nil {
			h.s.opts.Logger.Warn("could not delete the files pasted into a remote control session", "folder", staging, "error", err)
		}
	}
}

// Participant is one technician in a remote control session. It serves their frames and takes the files they paste.
type Participant struct {
	h    *hub
	opts JoinOptions
	out  chan []byte
	done chan struct{}
	once sync.Once

	// Guarded by h.mu.
	granted      bool
	pendingStart []byte
	awaiting     bool
	awaitFrame   uint32
	awaitSince   time.Time
	lastPointer  time.Time
}

// Handle takes one remote control frame of this technician's browser.
func (p *Participant) Handle(ctx context.Context, frame []byte) {
	if len(frame) == 0 || !FromBrowser(frame[0]) {
		return
	}
	h := p.h
	h.mu.Lock()
	if !p.granted {
		// Nothing reaches the screen before consent; the monitor the technician asked for is shown once it is given.
		if frame[0] == FrameStart {
			p.pendingStart = append([]byte(nil), frame...)
		}
		h.mu.Unlock()
		return
	}
	h.mu.Unlock()
	switch frame[0] {
	case FrameAck:
		var ack AckBody
		if json.Unmarshal(frame[1:], &ack) == nil {
			h.ack(p, ack.Frame)
		}
	case FramePointer:
		h.controller.Handle(h.ctx, frame)
		h.pointer(p, frame[1:])
	case FrameClipboard:
		if !p.opts.ClipboardEnabled || len(frame)-1 > MaxClipboardBytes {
			return
		}
		h.controller.Handle(h.ctx, frame)
	default:
		h.controller.Handle(h.ctx, frame)
	}
}

// Close takes the technician out of the session.
func (p *Participant) Close() {
	p.once.Do(func() {
		close(p.done)
		p.h.leave(p)
	})
}

// ClipboardEnabled reports whether this technician may use the clipboard.
func (p *Participant) ClipboardEnabled() bool { return p.opts.ClipboardEnabled }

// StagingBatch creates a new folder for files this technician pastes. It is readable by the user of the shown Windows session, and deleted
// with everything in it when the session ends.
func (p *Participant) StagingBatch() (string, error) {
	h := p.h
	if h.s.opts.StagingRoot == "" || h.s.opts.Stage == nil {
		return "", errors.New("files cannot be pasted on this endpoint")
	}
	session := h.controller.CurrentSession()
	h.mu.Lock()
	defer h.mu.Unlock()
	if h.closed {
		return "", errors.New("the session is closing")
	}
	if h.batches >= maxBatches {
		return "", errors.New("this session pasted too many sets of files; end it and open a new one")
	}
	if h.staging == "" {
		dir := filepath.Join(h.s.opts.StagingRoot, sanitizeID(h.id))
		if err := h.s.opts.Stage(dir, session); err != nil {
			return "", fmt.Errorf("the endpoint could not prepare a folder for the files: %w", err)
		}
		h.staging = dir
	}
	h.batches++
	batch := filepath.Join(h.staging, strconv.Itoa(h.batches))
	if err := os.Mkdir(batch, 0o700); err != nil {
		return "", fmt.Errorf("the endpoint could not prepare a folder for the files: %w", err)
	}
	return batch, nil
}

// PlaceFiles puts staged files on the endpoint clipboard, so the person at the endpoint (or the technician) pastes them.
func (p *Participant) PlaceFiles(paths []string) error {
	h := p.h
	if !h.controller.Running() {
		return errors.New("the screen of the endpoint is not shown; the files cannot be placed on its clipboard")
	}
	h.controller.Handle(h.ctx, jsonFrame(FramePlaceFiles, PlaceFilesBody{Paths: paths}))
	return nil
}

// CopiedFile returns the path of a file copied on the endpoint, by the index the browser was offered.
func (p *Participant) CopiedFile(index int) (string, error) {
	h := p.h
	h.mu.Lock()
	defer h.mu.Unlock()
	if index < 0 || index >= len(h.copied) {
		return "", errors.New("that file is no longer on the endpoint clipboard; copy it again")
	}
	f := h.copied[index]
	if f.Dir {
		return "", errors.New("a folder cannot be downloaded; copy the files in it instead")
	}
	return f.Path, nil
}

// enqueue passes a frame to the technician's sender. A technician whose connection cannot keep up is disconnected.
func (p *Participant) enqueue(frame []byte) {
	select {
	case <-p.done:
		return
	default:
	}
	select {
	case p.out <- frame:
	default:
		p.once.Do(func() {
			close(p.done)
			safego.Go(p.h.s.opts.Logger, "remote control slow technician", func() {
				p.opts.End("The connection is too slow to follow the screen. Open the session again.")
				p.h.leave(p)
			})
		})
	}
}

func (p *Participant) sendLoop() {
	for {
		select {
		case <-p.done:
			return
		case frame := <-p.out:
			if err := p.opts.Send(frame); err != nil {
				return // the session ends on its own when its relay is gone
			}
		}
	}
}

func broadcast(to []*Participant, frame []byte) {
	for _, p := range to {
		p.enqueue(frame)
	}
}

func jsonFrame(kind byte, body any) []byte {
	data, _ := json.Marshal(body)
	return append([]byte{kind}, data...)
}

// updateHeader reads the frame number and the last flag of a FrameUpdate (see AppendUpdate).
func updateHeader(frame []byte) (number uint32, last bool, ok bool) {
	if len(frame) < updateHeaderBytes || frame[0] != FrameUpdate {
		return 0, false, false
	}
	return binary.BigEndian.Uint32(frame[1:5]), frame[5]&FlagLast != 0, true
}

// underStagingRoot reports whether a path is one Fleeto staged for a paste into this endpoint.
func underStagingRoot(path, root string) bool {
	if root == "" || path == "" {
		return false
	}
	clean := strings.ToLower(filepath.Clean(path))
	base := strings.ToLower(filepath.Clean(root))
	return clean == base || strings.HasPrefix(clean, base+string(filepath.Separator))
}

// sanitizeID keeps the characters of a GUID only, so a session id can never name another folder.
func sanitizeID(id string) string {
	out := make([]byte, 0, len(id))
	for i := 0; i < len(id); i++ {
		c := id[i]
		if (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || c == '-' {
			out = append(out, c)
		}
	}
	if len(out) == 0 {
		return "session"
	}
	return string(out)
}

// ConsentMessage is the text of the consent prompt.
func ConsentMessage(technician string, seconds int) string {
	if technician == "" {
		technician = "A technician"
	}
	return fmt.Sprintf("%s wants to view and control this computer through Fleeto remote control.\n\n"+
		"Allow the session?\n\nWithout an answer within %d seconds, access is granted.", technician, seconds)
}

// CleanStaging deletes files pasted into sessions that did not end cleanly (the agent stopped); called when the agent service starts.
func CleanStaging(root string) error {
	if root == "" {
		return nil
	}
	if err := os.RemoveAll(root); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}
