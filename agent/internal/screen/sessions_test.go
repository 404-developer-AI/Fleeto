package screen

import (
	"context"
	"encoding/json"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

type sessionsHarness struct {
	t       *testing.T
	mu      sync.Mutex
	helpers []*fakeHelper
	s       *Sessions
	user    atomic.Value
	consent func(ctx context.Context, session uint32, technician string, timeout time.Duration) (ConsentAnswer, error)
	root    string
	staged  atomic.Int32
}

func newSessionsHarness(t *testing.T, configure func(*SessionsOptions)) *sessionsHarness {
	h := &sessionsHarness{t: t, root: t.TempDir()}
	h.user.Store(`ACME\anna`)
	opts := SessionsOptions{
		Launch: func(_ context.Context, id uint32) (Helper, error) {
			helper := newFakeHelper(id)
			h.mu.Lock()
			h.helpers = append(h.helpers, helper)
			h.mu.Unlock()
			return helper, nil
		},
		ConsoleSession:  func() uint32 { return 1 },
		SecureAttention: func() error { return nil },
		SessionUser:     func(uint32) string { return h.user.Load().(string) },
		Consent: func(ctx context.Context, session uint32, technician string, timeout time.Duration) (ConsentAnswer, error) {
			return h.consent(ctx, session, technician, timeout)
		},
		StagingRoot: h.root,
		Stage: func(dir string, _ uint32) error {
			h.staged.Add(1)
			return os.MkdirAll(dir, 0o700)
		},
		ConsoleCheck: time.Hour,
	}
	if configure != nil {
		configure(&opts)
	}
	h.s = NewSessions(opts)
	return h
}

func (h *sessionsHarness) helper(n int) *fakeHelper {
	h.t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		h.mu.Lock()
		if len(h.helpers) > n {
			helper := h.helpers[n]
			h.mu.Unlock()
			return helper
		}
		h.mu.Unlock()
		time.Sleep(5 * time.Millisecond)
	}
	h.t.Fatalf("helper %d was not started", n)
	return nil
}

func (h *sessionsHarness) helperCount() int {
	h.mu.Lock()
	defer h.mu.Unlock()
	return len(h.helpers)
}

// technician is one browser in a test: the frames the endpoint sends it, and how its session ended.
type technician struct {
	t       *testing.T
	p       *Participant
	frames  chan []byte
	ended   chan string
	actions chan string
	block   chan struct{}
}

func (h *sessionsHarness) join(id, name string, edit func(*JoinOptions)) *technician {
	tech := &technician{t: h.t, frames: make(chan []byte, 1024), ended: make(chan string, 1), actions: make(chan string, 8)}
	opts := JoinOptions{
		SessionID: "11111111-1111-1111-1111-111111111111", ParticipantID: id, Technician: name, ClipboardEnabled: true,
		Send: func(frame []byte) error {
			if tech.block != nil {
				<-tech.block
			}
			tech.frames <- frame
			return nil
		},
		End:    func(reason string) { tech.ended <- reason },
		Report: func(action, target, detail string) { tech.actions <- action + " " + target },
	}
	if edit != nil {
		edit(&opts)
	}
	tech.p = h.s.Join(opts)
	h.t.Cleanup(tech.p.Close)
	return tech
}

// next returns the next frame of a kind, skipping others.
func (tech *technician) next(kind byte) []byte {
	tech.t.Helper()
	deadline := time.After(5 * time.Second)
	for {
		select {
		case frame := <-tech.frames:
			if frame[0] == kind {
				return frame
			}
		case <-deadline:
			tech.t.Fatalf("no frame %x arrived", kind)
			return nil
		}
	}
}

// none fails when a frame of a kind arrives within a short time.
func (tech *technician) none(kind byte) {
	tech.t.Helper()
	deadline := time.After(150 * time.Millisecond)
	for {
		select {
		case frame := <-tech.frames:
			if frame[0] == kind {
				tech.t.Fatalf("an unexpected frame %x arrived: %s", kind, frame[1:])
			}
		case <-deadline:
			return
		}
	}
}

