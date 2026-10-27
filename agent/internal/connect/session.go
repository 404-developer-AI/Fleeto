package connect

import (
	"context"
	"crypto/ecdh"
	"crypto/sha256"
	"encoding/base64"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"sync"
	"time"

	"github.com/coder/websocket"

	"github.com/404-developer-AI/Fleeto/agent/internal/remote"
)

const (
	handshakeWait = 45 * time.Second
	openWait      = 20 * time.Second
	writeWait     = 30 * time.Second
	// channelBuffer is how many data frames wait for one local connection before the session waits for it.
	channelBuffer = 64
)

// Transport is the relay connection: one message per frame. A WebSocket in production, a pipe in tests.
type Transport interface {
	ReadText(ctx context.Context) (text []byte, binary []byte, err error)
	WriteText(ctx context.Context, text []byte) error
	Write(ctx context.Context, frame []byte) error
	Close(reason string) error
}

// DialRelay opens the relay WebSocket of a ticket with the instance's web URL as Origin, as the gateway requires.
func DialRelay(ctx context.Context, client *http.Client, link Link, ticket *Ticket) (Transport, error) {
	scheme := "https://"
	if link.isLocal() && len(ticket.RelayURL) > 3 && ticket.RelayURL[:3] == "ws:" {
		scheme = "http://"
	}
	conn, response, err := websocket.Dial(ctx, ticket.RelayURL, &websocket.DialOptions{
		HTTPClient: client, HTTPHeader: http.Header{"Origin": {scheme + link.Instance}},
	})
	if err != nil {
		if response != nil {
			return nil, fmt.Errorf("the relay refused the session (HTTP %d)", response.StatusCode)
		}
		return nil, fmt.Errorf("Fleeto Connect could not reach the relay: %w", err)
	}
	conn.SetReadLimit(remote.MaxFrameBytes + 1024)
	return &wsTransport{conn: conn}, nil
}

type wsTransport struct{ conn *websocket.Conn }

func (t *wsTransport) ReadText(ctx context.Context) ([]byte, []byte, error) {
	typ, data, err := t.conn.Read(ctx)
	if err != nil {
		return nil, nil, err
	}
	if typ == websocket.MessageText {
		return data, nil, nil
	}
	return nil, data, nil
}

func (t *wsTransport) WriteText(ctx context.Context, text []byte) error {
	return t.conn.Write(ctx, websocket.MessageText, text)
}

func (t *wsTransport) Write(ctx context.Context, frame []byte) error {
	return t.conn.Write(ctx, websocket.MessageBinary, frame)
}

func (t *wsTransport) Close(reason string) error {
	if len(reason) > 120 {
		reason = reason[:120]
	}
	return t.conn.Close(websocket.StatusNormalClosure, reason)
}

// Hello is what the endpoint offers, the first frame of the session.
type Hello struct {
	Hostname           string `json:"hostname"`
	Kind               string `json:"kind"`
	Version            string `json:"version"`
	IdleTimeoutSeconds int    `json:"idleTimeoutSeconds"`
	TunnelCertificate  string `json:"tunnelCertificate"`
}

// Session is the Fleeto Connect side of a Proxmox web interface session.
type Session struct {
	transport Transport
	send      *remote.Cipher
	recv      *remote.Cipher
	sendMu    sync.Mutex
	Hello     Hello

	mu       sync.Mutex
	next     uint16
	channels map[uint16]*channel
	ended    chan struct{}
	endOnce  sync.Once
	reason   string
}

// Open performs the handshake on a relay connection: the token as the first message, the endpoint's key accepted only by a fingerprint
// web gave, then the Hello, which must be of a Proxmox web interface session with a certificate to pin.
func Open(ctx context.Context, transport Transport, ticket *Ticket, private *ecdh.PrivateKey) (*Session, error) {
	payload, err := base64.StdEncoding.DecodeString(ticket.Token)
	if err != nil {
		return nil, errors.New("the session token cannot be read")
	}
	hello, _ := json.Marshal(map[string]string{"token": ticket.Token, "signature": ticket.Signature, "keyId": ticket.KeyID})
	ctx, cancel := context.WithTimeout(ctx, handshakeWait)
	defer cancel()
	if err := transport.WriteText(ctx, hello); err != nil {
		return nil, fmt.Errorf("the relay closed the connection: %w", err)
	}
	var keys remote.Keys
	for {
		text, _, err := transport.ReadText(ctx)
		if err != nil {
			return nil, fmt.Errorf("the relay closed the connection before the session started: %w", err)
		}
		if text == nil {
			return nil, errors.New("the relay sent data before the session was set up")
		}
		var message struct {
			Type                 string `json:"type"`
			Message              string `json:"message"`
			EndpointPublicKey    []byte `json:"endpointPublicKey"`
			Signature            []byte `json:"signature"`
			CertificatePublicKey []byte `json:"certificatePublicKey"`
		}
		if json.Unmarshal(text, &message) != nil {
			continue
		}
		if message.Type == "error" {
			return nil, errors.New(message.Message)
		}
		if message.Type != "ready" {
			continue
		}
		keys, err = remote.BrowserHandshake(private, sha256.Sum256(payload), message.EndpointPublicKey, message.Signature,
			message.CertificatePublicKey, ticket.Fingerprints)
		if err != nil {
			return nil, err
		}
		break
	}
	send, err := remote.NewCipher(keys.BrowserToEndpoint)
	if err != nil {
		return nil, err
	}
	recv, err := remote.NewCipher(keys.EndpointToBrowser)
	if err != nil {
		return nil, err
	}
	s := &Session{transport: transport, send: send, recv: recv, channels: map[uint16]*channel{}, ended: make(chan struct{})}
	frame, err := s.read(ctx)
	if err != nil {
		return nil, err
	}
	switch {
	case len(frame) > 0 && frame[0] == remote.FrameEnd:
		return nil, errors.New(endReason(frame[1:]))
	case len(frame) == 0 || frame[0] != remote.FrameHello || json.Unmarshal(frame[1:], &s.Hello) != nil:
		return nil, errors.New("the endpoint did not start the session")
	case s.Hello.Kind != "proxmox_web" || len(s.Hello.TunnelCertificate) != 64:
		return nil, errors.New("the endpoint did not offer the Proxmox web interface; update the agent")
	}
	return s, nil
}

