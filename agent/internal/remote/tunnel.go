package remote

import (
	"context"
	"encoding/binary"
	"encoding/json"
	"errors"
	"fmt"
	"net"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/safego"
)

// The Proxmox web interface (0.8.0, PROXMOX-WEB.md): Fleeto Connect on the technician's PC carries every connection of the browser as a
// tunnel channel, and the endpoint connects each channel to the one fixed target its Tunnel names (127.0.0.1:8006 on a Proxmox VE host).
// Nothing in a frame names a host or port. The bytes are the TLS of pveproxy, end to end between Fleeto Connect and the host, inside the
// encryption of the session.
const (
	// FrameTunnelOpen (Fleeto Connect to endpoint): connect a channel to the target. Body {channel}.
	FrameTunnelOpen byte = 0x20
	// FrameTunnelOpened (endpoint to Fleeto Connect): the channel is connected, or why not. Body {channel, error?}.
	FrameTunnelOpened byte = 0x21
	// FrameTunnelData (both ways): bytes of a channel. Binary: a 16-bit big-endian channel number, then the bytes.
	FrameTunnelData byte = 0x22
	// FrameTunnelClose (both ways): the channel ended. Body {channel}.
	FrameTunnelClose byte = 0x23
)

const (
	// MaxTunnelChannels is how many connections one session keeps open at a time; a browser opens about six per host, plus consoles.
	MaxTunnelChannels = 64
	// tunnelChunk is the most bytes one data frame carries.
	tunnelChunk = 32 * 1024
	// tunnelQueue is how many data frames wait for one connection before the session waits for it.
	tunnelQueue      = 64
	tunnelDialWait   = 10 * time.Second
	tunnelWriteLimit = time.Minute
)

// Tunnel is the fixed target of the Proxmox web interface on this endpoint.
type Tunnel struct {
	// Dial connects one channel to the target.
	Dial func(ctx context.Context) (net.Conn, error)
	// Certificate is the lowercase hex SHA-256 of the certificate the target serves; Fleeto Connect accepts the TLS of the target only
	// with it.
	Certificate string
}

// TunnelFactory returns the tunnel of the endpoint, or an error that says why it serves none (not a Proxmox VE host, pveproxy not
// listening). Called for every offer of the Proxmox web interface.
type TunnelFactory func(ctx context.Context) (*Tunnel, error)

type tunnelBody struct {
	Channel int    `json:"channel"`
	Error   string `json:"error,omitempty"`
}

type tunnelChannel struct {
	conn      net.Conn
	queue     chan []byte
	done      chan struct{}
	closeOnce sync.Once
}

func (c *tunnelChannel) close() {
	c.closeOnce.Do(func() {
		close(c.done)
		_ = c.conn.Close()
	})
}

// tunnels holds the channels of one session.
type tunnels struct {
	s        *Session
	mu       sync.Mutex
	channels map[uint16]*tunnelChannel
	// pending counts channels being connected, so a burst of opens cannot pass the limit together.
	pending int
}

func newTunnels(s *Session) *tunnels { return &tunnels{s: s, channels: map[uint16]*tunnelChannel{}} }

// handle takes one frame of a Proxmox web interface session.
func (t *tunnels) handle(ctx context.Context, kind byte, body []byte) {
	switch kind {
	case FrameTunnelOpen:
		var open tunnelBody
		if json.Unmarshal(body, &open) == nil && validChannel(open.Channel) {
			t.open(ctx, uint16(open.Channel))
		}
	case FrameTunnelData:
		if len(body) < 2 {
			return
		}
		channel := t.channel(binary.BigEndian.Uint16(body))
		if channel == nil {
			return
		}
		data := append([]byte(nil), body[2:]...)
		// The session waits while the connection is behind: backpressure instead of memory without a bound.
		select {
		case channel.queue <- data:
		case <-channel.done:
		case <-ctx.Done():
		}
	case FrameTunnelClose:
		var closeBody tunnelBody
		if json.Unmarshal(body, &closeBody) == nil && validChannel(closeBody.Channel) {
			if channel := t.remove(uint16(closeBody.Channel)); channel != nil {
				channel.close()
			}
		}
	}
}