// waitFor returns the next frame of a kind the helper gets, skipping others.
func waitFor(t *testing.T, helper *fakeHelper, kind byte) []byte {
	t.Helper()
	deadline := time.After(5 * time.Second)
	for {
		select {
		case frame := <-helper.received:
			if frame[0] == kind {
				return frame
			}
		case <-deadline:
			t.Fatalf("the helper got no frame %x", kind)
			return nil
		}
	}
}

func noHelperFrame(t *testing.T, helper *fakeHelper, kind byte) {
	t.Helper()
	deadline := time.After(150 * time.Millisecond)
	for {
		select {
		case frame := <-helper.received:
			if frame[0] == kind {
				t.Fatalf("the helper got an unexpected frame %x: %s", kind, frame[1:])
			}
		case <-deadline:
			return
		}
	}
}

func start(tech *technician) {
	tech.p.Handle(context.Background(), append([]byte{FrameStart}, `{"monitor":0}`...))
}

func lastUpdate(t *testing.T, frame uint32) []byte {
	updates, err := Updates(frame, nil)
	if err != nil || len(updates) != 1 {
		t.Fatalf("updates: %v", err)
	}
	return updates[0]
}

func participantsOf(t *testing.T, frame []byte) []ParticipantInfo {
	var body struct {
		Participants []ParticipantInfo `json:"participants"`
	}
	if err := json.Unmarshal(frame[1:], &body); err != nil {
		t.Fatal(err)
	}
	return body.Participants
}

func TestTwoTechniciansShareOneHelperAndTheHelperWaitsForBothToDraw(t *testing.T) {
	h := newSessionsHarness(t, func(o *SessionsOptions) { o.LagAllowance = time.Hour })
	anna := h.join("a", "Anna", nil)
	start(anna)
	helper := h.helper(0)
	waitFor(t, helper, FrameStart)
	bert := h.join("b", "Bert", nil)
	start(bert)
	waitFor(t, helper, FrameStart) // the second Start makes the helper send a whole frame for the newcomer
	if h.helperCount() != 1 || h.s.Count() != 1 {
		t.Fatalf("%d helpers for %d sessions, want one of each", h.helperCount(), h.s.Count())
	}

	list := participantsOf(t, bert.next(FrameParticipants))
	if len(list) != 2 || list[0].Name != "Anna" || list[0].You || list[1].Name != "Bert" || !list[1].You {
		t.Fatalf("participants %+v", list)
	}

	go func() {
		_ = WriteFrame(helper.outW, append([]byte{FrameInfo}, `{"monitor":0,"width":10,"height":10}`...))
		_ = WriteFrame(helper.outW, lastUpdate(t, 7))
	}()
	anna.next(FrameInfo)
	bert.next(FrameInfo)
	anna.next(FrameUpdate)
	bert.next(FrameUpdate)

	anna.p.Handle(context.Background(), append([]byte{FrameAck}, `{"frame":7}`...))
	noHelperFrame(t, helper, FrameAck)
	bert.p.Handle(context.Background(), append([]byte{FrameAck}, `{"frame":7}`...))
	ack := waitFor(t, helper, FrameAck)
	if !strings.Contains(string(ack), `"frame":7`) {
		t.Fatalf("ack %s", ack[1:])
	}

	// Input of either technician reaches the one helper.
	bert.p.Handle(context.Background(), append([]byte{FrameKey}, `{"code":"KeyA","key":"a","down":true}`...))
	waitFor(t, helper, FrameKey)
}

func TestATechnicianFarBehindDoesNotHoldTheOthersBack(t *testing.T) {
	h := newSessionsHarness(t, func(o *SessionsOptions) {
		o.LagAllowance = 50 * time.Millisecond
		o.Tick = 10 * time.Millisecond
	})
	anna := h.join("a", "Anna", nil)
	bert := h.join("b", "Bert", nil)
	start(anna)
	helper := h.helper(0)
	go func() { _ = WriteFrame(helper.outW, lastUpdate(t, 3)) }()
	anna.next(FrameUpdate)
	bert.next(FrameUpdate)
	anna.p.Handle(context.Background(), append([]byte{FrameAck}, `{"frame":3}`...))
	waitFor(t, helper, FrameAck) // Bert never draws it, the helper goes on after the allowance
}

