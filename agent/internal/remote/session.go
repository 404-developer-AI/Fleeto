package remote

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"strings"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
	"github.com/404-developer-AI/Fleeto/agent/internal/screen"
)

// Frame types: the first byte of every decrypted frame. Bodies are JSON, except Data, which carries a 16-bit big-endian channel number
// and raw bytes. The web UI (remote.js) holds the same list.
const (
	// FrameHello (endpoint to browser): what the endpoint offers, sent once as the first frame.
	FrameHello byte = 0x01
	// FrameOpen (browser to endpoint): open a terminal on a channel.
	FrameOpen byte = 0x02
	// FrameOpened (endpoint to browser): the terminal of a channel started, or why it did not.
	FrameOpened byte = 0x03
	// FrameData (both ways): terminal input or output of a channel.
	FrameData byte = 0x04
	// FrameResize (browser to endpoint): a new terminal size.
	FrameResize byte = 0x05
	// FrameCloseChannel (browser to endpoint): end the terminal of a channel.
	FrameCloseChannel byte = 0x06
	// FrameClosed (endpoint to browser): the terminal of a channel ended.
	FrameClosed byte = 0x07
	// FrameIdleWarning (endpoint to browser): the session closes soon without input.
	FrameIdleWarning byte = 0x08
	// FrameEnd (both ways): the session ends, with the reason.
	FrameEnd byte = 0x09
	// FrameActivity (browser to endpoint): the technician is still there (answers the idle warning).
	FrameActivity byte = 0x0A

	// Remote background operations (0.3.0 step 2). Request/response and transfers over the same encrypted session.
	// FrameRequest (browser to endpoint): a file, service or process operation. Body {id, op, ...}.
	FrameRequest byte = 0x0B
	// FrameResponse (endpoint to browser): the result of a request. Body {id, ok, error?, ...result}.
	FrameResponse byte = 0x0C
	// FrameChunk (both ways): a slice of a file transfer. Binary: a 32-bit big-endian transfer id, then the bytes.
	FrameChunk byte = 0x0D
	// FrameTransfer (both ways): flow control and the end of a transfer. Body {transfer, kind, ...}.
	FrameTransfer byte = 0x0E
)

const (
	// MaxTerminals is how many terminals one session keeps open at a time.
	MaxTerminals = 4
	// idleWarningBefore is when the idle warning is sent.
	idleWarningBefore = 2 * time.Minute
	outputChunk       = 32 * 1024
	writeTimeout      = 30 * time.Second
)

// Transport carries encrypted frames: one message per frame. The relay WebSocket in production, a pipe in tests.
type Transport interface {
	Read(ctx context.Context) ([]byte, error)
	Write(ctx context.Context, frame []byte) error
	Close(reason string) error
}

// Terminal is an interactive shell: Read returns its output, Write sends input.
type Terminal interface {
	io.ReadWriter
	Resize(cols, rows int) error
	// Close ends the shell and every process it started.
	Close() error
	// Wait returns the exit code once the shell ended.
	Wait() (int, error)
	// PTY reports whether the shell runs in a pseudo terminal; without one it gets line input only.
	PTY() bool
}

// TerminalOpener starts a shell by name with an initial size.
type TerminalOpener func(shell string, cols, rows int) (Terminal, error)

// Hello is the first frame of a session.
type Hello struct {
	Hostname           string   `json:"hostname"`
	Platform           string   `json:"platform"`
	Shells             []string `json:"shells"`
	PTY                bool     `json:"pty"`
	IdleTimeoutSeconds int      `json:"idleTimeoutSeconds"`
	// MaxFileBytes is the largest file one transfer may carry, from the token (policy). Set by Run.
	MaxFileBytes int64  `json:"maxFileBytes"`
	Version      string `json:"version"`
	// Kind is "remote_control" or "remote_background", from the token. Set by Run.
	Kind string `json:"kind"`
	// WindowsSession is the Windows session a remote control session shows, 0 for the console. Set by Run.
	WindowsSession uint32 `json:"windowsSession,omitempty"`
}

// ScreenHandler serves the screen of a remote control session (internal/screen.Controller).
type ScreenHandler interface {
	// Handle takes one remote control frame from the browser (type byte and body).
	Handle(ctx context.Context, frame []byte)
	Close()
}

// ScreenFactory creates the screen handler of a remote control session; send passes a frame to the browser.
type ScreenFactory func(token *Token, send func(frame []byte) error) ScreenHandler

