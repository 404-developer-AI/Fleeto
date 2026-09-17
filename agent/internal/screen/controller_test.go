package screen

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// fakeHelper is a helper process over in-memory pipes: the test plays the helper on the other ends.
type fakeHelper struct {
	session  uint32
	inR      *io.PipeReader
	inW      *io.PipeWriter
	outR     *io.PipeReader
	outW     *io.PipeWriter
	closed   atomic.Bool
	received chan []byte
}

func newFakeHelper(session uint32) *fakeHelper {
	h := &fakeHelper{session: session, received: make(chan []byte, 256)}
	h.inR, h.inW = io.Pipe()
	h.outR, h.outW = io.Pipe()
	go func() {
		for {
			frame, err := ReadFrame(h.inR)
			if err != nil {
				return
			}
			h.received <- frame
		}
	}()
	return h
}

func (h *fakeHelper) In() io.Writer  { return h.inW }
func (h *fakeHelper) Out() io.Reader { return h.outR }
func (h *fakeHelper) Close() error {
	if h.closed.CompareAndSwap(false, true) {
		_ = h.inW.Close()
		_ = h.outW.Close()
	}
	return nil
}

// crash ends the helper's output as if the process died.
func (h *fakeHelper) crash() { _ = h.outW.CloseWithError(errors.New("helper died")) }

func (h *fakeHelper) expect(t *testing.T, kind byte) []byte {
	t.Helper()
	select {
	case frame := <-h.received:
		if frame[0] != kind {
			t.Fatalf("the helper got frame %x, want %x", frame[0], kind)
		}
		return frame
	case <-time.After(5 * time.Second):
		t.Fatalf("the helper got no frame %x", kind)
		return nil
	}
}

type controllerHarness struct {
	t        *testing.T
	mu       sync.Mutex
	helpers  []*fakeHelper
	console  atomic.Uint32
	toBrowse chan []byte
	sas      error
	c        *Controller
}

func newControllerHarness(t *testing.T, session uint32) *controllerHarness {
	h := &controllerHarness{t: t, toBrowse: make(chan []byte, 32)}
	h.console.Store(1)
	h.c = NewController(ControllerOptions{
		Send: func(frame []byte) error { h.toBrowse <- frame; return nil },
		Launch: func(_ context.Context, id uint32) (Helper, error) {
			helper := newFakeHelper(id)
			h.mu.Lock()
			h.helpers = append(h.helpers, helper)
			h.mu.Unlock()
			return helper, nil
		},
		Session:         session,
		ConsoleSession:  h.console.Load,
		SecureAttention: func() error { return h.sas },
		ConsoleCheck:    20 * time.Millisecond,
	})
	t.Cleanup(h.c.Close)
	return h
}