func TestNobodyDrawingAFrameKeepsTheHelperWaiting(t *testing.T) {
	h := newSessionsHarness(t, func(o *SessionsOptions) {
		o.LagAllowance = 10 * time.Millisecond
		o.Tick = 10 * time.Millisecond
	})
	anna := h.join("a", "Anna", nil)
	start(anna)
	helper := h.helper(0)
	go func() { _ = WriteFrame(helper.outW, lastUpdate(t, 1)) }()
	anna.next(FrameUpdate)
	noHelperFrame(t, helper, FrameAck) // the helper's own timeout decides, not the hub
}

func TestEachTechnicianSeesWhereTheOthersPoint(t *testing.T) {
	h := newSessionsHarness(t, nil)
	anna := h.join("a", "Anna", nil)
	bert := h.join("b", "Bert", nil)
	start(anna)
	helper := h.helper(0)
	anna.p.Handle(context.Background(), append([]byte{FramePointer}, `{"x":12,"y":34,"buttons":0}`...))
	waitFor(t, helper, FramePointer)
	var pointer PeerPointerBody
	if err := json.Unmarshal(bert.next(FramePeerPointer)[1:], &pointer); err != nil || pointer.ID != "a" || pointer.X != 12 || pointer.Y != 34 {
		t.Fatalf("pointer %+v %v", pointer, err)
	}
	anna.none(FramePeerPointer)
}

func TestOnlyTheFirstTechnicianIsAskedForConsent(t *testing.T) {
	answer := make(chan ConsentAnswer)
	asked := make(chan string, 4)
	h := newSessionsHarness(t, nil)
	h.consent = func(_ context.Context, session uint32, technician string, timeout time.Duration) (ConsentAnswer, error) {
		asked <- technician
		if session != 1 || timeout != 45*time.Second {
			t.Errorf("asked on session %d with %s", session, timeout)
		}
		return <-answer, nil
	}
	consent := func(o *JoinOptions) {
		o.ConsentRequired = true
		o.ConsentTimeout = 45 * time.Second
	}
	anna := h.join("a", "Anna", consent)
	if who := <-asked; who != "Anna" {
		t.Fatalf("asked for %q", who)
	}
	waiting := anna.next(FrameConsent)
	if !strings.Contains(string(waiting), `"waiting"`) || !strings.Contains(string(waiting), `ACME\\anna`) {
		t.Fatalf("consent %s", waiting[1:])
	}
	start(anna)
	anna.p.Handle(context.Background(), append([]byte{FrameKey}, `{"code":"KeyA","key":"a","down":true}`...))
	time.Sleep(50 * time.Millisecond)
	if h.helperCount() != 0 {
		t.Fatal("the screen was shown before consent")
	}

	// A technician who joins while the prompt is open waits for the same answer.
	bert := h.join("b", "Bert", consent)
	bert.next(FrameConsent)
	answer <- ConsentAllowed
	granted := anna.next(FrameConsent)
	if !strings.Contains(string(granted), `"granted"`) {
		t.Fatalf("consent %s", granted[1:])
	}
	bert.next(FrameConsent)
	helper := h.helper(0)
	waitFor(t, helper, FrameStart) // the Start sent while waiting
	if action := <-anna.actions; action != `consent.granted ACME\anna` {
		t.Fatalf("reported %q", action)
	}

	// Joining the running session later: no prompt.
	carl := h.join("c", "Carl", consent)
	carl.next(FrameParticipants)
	select {
	case who := <-asked:
		t.Fatalf("asked again for %q", who)
	case <-time.After(100 * time.Millisecond):
	}
}

