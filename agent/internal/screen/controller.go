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

	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// Helper is a running helper process: frames for it go to In, frames from it come out of Out.
type Helper interface {
	In() io.Writer
	Out() io.Reader
	// Close ends the helper and releases its resources; it is safe to call more than once.
	Close() error
}

// Launcher starts a helper in a Windows session.
type Launcher func(ctx context.Context, sessionID uint32) (Helper, error)

// ErrNotSupported is returned where remote control is not available on this platform.
var ErrNotSupported = errors.New("remote control is not supported on this platform yet")

const (
	// consoleCheck is how often a console session is checked for a switch to another Windows session (fast user switching, a sign-in).
	consoleCheck = 2 * time.Second
	// maxRestarts is how many times a helper that stopped is started again within restartWindow.
	maxRestarts   = 3
	restartWindow = time.Minute
	// restartDelay lets Windows settle before the helper starts again: a helper that ended with its Windows session must not be started
	// again in a session that is on its way out (found while testing 0.3.0 step 4).
	restartDelay = 300 * time.Millisecond
)

// ControllerOptions configure a Controller.
type ControllerOptions struct {
	// Send passes a frame to the browser over the encrypted session.
	Send func(frame []byte) error
	// Launch starts a helper; Session is the Windows session of the token, 0 for the console.
	Launch  Launcher
	Session uint32
	// ClipboardLaunch starts the process that serves the clipboard of the Windows session as the user signed in on it (0.3.0 step 4);
	// nil where the clipboard is not served.
	ClipboardLaunch Launcher
	// ConsoleSession returns the Windows session attached to the console now.
	ConsoleSession func() uint32
	// SessionExists reports whether a Windows session is still there; nil means it is assumed to be.
	SessionExists func(session uint32) bool
	// SecureAttention sends Ctrl+Alt+Del, or says why it cannot.
	SecureAttention func() error
	Logger          *slog.Logger
	Now             func() time.Time
	// ConsoleCheck overrides consoleCheck in tests.
	ConsoleCheck time.Duration
}

// Controller serves the screen of one remote control session inside the agent service: it starts the helper in the right Windows
// session when the browser first asks for the screen, forwards the browser's frames to it and its frames to the browser, follows the
// console to another session, starts a helper that stopped again, and handles Ctrl+Alt+Del itself.
type Controller struct {
	opts ControllerOptions

	mu         sync.Mutex
	helper     Helper
	clipboard  Helper
	clipTries  []time.Time
	current    uint32
	lastStart  []byte
	lastBanner []byte
	restarts   []time.Time
	closed     bool
	cancel     context.CancelFunc
	watching   bool
}

// NewController returns a controller; nothing starts until the browser sends FrameStart.
func NewController(opts ControllerOptions) *Controller {
	if opts.Logger == nil {
		opts.Logger = slog.New(slog.DiscardHandler)
	}
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.ConsoleCheck <= 0 {
		opts.ConsoleCheck = consoleCheck
	}
	return &Controller{opts: opts}
}

// Handle takes one frame for the helper: a browser frame, or a frame of the agent service itself (the banner, files for the clipboard).
func (c *Controller) Handle(ctx context.Context, frame []byte) {
	if len(frame) == 0 || !(FromBrowser(frame[0]) || FromAgent(frame[0])) {
		return
	}
	started := false
	switch frame[0] {
	case FrameSecureAttention:
		if err := c.opts.SecureAttention(); err != nil {
			c.notice(err.Error())
		}
		return
	case FrameBanner:
		// Kept, so a helper that starts later or again shows the banner too.
		c.mu.Lock()
		c.lastBanner = append([]byte(nil), frame...)
		c.mu.Unlock()
	case FrameStart:
		c.mu.Lock()
		c.lastStart = append([]byte(nil), frame...)
		c.mu.Unlock()
		var err error
		if started, err = c.ensure(ctx); err != nil {
			c.notice(err.Error())
			return
		}
		if started {
			// The clipboard of the session is watched from the moment the screen is shown, so a copy on the endpoint is offered without
			// the technician asking for it. Nobody signed in is normal (the sign-in screen), so it is logged, not shown.
			safego.Go(c.opts.Logger, "remote control clipboard", func() {
				if _, err := c.ensureClipboard(ctx); err != nil {
					c.opts.Logger.Info("the clipboard of the Windows session is not served", "reason", err)
				}
			})
		}
	case FrameClipboard, FramePlaceFiles:
		// The clipboard belongs to the user signed in on the session, so it is served by their own process, not by the helper.
		helper, err := c.ensureClipboard(ctx)
		if err != nil {
			c.notice(err.Error())
			return
		}
		if err := WriteFrame(helper.In(), frame); err != nil {
			c.opts.Logger.Debug("could not pass a frame to the clipboard of the Windows session", "error", err)
		}
		return
	}
	c.mu.Lock()
	helper, banner := c.helper, c.lastBanner
	c.mu.Unlock()
	if helper == nil {
		return
	}
	if err := WriteFrame(helper.In(), frame); err != nil {
		c.opts.Logger.Debug("could not pass a frame to the remote control helper", "error", err)
	}
	if started && banner != nil {
		_ = WriteFrame(helper.In(), banner)
	}
}