type openBody struct {
	Channel int    `json:"channel"`
	Service string `json:"service"`
	Shell   string `json:"shell"`
	Cols    int    `json:"cols"`
	Rows    int    `json:"rows"`
}

type channelBody struct {
	Channel  int    `json:"channel"`
	Cols     int    `json:"cols,omitempty"`
	Rows     int    `json:"rows,omitempty"`
	PTY      bool   `json:"pty,omitempty"`
	Shell    string `json:"shell,omitempty"`
	ExitCode *int   `json:"exitCode,omitempty"`
	Error    string `json:"error,omitempty"`
}

type endBody struct {
	Reason string `json:"reason"`
}

// SessionOptions configures one session.
type SessionOptions struct {
	Token     *Token
	Keys      Keys
	Transport Transport
	Hello     Hello
	Open      TerminalOpener
	// Report records an action the technician took (files, services, processes) for the audit log; nil disables reporting.
	Report func(action, target, detail string)
	// Screen serves remote control sessions; nil where remote control is not served.
	Screen ScreenFactory
	Logger *slog.Logger
	Now    func() time.Time
	// Tick is how often the idle timeout is checked; tests shorten it.
	Tick time.Duration
}

// Session is one participant's remote background session on the endpoint.
type Session struct {
	opts   SessionOptions
	recv   *Cipher
	send   *Cipher
	sendMu sync.Mutex

	mu         sync.Mutex
	terminals  map[uint16]Terminal
	lastInput  time.Time
	warned     bool
	background *background
	screen     ScreenHandler
}

// NewSession prepares a session with the derived keys.
func NewSession(opts SessionOptions) (*Session, error) {
	if opts.Now == nil {
		opts.Now = time.Now
	}
	if opts.Tick <= 0 {
		opts.Tick = 10 * time.Second
	}
	if opts.Logger == nil {
		opts.Logger = slog.New(slog.DiscardHandler)
	}
	recv, err := NewCipher(opts.Keys.BrowserToEndpoint)
	if err != nil {
		return nil, err
	}
	send, err := NewCipher(opts.Keys.EndpointToBrowser)
	if err != nil {
		return nil, err
	}
	s := &Session{opts: opts, recv: recv, send: send, terminals: map[uint16]Terminal{}, lastInput: opts.Now()}
	s.background = newBackground(s)
	return s, nil
}

// Run serves the session until the browser ends it, the relay drops, a frame fails authentication or the session is idle too long.
// Every terminal it opened is closed when it returns. The returned reason is for the endpoint's log.
func (s *Session) Run(ctx context.Context) (reason string) {
	ctx, cancel := context.WithCancel(ctx)
	defer cancel()
	defer s.closeTerminals()

	hello := s.opts.Hello
	hello.IdleTimeoutSeconds = int(s.opts.Token.IdleTimeout() / time.Second)
	hello.MaxFileBytes = s.opts.Token.MaxFileBytes()
	hello.Kind = "remote_background"
	if s.remoteControl() {
		hello.Kind = "remote_control"
		hello.WindowsSession = s.opts.Token.GetWindowsSessionId()
		hello.MaxFileBytes = 0
		if s.opts.Screen == nil {
			s.end("This endpoint does not serve remote control. Update the agent.")
			return "remote control is not served here"
		}
		s.screen = s.opts.Screen(s.opts.Token, func(frame []byte) error { return s.sendFrame(ctx, frame) })
		defer s.screen.Close()
	}
	if err := s.sendJSON(ctx, FrameHello, hello); err != nil {
		return "the relay closed before the session started: " + err.Error()
	}
	defer s.background.close()

	frames := make(chan []byte)
	readErr := make(chan error, 1)
	safego.Go(s.opts.Logger, "remote session reader", func() {
		for {
			frame, err := s.opts.Transport.Read(ctx)
			if err != nil {
				readErr <- err
				return
			}
			select {
			case frames <- frame:
			case <-ctx.Done():
				return
			}
		}
	})

	ticker := time.NewTicker(s.opts.Tick)
	defer ticker.Stop()
	idle := s.opts.Token.IdleTimeout()
	for {
		select {
		case <-ctx.Done():
			s.end("The endpoint service is stopping.")
			return "the service is stopping"
		case err := <-readErr:
			return "the relay closed: " + err.Error()
		case <-ticker.C:
			quiet := s.opts.Now().Sub(s.lastInputTime())
			if quiet >= idle {
				s.end(fmt.Sprintf("The session closed after %s without input.", describe(idle)))
				return "idle timeout"
			}
			if quiet >= idle-idleWarningBefore && s.markWarned() {
				left := int((idle - quiet) / time.Second)
				_ = s.sendJSON(ctx, FrameIdleWarning, map[string]int{"secondsLeft": left})
			}
		case frame := <-frames:
			plaintext, err := s.recv.Open(frame)
			if err != nil {
				// Never answer a forged frame: end the session.
				_ = s.opts.Transport.Close("frame authentication failed")
				return "a frame failed authentication; the session was closed"
			}
			if done, why := s.handle(ctx, plaintext); done {
				return why
			}
		}
	}
}