func TestARefusalEndsTheSessionAndNothingIsShown(t *testing.T) {
	h := newSessionsHarness(t, nil)
	h.consent = func(context.Context, uint32, string, time.Duration) (ConsentAnswer, error) {
		return ConsentRefused, nil
	}
	anna := h.join("a", "Anna", func(o *JoinOptions) { o.ConsentRequired = true })
	start(anna)
	select {
	case reason := <-anna.ended:
		if !strings.Contains(reason, `ACME\anna refused`) {
			t.Fatalf("ended with %q", reason)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the session did not end")
	}
	if action := <-anna.actions; !strings.HasPrefix(action, "consent.refused") {
		t.Fatalf("reported %q", action)
	}
	if h.helperCount() != 0 {
		t.Fatal("a refused session showed the screen")
	}
}

func TestWithNobodySignedInOrNoAnswerAccessIsGranted(t *testing.T) {
	h := newSessionsHarness(t, nil)
	h.user.Store("")
	h.consent = func(context.Context, uint32, string, time.Duration) (ConsentAnswer, error) {
		t.Error("consent was asked with nobody signed in")
		return ConsentRefused, nil
	}
	anna := h.join("a", "Anna", func(o *JoinOptions) { o.ConsentRequired = true })
	if granted := anna.next(FrameConsent); !strings.Contains(string(granted), "Nobody is signed in") {
		t.Fatalf("consent %s", granted[1:])
	}
	if action := <-anna.actions; !strings.HasPrefix(action, "consent.not_asked") {
		t.Fatalf("reported %q", action)
	}

	timedOut := newSessionsHarness(t, nil)
	timedOut.consent = func(context.Context, uint32, string, time.Duration) (ConsentAnswer, error) {
		return ConsentTimedOut, nil
	}
	bert := timedOut.join("b", "Bert", func(o *JoinOptions) {
		o.ConsentRequired = true
		o.SessionID = "22222222-2222-2222-2222-222222222222"
	})
	for {
		frame := bert.next(FrameConsent)
		if strings.Contains(string(frame), `"granted"`) {
			break
		}
	}
	if action := <-bert.actions; !strings.HasPrefix(action, "consent.timeout") {
		t.Fatalf("reported %q", action)
	}
}

func TestTheBannerNamesEveryTechnicianAndALeaveReleasesKeys(t *testing.T) {
	h := newSessionsHarness(t, nil)
	banner := func(o *JoinOptions) { o.BannerVisible = true }
	anna := h.join("a", "Anna", banner)
	start(anna)
	helper := h.helper(0)
	names := func() []string {
		var body BannerBody
		if err := json.Unmarshal(waitFor(t, helper, FrameBanner)[1:], &body); err != nil {
			t.Fatal(err)
		}
		return body.Names
	}
	if got := names(); len(got) != 1 || got[0] != "Anna" {
		t.Fatalf("banner %v", got)
	}
	bert := h.join("b", "Bert", nil)
	if got := names(); len(got) != 2 || got[1] != "Bert" {
		t.Fatalf("banner %v", got)
	}
	bert.p.Close()
	waitFor(t, helper, FrameReleaseKeys)
	if got := names(); len(got) != 1 || got[0] != "Anna" {
		t.Fatalf("banner %v", got)
	}
	if list := participantsOf(t, anna.next(FrameParticipants)); len(list) == 0 {
		t.Fatal("no participant list after the leave")
	}
}

func TestNoBannerWhenTheFirstTechniciansTokenSaysSo(t *testing.T) {
	h := newSessionsHarness(t, nil)
	anna := h.join("a", "Anna", nil)
	start(anna)
	helper := h.helper(0)
	h.join("b", "Bert", func(o *JoinOptions) { o.BannerVisible = true })
	noHelperFrame(t, helper, FrameBanner)
}

func TestClipboardTextFollowsEachTechniciansToken(t *testing.T) {
	h := newSessionsHarness(t, nil)
	anna := h.join("a", "Anna", nil)
	bert := h.join("b", "Bert", func(o *JoinOptions) { o.ClipboardEnabled = false })
	start(anna)
	helper := h.helper(0)

	go func() { _ = WriteFrame(helper.outW, append([]byte{FrameClipboard}, "copied on the endpoint"...)) }()
	if text := anna.next(FrameClipboard); string(text[1:]) != "copied on the endpoint" {
		t.Fatalf("text %q", text[1:])
	}
	bert.none(FrameClipboard)

	bert.p.Handle(context.Background(), append([]byte{FrameClipboard}, "from bert"...))
	noHelperFrame(t, helper, FrameClipboard)
	anna.p.Handle(context.Background(), append([]byte{FrameClipboard}, "from anna"...))
	if got := waitFor(t, helper, FrameClipboard); string(got[1:]) != "from anna" {
		t.Fatalf("helper got %q", got[1:])
	}
	anna.p.Handle(context.Background(), append([]byte{FrameClipboard}, strings.Repeat("x", MaxClipboardBytes+1)...))
	noHelperFrame(t, helper, FrameClipboard)
}

func TestCopiedFilesAreOfferedAndPastedFilesLiveAsLongAsTheSession(t *testing.T) {
	h := newSessionsHarness(t, nil)
	anna := h.join("a", "Anna", nil)
	start(anna)
	helper := h.helper(0)

	copied := CopiedFilesBody{Files: []CopiedFile{{Path: `C:\Users\anna\report.pdf`, Name: "report.pdf", Size: 2048}, {Path: `C:\Users\anna\Photos`, Name: "Photos", Dir: true}}}
	go func() { _ = WriteFrame(helper.outW, jsonFrame(FrameCopiedFiles, copied)) }()
	var offer ClipboardFilesBody
	if err := json.Unmarshal(anna.next(FrameClipboardFiles)[1:], &offer); err != nil {
		t.Fatal(err)
	}
	if len(offer.Files) != 1 || offer.Files[0].Index != 0 || offer.Files[0].Name != "report.pdf" || offer.Folders != 1 {
		t.Fatalf("offer %+v", offer)
	}
	if path, err := anna.p.CopiedFile(0); err != nil || path != `C:\Users\anna\report.pdf` {
		t.Fatalf("copied file %q %v", path, err)
	}
	if _, err := anna.p.CopiedFile(1); err == nil {
		t.Fatal("a folder was offered for download")
	}
	if _, err := anna.p.CopiedFile(5); err == nil {
		t.Fatal("an unknown index was accepted")
	}

	// A technician who joins later is offered the same files.
	bert := h.join("b", "Bert", nil)
	bert.next(FrameClipboardFiles)

	batch, err := anna.p.StagingBatch()
	if err != nil {
		t.Fatal(err)
	}
	second, err := bert.p.StagingBatch()
	if err != nil || filepath.Dir(batch) != filepath.Dir(second) || batch == second || h.staged.Load() != 1 {
		t.Fatalf("batches %q %q (%v), staged %d times", batch, second, err, h.staged.Load())
	}
	if !strings.HasPrefix(batch, h.root) {
		t.Fatalf("batch %q outside the staging root", batch)
	}
	if err := anna.p.PlaceFiles([]string{filepath.Join(batch, "a.txt")}); err != nil {
		t.Fatal(err)
	}
	var place PlaceFilesBody
	if err := json.Unmarshal(waitFor(t, helper, FramePlaceFiles)[1:], &place); err != nil || len(place.Paths) != 1 {
		t.Fatalf("place %+v %v", place, err)
	}

	anna.p.Close()
	if _, err := os.Stat(filepath.Dir(batch)); err != nil {
		t.Fatal("the pasted files were deleted while a technician was still in the session")
	}
	bert.p.Close()
	if _, err := os.Stat(filepath.Dir(batch)); !os.IsNotExist(err) {
		t.Fatal("the pasted files were not deleted when the session ended")
	}
	if !helper.closed.Load() || h.s.Count() != 0 {
		t.Fatal("the helper or the session outlived the last technician")
	}
}

func TestASlowTechnicianIsDisconnectedAndTheOthersKeepTheScreen(t *testing.T) {
	h := newSessionsHarness(t, nil)
	anna := h.join("a", "Anna", nil)
	bert := h.join("b", "Bert", nil)
	start(anna)
	helper := h.helper(0)
	bert.block = make(chan struct{}) // Bert's connection stops
	t.Cleanup(func() { close(bert.block) })
	go func() {
		for i := 0; i < sendQueue+50; i++ {
			if WriteFrame(helper.outW, append([]byte{FrameNotice}, `{"message":"x"}`...)) != nil {
				return
			}
		}
	}()
	select {
	case reason := <-bert.ended:
		if !strings.Contains(reason, "too slow") {
			t.Fatalf("ended with %q", reason)
		}
	case <-time.After(5 * time.Second):
		t.Fatal("the slow technician was not disconnected")
	}
	for i := 0; i < sendQueue; i++ {
		anna.next(FrameNotice)
	}
}
