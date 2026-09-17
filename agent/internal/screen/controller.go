package screen

import (
	"context"
	"encoding/json"
	"errors"
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
)

// ControllerOptions configure a Controller.
type ControllerOptions struct {
	// Send passes a frame to the browser over the encrypted session.
	Send func(frame []byte) error
	// Launch starts a helper; Session is the Windows session of the token, 0 for the console.
	Launch  Launcher
	Session uint32
	// ConsoleSession returns the Windows session attached to the console now.
	ConsoleSession func() uint32
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

	mu        sync.Mutex
	helper    Helper
	current   uint32
	lastStart []byte
	restarts  []time.Time
	closed    bool
	cancel    context.CancelFunc
	watching  bool
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

// Handle takes one frame from the browser (type byte and body).
func (c *Controller) Handle(ctx context.Context, frame []byte) {
	if len(frame) == 0 || !FromBrowser(frame[0]) {
		return
	}
	switch frame[0] {
	case FrameSecureAttention:
		if err := c.opts.SecureAttention(); err != nil {
			c.notice(err.Error())
		}
		return
	case FrameStart:
		c.mu.Lock()
		c.lastStart = append([]byte(nil), frame...)
		c.mu.Unlock()
		if err := c.ensure(ctx); err != nil {
			c.notice(err.Error())
			return
		}
	}
	c.mu.Lock()
	helper := c.helper
	c.mu.Unlock()
	if helper == nil {
		return
	}
	if err := WriteFrame(helper.In(), frame); err != nil {
		c.opts.Logger.Debug("could not pass a frame to the remote control helper", "error", err)
	}
}

// ensure starts the helper in the session to show when none runs.
func (c *Controller) ensure(ctx context.Context) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.closed {
		return errors.New("the session is closing")
	}
	if c.helper != nil {
		return nil
	}
	if !c.watching {
		watchCtx, cancel := context.WithCancel(ctx)
		c.cancel = cancel
		c.watching = true
		if c.opts.Session == 0 {
			safego.Go(c.opts.Logger, "remote control console watch", func() { c.watchConsole(watchCtx) })
		}
	}
	return c.startLocked(ctx)
}

func (c *Controller) startLocked(ctx context.Context) error {
	session := c.opts.Session
	if session == 0 {
		session = c.opts.ConsoleSession()
		if session == 0 || session == 0xFFFFFFFF {
			return errors.New("No Windows session is attached to the console right now. Try again in a moment.")
		}
	}
	helper, err := c.opts.Launch(ctx, session)
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
	if c.helper != helper || c.closed {
		c.mu.Unlock()
		return
	}
	_ = helper.Close()
	c.helper = nil
	now := c.opts.Now()
	recent := c.restarts[:0]
	for _, at := range c.restarts {
		if now.Sub(at) < restartWindow {
			recent = append(recent, at)
		}
	}
	c.restarts = append(recent, now)
	if len(c.restarts) > maxRestarts || ctx.Err() != nil {
		c.mu.Unlock()
		c.notice("The screen of the endpoint stopped and could not be started again. End the session and open it again.")
		return
	}
	err := c.startLocked(ctx)
	start, helperNow := c.lastStart, c.helper
	c.mu.Unlock()
	if err != nil {
		c.notice(err.Error())
		return
	}
	if start != nil && helperNow != nil {
		_ = WriteFrame(helperNow.In(), start)
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
		start, helper := c.lastStart, c.helper
		c.mu.Unlock()
		_ = old.Close()
		if err != nil {
			c.notice(err.Error())
			continue
		}
		c.notice("The console switched to Windows session " + itoa(now) + ".")
		if start != nil && helper != nil {
			_ = WriteFrame(helper.In(), start)
		}
	}
}

func (c *Controller) notice(message string) {
	data, _ := json.Marshal(NoticeBody{Message: message})
	_ = c.opts.Send(append([]byte{FrameNotice}, data...))
}

// Close stops the helper; the helper releases every key and button it holds as it exits.
func (c *Controller) Close() {
	c.mu.Lock()
	c.closed = true
	helper := c.helper
	c.helper = nil
	cancel := c.cancel
	c.mu.Unlock()
	if cancel != nil {
		cancel()
	}
	if helper != nil {
		_ = helper.Close()
	}
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