// ensureClipboard starts the process that serves the clipboard of the session as its signed-in user, and returns it.
func (c *Controller) ensureClipboard(ctx context.Context) (Helper, error) {
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		return nil, errors.New("the session is closing")
	}
	if c.clipboard != nil {
		helper := c.clipboard
		c.mu.Unlock()
		return helper, nil
	}
	if c.opts.ClipboardLaunch == nil {
		c.mu.Unlock()
		return nil, errors.New("This endpoint does not serve the clipboard of a remote control session. Update the agent.")
	}
	now := c.opts.Now()
	recent := c.clipTries[:0]
	for _, at := range c.clipTries {
		if now.Sub(at) < restartWindow {
			recent = append(recent, at)
		}
	}
	c.clipTries = append(recent, now)
	if len(c.clipTries) > maxRestarts {
		c.mu.Unlock()
		return nil, errors.New("The clipboard of the endpoint stopped and could not be started again. End the session and open it again.")
	}
	session := c.sessionLocked()
	c.mu.Unlock()
	if session == 0 || session == 0xFFFFFFFF {
		return nil, errors.New("No Windows session is attached to the console right now, so its clipboard cannot be used.")
	}

	helper, err := launch(ctx, c.opts.ClipboardLaunch, session)
	if err != nil {
		c.opts.Logger.Info("could not start the clipboard of the Windows session", "session", session, "error", err)
		return nil, errors.New("The clipboard of Windows session " + itoa(session) + " cannot be used: " + err.Error())
	}
	c.mu.Lock()
	if c.closed {
		c.mu.Unlock()
		_ = helper.Close()
		return nil, errors.New("the session is closing")
	}
	c.clipboard = helper
	c.mu.Unlock()
	c.opts.Logger.Info("the clipboard of the Windows session is served by its signed-in user", "session", session)
	safego.Go(c.opts.Logger, "remote control clipboard output", func() { c.pumpClipboard(helper) })
	return helper, nil
}

// pumpClipboard passes what the clipboard process sends (copied files, copied text, a notice) to the browser until it stops.
func (c *Controller) pumpClipboard(helper Helper) {
	for {
		frame, err := ReadFrame(helper.Out())
		if err != nil {
			break
		}
		if !FromHelper(frame[0]) {
			continue
		}
		if err := c.opts.Send(frame); err != nil {
			break
		}
	}
	c.mu.Lock()
	if c.clipboard == helper {
		c.clipboard = nil
	}
	c.mu.Unlock()
	_ = helper.Close()
}

// closeClipboard ends the clipboard process, so the next use starts one in the session that is shown now.
func (c *Controller) closeClipboard() {
	c.mu.Lock()
	helper := c.clipboard
	c.clipboard = nil
	c.mu.Unlock()
	if helper != nil {
		_ = helper.Close()
	}
}

// sessionLocked is the Windows session to work in: the chosen one, or the console session now.
func (c *Controller) sessionLocked() uint32 {
	if c.opts.Session != 0 {
		return c.opts.Session
	}
	if c.helper != nil {
		return c.current
	}
	return c.opts.ConsoleSession()
}

// Running reports whether a helper shows the screen now.
func (c *Controller) Running() bool {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.helper != nil
}

// CurrentSession is the Windows session the helper shows, or the one it would show: the chosen session, or the console session now.
func (c *Controller) CurrentSession() uint32 {
	c.mu.Lock()
	defer c.mu.Unlock()
	return c.sessionLocked()
}

// ensure starts the helper in the session to show when none runs, and reports whether it started one.
func (c *Controller) ensure(ctx context.Context) (bool, error) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return false, errors.New("the session is closing")
	}
	if c.helper != nil {
		return false, nil
	}
	if !c.watching {
		watchCtx, cancel := context.WithCancel(ctx)
		c.cancel = cancel
		c.watching = true
		if c.opts.Session == 0 {
			safego.Go(c.opts.Logger, "remote control console watch", func() { c.watchConsole(watchCtx) })
		}
	}
	if err := c.startLocked(ctx); err != nil {
		return false, err
	}
	return true, nil
}

func (c *Controller) startLocked(ctx context.Context) error {
	session := c.opts.Session
	if session == 0 {
		session = c.opts.ConsoleSession()
		if session == 0 || session == 0xFFFFFFFF {
			return errors.New("No Windows session is attached to the console right now. Try again in a moment.")
		}
	}
	helper, err := launch(ctx, c.opts.Launch, session)
	if err != nil {
		c.opts.Logger.Warn("could not start the remote control helper", "session", session, "error", err)
		return errors.New("The endpoint could not show Windows session " + itoa(session) + ": " + err.Error())
	}
	c.helper = helper
	c.current = session
	c.opts.Logger.Info("remote control helper started", "session", session)
	safego.Go(c.opts.Logger, "remote control helper output", func() { c.pump(ctx, helper) })
	return nil
}