func (s *Session) handle(ctx context.Context, frame []byte) (done bool, reason string) {
	if len(frame) == 0 {
		return false, ""
	}
	body := frame[1:]
	if s.remoteControl() {
		// A remote control session carries the screen only: no terminal, files, services or processes, whatever the browser sends.
		switch {
		case frame[0] == FrameActivity:
			s.touch()
		case frame[0] == FrameEnd:
			_ = s.opts.Transport.Close("ended by the technician")
			return true, "ended by the technician"
		case screen.FromBrowser(frame[0]):
			if frame[0] != screen.FrameAck {
				s.touch()
			}
			s.screen.Handle(ctx, frame)
		}
		return false, ""
	}
	switch frame[0] {
	case FrameOpen:
		s.touch()
		var open openBody
		if json.Unmarshal(body, &open) != nil {
			return false, ""
		}
		s.open(ctx, open)
	case FrameData:
		s.touch()
		if len(body) < 2 {
			return false, ""
		}
		if t := s.terminal(binary.BigEndian.Uint16(body)); t != nil {
			_, _ = t.Write(body[2:])
		}
	case FrameResize:
		s.touch()
		var resize channelBody
		if json.Unmarshal(body, &resize) == nil && validChannel(resize.Channel) {
			if t := s.terminal(uint16(resize.Channel)); t != nil {
				_ = t.Resize(clampSize(resize.Cols, 20, 500), clampSize(resize.Rows, 5, 300))
			}
		}
	case FrameCloseChannel:
		s.touch()
		var closeBody channelBody
		if json.Unmarshal(body, &closeBody) == nil && validChannel(closeBody.Channel) {
			if t := s.removeTerminal(uint16(closeBody.Channel)); t != nil {
				_ = t.Close()
			}
		}
	case FrameActivity:
		s.touch()
	case FrameRequest, FrameChunk, FrameTransfer:
		s.touch()
		s.background.handle(ctx, frame[0], body)
	case FrameEnd:
		var end endBody
		_ = json.Unmarshal(body, &end)
		_ = s.opts.Transport.Close("ended by the technician")
		return true, "ended by the technician"
	default:
		// A newer browser may send frames this endpoint does not know; ignoring them keeps sessions working across versions.
	}
	return false, ""
}

func (s *Session) open(ctx context.Context, open openBody) {
	if !validChannel(open.Channel) {
		return
	}
	channel := uint16(open.Channel)
	reply := channelBody{Channel: open.Channel, Shell: open.Shell}
	if open.Service != "terminal" {
		reply.Error = "This endpoint does not offer that service yet. Update the agent."
		_ = s.sendJSON(ctx, FrameOpened, reply)
		return
	}
	s.mu.Lock()
	_, taken := s.terminals[channel]
	count := len(s.terminals)
	s.mu.Unlock()
	if taken {
		return
	}
	if count >= MaxTerminals {
		reply.Error = fmt.Sprintf("A session keeps at most %d terminals open. Close one first.", MaxTerminals)
		_ = s.sendJSON(ctx, FrameOpened, reply)
		return
	}
	terminal, err := s.opts.Open(open.Shell, clampSize(open.Cols, 20, 500), clampSize(open.Rows, 5, 300))
	if err != nil {
		reply.Error = err.Error()
		_ = s.sendJSON(ctx, FrameOpened, reply)
		return
	}
	s.mu.Lock()
	s.terminals[channel] = terminal
	s.mu.Unlock()
	reply.PTY = terminal.PTY()
	s.opts.Logger.Info("remote terminal opened", "participant", s.opts.Token.GetParticipantId(), "technician", s.opts.Token.GetTechnicianName(),
		"shell", open.Shell)
	if err := s.sendJSON(ctx, FrameOpened, reply); err != nil {
		return
	}
	safego.Go(s.opts.Logger, "remote terminal output", func() { s.pump(ctx, channel, terminal) })
}