func (s *Session) read(ctx context.Context) ([]byte, error) {
	for {
		text, data, err := s.transport.ReadText(ctx)
		if err != nil {
			return nil, err
		}
		if text != nil {
			continue
		}
		plaintext, err := s.recv.Open(data)
		if err != nil {
			// Never answer a forged frame: end the session.
			_ = s.transport.Close("frame authentication failed")
			return nil, errors.New("a frame of the session failed authentication; the session was closed")
		}
		return plaintext, nil
	}
}

// Run reads the frames of the endpoint until the session ends, and returns why it ended.
func (s *Session) Run(ctx context.Context) string {
	defer s.closeChannels()
	for {
		frame, err := s.read(ctx)
		if err != nil {
			s.finish("The connection to the endpoint was lost.")
			return s.reason
		}
		if len(frame) == 0 {
			continue
		}
		body := frame[1:]
		switch frame[0] {
		case remote.FrameTunnelOpened:
			var opened struct {
				Channel int    `json:"channel"`
				Error   string `json:"error"`
			}
			if json.Unmarshal(body, &opened) == nil {
				if c := s.channel(uint16(opened.Channel)); c != nil {
					c.opened(opened.Error)
				}
			}
		case remote.FrameTunnelData:
			if len(body) >= 2 {
				if c := s.channel(binary.BigEndian.Uint16(body)); c != nil {
					c.deliver(ctx, append([]byte(nil), body[2:]...))
				}
			}
		case remote.FrameTunnelClose:
			var closed struct {
				Channel int `json:"channel"`
			}
			if json.Unmarshal(body, &closed) == nil {
				if c := s.remove(uint16(closed.Channel)); c != nil {
					c.remoteClosed()
				}
			}
		case remote.FrameIdleWarning:
			// Only a request of the browser counts as activity; the Proxmox page polls while it is open.
		case remote.FrameEnd:
			s.finish(endReason(body))
			_ = s.transport.Close("ended by the endpoint")
			return s.reason
		}
	}
}

// End ends the session from this side.
func (s *Session) End(reason string) {
	ctx, cancel := context.WithTimeout(context.Background(), 5*time.Second)
	defer cancel()
	body, _ := json.Marshal(map[string]string{"reason": reason})
	_ = s.sendFrame(ctx, append([]byte{remote.FrameEnd}, body...))
	_ = s.transport.Close(reason)
	s.finish(reason)
}

// Ended is closed once the session ended.
func (s *Session) Ended() <-chan struct{} { return s.ended }

func (s *Session) finish(reason string) {
	s.endOnce.Do(func() {
		s.mu.Lock()
		s.reason = reason
		s.mu.Unlock()
		close(s.ended)
	})
}

func (s *Session) sendFrame(ctx context.Context, plaintext []byte) error {
	s.sendMu.Lock()
	defer s.sendMu.Unlock()
	frame, err := s.send.Seal(plaintext)
	if err != nil {
		return err
	}
	ctx, cancel := context.WithTimeout(ctx, writeWait)
	defer cancel()
	return s.transport.Write(ctx, frame)
}

func (s *Session) sendJSON(ctx context.Context, kind byte, body any) error {
	data, _ := json.Marshal(body)
	return s.sendFrame(ctx, append([]byte{kind}, data...))
}