func (h *controllerHarness) helper(n int) *fakeHelper {
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

func (h *controllerHarness) browserGets(kind byte) []byte {
	h.t.Helper()
	select {
	case frame := <-h.toBrowse:
		if frame[0] != kind {
			h.t.Fatalf("the browser got frame %x (%s), want %x", frame[0], frame[1:], kind)
		}
		return frame
	case <-time.After(5 * time.Second):
		h.t.Fatalf("the browser got no frame %x", kind)
		return nil
	}
}

func TestTheHelperStartsInTheConsoleSessionOnStartAndFramesPassBothWays(t *testing.T) {
	h := newControllerHarness(t, 0)
	ctx := context.Background()
	h.c.Handle(ctx, []byte{FramePointer, '{', '}'}) // before Start: no helper, nothing to pass on
	h.c.Handle(ctx, append([]byte{FrameStart}, `{"monitor":0}`...))
	helper := h.helper(0)
	if helper.session != 1 {
		t.Fatalf("the helper started in session %d, want the console session 1", helper.session)
	}
	helper.expect(t, FrameStart)
	h.c.Handle(ctx, append([]byte{FrameKey}, `{"code":"KeyA","key":"a","down":true}`...))
	helper.expect(t, FrameKey)

	// The helper's frames reach the browser; a frame type the helper may not send is dropped.
	go func() {
		_ = WriteFrame(helper.outW, []byte{FrameEndForTest})
		_ = WriteFrame(helper.outW, append([]byte{FrameInfo}, `{"monitor":0}`...))
	}()
	h.browserGets(FrameInfo)
}

// FrameEndForTest is a remote background frame type (End) that must never come from a helper.
const FrameEndForTest byte = 0x09

func TestARemoteSessionStartsTheHelperInThatSession(t *testing.T) {
	h := newControllerHarness(t, 3)
	h.c.Handle(context.Background(), append([]byte{FrameStart}, `{"monitor":-1}`...))
	if helper := h.helper(0); helper.session != 3 {
		t.Fatalf("session %d, want 3", helper.session)
	}
}

func TestAHelperThatStopsIsStartedAgainWithTheLastStart(t *testing.T) {
	h := newControllerHarness(t, 0)
	h.c.Handle(context.Background(), append([]byte{FrameStart}, `{"monitor":1}`...))
	first := h.helper(0)
	first.expect(t, FrameStart)
	first.crash()
	second := h.helper(1)
	start := second.expect(t, FrameStart)
	var body StartBody
	if json.Unmarshal(start[1:], &body) != nil || body.Monitor != 1 {
		t.Fatalf("the restarted helper got %s", start[1:])
	}
	if !first.closed.Load() {
		t.Fatal("the stopped helper was not closed")
	}
}

func TestTheConsoleIsFollowedToAnotherSession(t *testing.T) {
	h := newControllerHarness(t, 0)
	h.c.Handle(context.Background(), append([]byte{FrameStart}, `{"monitor":0}`...))
	first := h.helper(0)
	first.expect(t, FrameStart)
	h.console.Store(5)
	second := h.helper(1)
	if second.session != 5 {
		t.Fatalf("session %d, want 5", second.session)
	}
	second.expect(t, FrameStart)
	notice := h.browserGets(FrameNotice)
	if !strings.Contains(string(notice), "session 5") {
		t.Fatalf("notice %s", notice[1:])
	}
	if !first.closed.Load() {
		t.Fatal("the helper of the old console session was not closed")
	}
}

func TestCtrlAltDelIsHandledByTheServiceAndARefusalIsExplained(t *testing.T) {
	h := newControllerHarness(t, 0)
	h.sas = errors.New("Ctrl+Alt+Del is turned off on this endpoint")
	h.c.Handle(context.Background(), []byte{FrameSecureAttention})
	notice := h.browserGets(FrameNotice)
	if !strings.Contains(string(notice), "turned off") {
		t.Fatalf("notice %s", notice[1:])
	}
	h.mu.Lock()
	started := len(h.helpers)
	h.mu.Unlock()
	if started != 0 {
		t.Fatal("Ctrl+Alt+Del started a helper")
	}
}

func TestClosingTheControllerClosesTheHelper(t *testing.T) {
	h := newControllerHarness(t, 0)
	h.c.Handle(context.Background(), append([]byte{FrameStart}, `{"monitor":0}`...))
	helper := h.helper(0)
	helper.expect(t, FrameStart)
	h.c.Close()
	if !helper.closed.Load() {
		t.Fatal("the helper is still running")
	}
	h.c.Handle(context.Background(), append([]byte{FrameStart}, `{"monitor":0}`...))
	time.Sleep(50 * time.Millisecond)
	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.helpers) != 1 {
		t.Fatal("a closed controller started a helper")
	}
}

func TestALauncherThatPanicsBecomesANoticeNotADeadSession(t *testing.T) {
	h := newControllerHarness(t, 0)
	h.c.opts.Launch = func(context.Context, uint32) (Helper, error) { panic("boom in a Win32 call") }
	h.c.Handle(context.Background(), append([]byte{FrameStart}, `{"monitor":0}`...))
	notice := h.browserGets(FrameNotice)
	if !strings.Contains(string(notice), "stopped unexpectedly") {
		t.Fatalf("notice %s", notice[1:])
	}
	// The controller is still usable: a working launcher afterwards starts a helper.
	h.c.opts.Launch = func(_ context.Context, id uint32) (Helper, error) {
		helper := newFakeHelper(id)
		h.mu.Lock()
		h.helpers = append(h.helpers, helper)
		h.mu.Unlock()
		return helper, nil
	}
	h.c.Handle(context.Background(), append([]byte{FrameStart}, `{"monitor":0}`...))
	h.helper(0).expect(t, FrameStart)
}