// pump passes the helper's frames to the browser until the helper stops, then starts it again when that was not asked for.
func (c *Controller) pump(ctx context.Context, helper Helper) {
	// why the helper's frames ended: it stopped, or the session could not take them any more.
	var why error
	for {
		frame, err := ReadFrame(helper.Out())
		if err != nil {
			why = err
			break
		}
		if !FromHelper(frame[0]) {
			continue
		}
		if err := c.opts.Send(frame); err != nil {
			why = err
			break
		}
	}
	c.opts.Logger.Warn("the frames of the remote control helper ended", "reason", why)
	c.mu.Lock()
	if c.helper != helper || c.closed {
		c.mu.Unlock()
		return
	}
	_ = helper.Close()
	stopped := c.current
	c.helper = nil
	c.mu.Unlock()

	// The helper ends with its Windows session: a session that is gone is never worth another try, and a console that moved to another
	// session is a switch, not a helper that keeps failing.
	if c.opts.Session != 0 && c.opts.SessionExists != nil && !c.opts.SessionExists(c.opts.Session) {
		c.opts.Logger.Info("the Windows session of this remote control session ended", "session", c.opts.Session)
		c.notice("Windows session " + itoa(c.opts.Session) + " ended (the user signed out). Open remote control again on the console or another session.")
		return
	}
	select {
	case <-ctx.Done():
		return
	case <-time.After(restartDelay):
	}

	c.mu.Lock()
	if c.closed || c.helper != nil {
		c.mu.Unlock()
		return
	}
	now := c.opts.Now()
	if c.opts.Session == 0 && c.opts.ConsoleSession() != stopped {
		// The console switched; the helper did not fail.
		c.restarts = nil
	}
	recent := c.restarts[:0]
	for _, at := range c.restarts {
		if now.Sub(at) < restartWindow {
			recent = append(recent, at)
		}
	}
	c.restarts = append(recent, now)
	if len(c.restarts) > maxRestarts || ctx.Err() != nil {
		c.mu.Unlock()
		c.opts.Logger.Error("the remote control helper stopped too often; the screen of this session stays black",
			"restarts", maxRestarts, "window", restartWindow)
		c.notice("The screen of the endpoint stopped and could not be started again. End the session and open it again.")
		return
	}
	err := c.startLocked(ctx)
	start, banner, helperNow := c.lastStart, c.lastBanner, c.helper
	c.mu.Unlock()
	if err != nil {
		c.notice(err.Error())
		return
	}
	replay(helperNow, start, banner)
}

// replay gives a helper that started again the last Start and banner, so it shows the same monitor and banner as before.
func replay(helper Helper, frames ...[]byte) {
	if helper == nil {
		return
	}
	for _, frame := range frames {
		if frame != nil {
			_ = WriteFrame(helper.In(), frame)
		}
	}
}

// watchConsole restarts the helper in the new console session when the console switches to another Windows session.
func (c *Controller) watchConsole(ctx context.Context) {
	ticker := time.NewTicker(c.opts.ConsoleCheck)
	defer ticker.Stop()
	for {
		select {
		case <-ctx.Done():
			return
		case <-ticker.C:
		}
		now := c.opts.ConsoleSession()
		c.mu.Lock()
		if c.closed || c.helper == nil || now == c.current || now == 0 || now == 0xFFFFFFFF {
			c.mu.Unlock()
			continue
		}
		old := c.helper
		c.helper = nil
		err := c.startLocked(ctx)
		start, banner, helper := c.lastStart, c.lastBanner, c.helper
		c.mu.Unlock()
		_ = old.Close()
		if err != nil {
			c.notice(err.Error())
			continue
		}
		c.notice("The console switched to Windows session " + itoa(now) + ".")
		replay(helper, start, banner)
		// The clipboard of the session that was left behind is not this session's any more.
		c.closeClipboard()
		c.clipTries = nil
	}
}

func (c *Controller) notice(message string) {
	data, _ := json.Marshal(NoticeBody{Message: message})
	_ = c.opts.Send(append([]byte{FrameNotice}, data...))
}

// Close stops the helper and the clipboard process; the helper releases every key and button it holds as it exits.
func (c *Controller) Close() {
	c.mu.Lock()
	c.closed = true
	helper, clipboard := c.helper, c.clipboard
	c.helper, c.clipboard = nil, nil
	cancel := c.cancel
	c.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	if helper != nil {
		_ = helper.Close()
	}
	if clipboard != nil {
		_ = clipboard.Close()
	}
}

// launch calls the launcher and turns a panic (a Win32 call in the platform launcher) into an error, so a helper that cannot start ends
// as a notice to the technician instead of killing the whole session.
func launch(ctx context.Context, launcher Launcher, session uint32) (h Helper, err error) {
	defer func() {
		if r := recover(); r != nil {
			h, err = nil, fmt.Errorf("the helper stopped unexpectedly: %v", r)
		}
	}()
	return launcher(ctx, session)
}

func itoa(v uint32) string {
	if v == 0 {
		return "0"
	}
	var buf [10]byte
	i := len(buf)
	for v > 0 {
		i--
		buf[i] = byte('0' + v%10)
		v /= 10
	}
	return string(buf[i:])
}