// pump sends the output of a terminal until it ends, then reports its exit.
func (s *Session) pump(ctx context.Context, channel uint16, terminal Terminal) {
	buffer := make([]byte, outputChunk+3)
	buffer[0] = FrameData
	binary.BigEndian.PutUint16(buffer[1:3], channel)
	for {
		n, err := terminal.Read(buffer[3:])
		if n > 0 {
			if werr := s.sendFrame(ctx, buffer[:3+n]); werr != nil {
				_ = terminal.Close()
				return
			}
		}
		if err != nil {
			break
		}
	}
	code, waitErr := terminal.Wait()
	removed := s.removeTerminal(channel) != nil
	_ = terminal.Close()
	reply := channelBody{Channel: int(channel)}
	if waitErr == nil {
		reply.ExitCode = &code
	} else if removed {
		reply.Error = waitErr.Error()
	}
	_ = s.sendJSON(ctx, FrameClosed, reply)
}

func (s *Session) remoteControl() bool {
	return s.opts.Token.GetKind() == agentv1.RemoteSessionKind_REMOTE_SESSION_KIND_REMOTE_CONTROL
}

func (s *Session) end(reason string) {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	_ = s.sendJSON(ctx, FrameEnd, endBody{Reason: reason})
	_ = s.opts.Transport.Close(reason)
}

func (s *Session) sendJSON(ctx context.Context, kind byte, body any) error {
	data, err := json.Marshal(body)
	if err != nil {
		return err
	}
	return s.sendFrame(ctx, append([]byte{kind}, data...))
}

// sendChunkFrame sends a binary transfer chunk: the FrameChunk type byte, then the payload (a 32-bit transfer id and the bytes).
func (s *Session) sendChunkFrame(ctx context.Context, payload []byte) error {
	frame := make([]byte, 1+len(payload))
	frame[0] = FrameChunk
	copy(frame[1:], payload)
	return s.sendFrame(ctx, frame)
}

func (s *Session) sendFrame(ctx context.Context, plaintext []byte) error {
	s.sendMu.Lock()
	defer s.sendMu.Unlock()
	frame, err := s.send.Seal(plaintext)
	if err != nil {
		return err
	}
	writeCtx, cancel := context.WithTimeout(ctx, writeTimeout)
	defer cancel()
	return s.opts.Transport.Write(writeCtx, frame)
}

func (s *Session) terminal(channel uint16) Terminal {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.terminals[channel]
}

func (s *Session) removeTerminal(channel uint16) Terminal {
	s.mu.Lock()
	defer s.mu.Unlock()
	t := s.terminals[channel]
	delete(s.terminals, channel)
	return t
}

func (s *Session) closeTerminals() {
	s.mu.Lock()
	terminals := s.terminals
	s.terminals = map[uint16]Terminal{}
	s.mu.Unlock()
	for _, t := range terminals {
		_ = t.Close()
	}
}

func (s *Session) touch() {
	s.mu.Lock()
	s.lastInput = s.opts.Now()
	s.warned = false
	s.mu.Unlock()
}

func (s *Session) lastInputTime() time.Time {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.lastInput
}

func (s *Session) markWarned() bool {
	s.mu.Lock()
	defer s.mu.Unlock()
	if s.warned {
		return false
	}
	s.warned = true
	return true
}

func validChannel(channel int) bool { return channel >= 1 && channel <= 0xFFFF }

func clampSize(value, low, high int) int { return min(max(value, low), high) }

func describe(d time.Duration) string {
	minutes := int(d / time.Minute)
	if minutes == 1 {
		return "1 minute"
	}
	return fmt.Sprintf("%d minutes", minutes)
}

// ErrUnknownShell is returned for a shell this endpoint does not offer.
var ErrUnknownShell = errors.New("this endpoint does not offer that shell")

// ShellLabel is the display name of a shell for messages.
func ShellLabel(shell string) string {
	switch strings.ToLower(shell) {
	case "powershell":
		return "PowerShell"
	case "cmd":
		return "Command Prompt"
	default:
		return shell
	}
}