// Dial opens a tunnel channel to pveproxy on the endpoint. The returned connection carries raw bytes; the caller speaks TLS over it.
func (s *Session) Dial(ctx context.Context) (net.Conn, error) {
	select {
	case <-s.ended:
		return nil, errors.New("the session ended")
	default:
	}
	s.mu.Lock()
	if len(s.channels) >= remote.MaxTunnelChannels {
		s.mu.Unlock()
		return nil, fmt.Errorf("the session keeps at most %d connections open", remote.MaxTunnelChannels)
	}
	var id uint16
	for {
		s.next++
		if s.next == 0 {
			s.next = 1
		}
		if _, taken := s.channels[s.next]; !taken {
			id = s.next
			break
		}
	}
	c := newChannel(s, id)
	s.channels[id] = c
	s.mu.Unlock()
	if err := s.sendJSON(ctx, remote.FrameTunnelOpen, map[string]int{"channel": int(id)}); err != nil {
		s.remove(id)
		return nil, err
	}
	ctx, cancel := context.WithTimeout(ctx, openWait)
	defer cancel()
	select {
	case refusal := <-c.openResult:
		if refusal != "" {
			s.remove(id)
			return nil, errors.New(refusal)
		}
		return c, nil
	case <-s.ended:
		return nil, errors.New("the session ended")
	case <-ctx.Done():
		s.remove(id)
		_ = s.sendJSON(context.Background(), remote.FrameTunnelClose, map[string]int{"channel": int(id)})
		return nil, errors.New("the endpoint did not connect to the Proxmox web interface in time")
	}
}

func (s *Session) channel(id uint16) *channel {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.channels[id]
}

func (s *Session) remove(id uint16) *channel {
	s.mu.Lock()
	defer s.mu.Unlock()
	c := s.channels[id]
	delete(s.channels, id)
	return c
}

func (s *Session) closeChannels() {
	s.mu.Lock()
	channels := s.channels
	s.channels = map[uint16]*channel{}
	s.mu.Unlock()
	for _, c := range channels {
		c.remoteClosed()
	}
}

func endReason(body []byte) string {
	var end struct {
		Reason string `json:"reason"`
	}
	if json.Unmarshal(body, &end) == nil && end.Reason != "" {
		return end.Reason
	}
	return "The endpoint ended the session."
}

// channel is one tunnel connection as a net.Conn.
type channel struct {
	s          *Session
	id         uint16
	openResult chan string
	incoming   chan []byte
	pending    []byte
	done       chan struct{}
	doneOnce   sync.Once
	readMu     sync.Mutex
	// deadline handling is minimal: net/http sets none on a dialed connection it does not own a deadline for.
}

func newChannel(s *Session, id uint16) *channel {
	return &channel{s: s, id: id, openResult: make(chan string, 1), incoming: make(chan []byte, channelBuffer), done: make(chan struct{})}
}

func (c *channel) opened(refusal string) {
	select {
	case c.openResult <- refusal:
	default:
	}
}

func (c *channel) deliver(ctx context.Context, data []byte) {
	select {
	case c.incoming <- data:
	case <-c.done:
	case <-ctx.Done():
	}
}

func (c *channel) remoteClosed() { c.doneOnce.Do(func() { close(c.done) }) }

func (c *channel) Read(p []byte) (int, error) {
	c.readMu.Lock()
	defer c.readMu.Unlock()
	if len(c.pending) == 0 {
		select {
		case data := <-c.incoming:
			c.pending = data
		case <-c.done:
			// Deliver what arrived before the close.
			select {
			case data := <-c.incoming:
				c.pending = data
			default:
				return 0, io.EOF
			}
		}
	}
	n := copy(p, c.pending)
	c.pending = c.pending[n:]
	return n, nil
}

func (c *channel) Write(p []byte) (int, error) {
	written := 0
	for len(p) > 0 {
		select {
		case <-c.done:
			return written, net.ErrClosed
		default:
		}
		n := min(len(p), 32*1024)
		frame := make([]byte, 3+n)
		frame[0] = remote.FrameTunnelData
		binary.BigEndian.PutUint16(frame[1:3], c.id)
		copy(frame[3:], p[:n])
		if err := c.s.sendFrame(context.Background(), frame); err != nil {
			return written, err
		}
		written += n
		p = p[n:]
	}
	return written, nil
}

func (c *channel) Close() error {
	if c.s.remove(c.id) == c {
		_ = c.s.sendJSON(context.Background(), remote.FrameTunnelClose, map[string]int{"channel": int(c.id)})
	}
	c.remoteClosed()
	return nil
}

func (c *channel) LocalAddr() net.Addr              { return tunnelAddr{} }
func (c *channel) RemoteAddr() net.Addr             { return tunnelAddr{} }
func (c *channel) SetDeadline(time.Time) error      { return nil }
func (c *channel) SetReadDeadline(time.Time) error  { return nil }
func (c *channel) SetWriteDeadline(time.Time) error { return nil }

type tunnelAddr struct{}

func (tunnelAddr) Network() string { return "fleeto-tunnel" }
func (tunnelAddr) String() string  { return "pveproxy" }