func (t *tunnels) open(ctx context.Context, channel uint16) {
	reply := tunnelBody{Channel: int(channel)}
	t.mu.Lock()
	_, taken := t.channels[channel]
	full := len(t.channels)+t.pending >= MaxTunnelChannels
	if !taken && !full {
		t.pending++
	}
	t.mu.Unlock()
	if taken {
		return
	}
	if full {
		reply.Error = fmt.Sprintf("The session keeps at most %d connections open at a time.", MaxTunnelChannels)
		_ = t.s.sendJSON(ctx, FrameTunnelOpened, reply)
		return
	}
	// Connecting can take a moment; the session goes on meanwhile.
	safego.Go(t.s.opts.Logger, "tunnel connect", func() {
		dialCtx, cancel := context.WithTimeout(ctx, tunnelDialWait)
		conn, err := t.s.opts.Tunnel.Dial(dialCtx)
		cancel()
		t.mu.Lock()
		t.pending--
		var c *tunnelChannel
		if err == nil {
			c = &tunnelChannel{conn: conn, queue: make(chan []byte, tunnelQueue), done: make(chan struct{})}
			t.channels[channel] = c
		}
		t.mu.Unlock()
		if err != nil {
			reply.Error = "The endpoint could not connect to the Proxmox web interface: " + err.Error()
			_ = t.s.sendJSON(ctx, FrameTunnelOpened, reply)
			return
		}
		if t.s.sendJSON(ctx, FrameTunnelOpened, reply) != nil {
			t.remove(channel)
			c.close()
			return
		}
		safego.Go(t.s.opts.Logger, "tunnel writer", func() { t.write(c) })
		safego.Go(t.s.opts.Logger, "tunnel reader", func() { t.read(ctx, channel, c) })
	})
}

// write passes the bytes from Fleeto Connect to the connection.
func (t *tunnels) write(c *tunnelChannel) {
	for {
		select {
		case <-c.done:
			return
		case data := <-c.queue:
			_ = c.conn.SetWriteDeadline(time.Now().Add(tunnelWriteLimit))
			if _, err := c.conn.Write(data); err != nil {
				c.close()
				return
			}
		}
	}
}

// read passes the bytes of the connection to Fleeto Connect until it ends, then closes the channel on both sides.
func (t *tunnels) read(ctx context.Context, channel uint16, c *tunnelChannel) {
	buffer := make([]byte, tunnelChunk+3)
	buffer[0] = FrameTunnelData
	binary.BigEndian.PutUint16(buffer[1:3], channel)
	for {
		n, err := c.conn.Read(buffer[3:])
		if n > 0 {
			if werr := t.s.sendFrame(ctx, buffer[:3+n]); werr != nil {
				break
			}
		}
		if err != nil {
			break
		}
	}
	// Removed first, so only one side reports the close.
	if t.remove(channel) == c {
		_ = t.s.sendJSON(ctx, FrameTunnelClose, tunnelBody{Channel: int(channel)})
	}
	c.close()
}

func (t *tunnels) channel(channel uint16) *tunnelChannel {
	t.mu.Lock()
	defer t.mu.Unlock()
	return t.channels[channel]
}

func (t *tunnels) remove(channel uint16) *tunnelChannel {
	t.mu.Lock()
	defer t.mu.Unlock()
	c := t.channels[channel]
	delete(t.channels, channel)
	return c
}

func (t *tunnels) closeAll() {
	t.mu.Lock()
	channels := t.channels
	t.channels = map[uint16]*tunnelChannel{}
	t.mu.Unlock()
	for _, c := range channels {
		c.close()
	}
}

// ErrNoTunnel is returned by a TunnelFactory on an endpoint that is not a Proxmox VE host.
var ErrNoTunnel = errors.New("this endpoint is not a Proxmox VE host")
