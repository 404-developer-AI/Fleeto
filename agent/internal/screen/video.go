package screen

import (
	"encoding/binary"
	"errors"
	"runtime"
	"sync"
	"time"
)

// H.264 video (0.3.0 step 5, ARCHITECTURE.md §4 Remote control). Where the endpoint has an H.264 encoder (Media Foundation on Windows) and
// every technician's browser decodes H.264 (WebCodecs), the helper sends the screen as H.264 instead of tiles: much less data for moving
// content (video, scrolling, dragging a window) at the same flow control, one frame at a time, acknowledged after the browser drew it. The
// tiles stay the fallback: a browser without WebCodecs, an endpoint without an encoder, an area the encoder refuses, an encoder that fails
// while it runs, or a browser whose decoder fails.

const (
	// CodecH264 and CodecTiles are the names of the codecs in FrameStart and FrameInfo.
	CodecH264  = "h264"
	CodecTiles = "tiles"

	// videoFrameRate is the frame rate the encoder plans its bit rate with: the helper's highest (one capture every 40 ms).
	videoFrameRate = 25

	// VideoFlagKey marks a video frame that starts with an IDR picture (with its SPS and PPS): a decoder can start there.
	VideoFlagKey byte = 2

	// videoHeaderBytes is the header of every FrameVideo:
	//
	//	type byte (0x1F) | frame uint32 | flags uint8 (1: last part of the frame, 2: key frame) | width uint16 | height uint16 |
	//	endpoint uint32 (microseconds of capture, conversion and encoding) | waited uint32 (microseconds between the acknowledgement of the
	//	previous frame and the capture of this one) | data (a part of one H.264 access unit, Annex B)
	//
	// The frame number and flags sit where they sit in a FrameUpdate, so the hub reads both the same way.
	videoHeaderBytes = 1 + 4 + 1 + 2 + 2 + 4 + 4
)

// VideoFrame is one encoded frame with the timings the browser uses to estimate the latency.
type VideoFrame struct {
	Number   uint32
	Key      bool
	Width    int
	Height   int
	Endpoint time.Duration
	Waited   time.Duration
	Data     []byte
}

// VideoUpdates cuts one encoded frame into FrameVideo frames of at most MaxFrameBytes each; the last one carries FlagLast.
func VideoUpdates(v VideoFrame) ([][]byte, error) {
	if len(v.Data) == 0 {
		return nil, errors.New("an encoded frame is empty")
	}
	if v.Width <= 0 || v.Height <= 0 || v.Width > 0xFFFF || v.Height > 0xFFFF {
		return nil, errors.New("the size of an encoded frame is out of range")
	}
	flags := byte(0)
	if v.Key {
		flags |= VideoFlagKey
	}
	room := MaxFrameBytes - videoHeaderBytes
	var out [][]byte
	for rest := v.Data; len(rest) > 0; {
		n := min(len(rest), room)
		part := make([]byte, videoHeaderBytes+n)
		part[0] = FrameVideo
		binary.BigEndian.PutUint32(part[1:], v.Number)
		part[5] = flags
		if n == len(rest) {
			part[5] |= FlagLast
		}
		binary.BigEndian.PutUint16(part[6:], uint16(v.Width))
		binary.BigEndian.PutUint16(part[8:], uint16(v.Height))
		binary.BigEndian.PutUint32(part[10:], micros(v.Endpoint))
		binary.BigEndian.PutUint32(part[14:], micros(v.Waited))
		copy(part[videoHeaderBytes:], rest[:n])
		out = append(out, part)
		rest = rest[n:]
	}
	return out, nil
}

// ParseVideo reads a FrameVideo (for tests and tools): the header, the data and whether it is the last part.
func ParseVideo(frame []byte) (v VideoFrame, last bool, err error) {
	if len(frame) < videoHeaderBytes || frame[0] != FrameVideo {
		return VideoFrame{}, false, errors.New("not a video frame")
	}
	v = VideoFrame{
		Number: binary.BigEndian.Uint32(frame[1:]), Key: frame[5]&VideoFlagKey != 0,
		Width: int(binary.BigEndian.Uint16(frame[6:])), Height: int(binary.BigEndian.Uint16(frame[8:])),
		Endpoint: time.Duration(binary.BigEndian.Uint32(frame[10:])) * time.Microsecond,
		Waited:   time.Duration(binary.BigEndian.Uint32(frame[14:])) * time.Microsecond,
		Data:     frame[videoHeaderBytes:],
	}
	return v, frame[5]&FlagLast != 0, nil
}

func micros(d time.Duration) uint32 {
	if d <= 0 {
		return 0
	}
	return uint32(min(d.Microseconds(), 0xFFFFFFFF))
}

// ChooseCodec picks the codec for a session: H.264 when the technicians all decode it and the endpoint can encode it, else tiles.
func ChooseCodec(requested []string, h264Available bool) string {
	if h264Available && hasCodec(requested, CodecH264) {
		return CodecH264
	}
	return CodecTiles
}

func hasCodec(list []string, codec string) bool {
	for _, c := range list {
		if c == codec {
			return true
		}
	}
	return false
}

// commonCodecs is the codecs every list holds, in the order of the first list.
func commonCodecs(lists [][]string) []string {
	if len(lists) == 0 {
		return nil
	}
	out := []string{}
	for _, c := range lists[0] {
		shared := true
		for _, other := range lists[1:] {
			if !hasCodec(other, c) {
				shared = false
				break
			}
		}
		if shared && !hasCodec(out, c) {
			out = append(out, c)
		}
	}
	return out
}

// EvenSize is the size the encoder works at: H.264 in 4:2:0 needs an even width and height, so an odd edge repeats its last pixels.
func EvenSize(width, height int) (int, int) {
	return width + width&1, height + height&1
}

// ToNV12 converts a captured image (B, G, R, A) to NV12 (a Y plane, then interleaved U and V at half resolution) of the even size, with
// BT.709 coefficients in limited range, as the encoder's media type says. dst is reused when it is large enough.
func ToNV12(img *Image, dst []byte) []byte {
	w, h := EvenSize(img.Width, img.Height)
	size := w*h + w*h/2
	if cap(dst) < size {
		dst = make([]byte, size)
	}
	dst = dst[:size]
	// Rows of chroma blocks are split over the processors: a 1920 x 1080 frame converts in a few milliseconds.
	blocks := h / 2
	workers := min(runtime.GOMAXPROCS(0), 8, max(blocks/64, 1))
	var wg sync.WaitGroup
	per := (blocks + workers - 1) / workers
	for start := 0; start < blocks; start += per {
		end := min(start+per, blocks)
		wg.Add(1)
		go func(start, end int) {
			defer wg.Done()
			convertRows(img, dst, w, h, start, end)
		}(start, end)
	}
	wg.Wait()
	return dst
}

// convertRows converts the chroma block rows [start, end) (two pixel rows each).
func convertRows(img *Image, dst []byte, w, h, start, end int) {
	uv := dst[w*h:]
	lastX, lastY := img.Width-1, img.Height-1
	for by := start; by < end; by++ {
		y0 := 2 * by
		rows := [2]int{min(y0, lastY), min(y0+1, lastY)}
		for bx := 0; bx < w/2; bx++ {
			x0 := 2 * bx
			cols := [2]int{min(x0, lastX), min(x0+1, lastX)}
			var sr, sg, sb int
			for j, sy := range rows {
				rowBase := sy * img.Width
				out := (y0 + j) * w
				for i, sx := range cols {
					p := (rowBase + sx) * 4
					b, g, r := int(img.Pix[p]), int(img.Pix[p+1]), int(img.Pix[p+2])
					dst[out+x0+i] = byte((47*r + 157*g + 16*b + 128 + 16<<8) >> 8)
					sr, sg, sb = sr+r, sg+g, sb+b
				}
			}
			// The average of the four pixels, then the chroma of that color.
			r, g, b := (sr+2)>>2, (sg+2)>>2, (sb+2)>>2
			u := (-26*r - 87*g + 112*b + 128 + 128<<8) >> 8
			v := (112*r - 102*g - 10*b + 128 + 128<<8) >> 8
			o := by*w + x0
			uv[o], uv[o+1] = clampByte(u), clampByte(v)
		}
	}
}

func clampByte(v int) byte {
	return byte(min(max(v, 0), 255))
}

// NAL unit types of H.264 used here.
const (
	nalIDR = 5
	nalSPS = 7
	nalPPS = 8
)

// nalTypes lists the NAL unit types of an Annex B access unit, in order.
func nalTypes(data []byte) []byte {
	var types []byte
	for i := 0; i+3 < len(data); i++ {
		if data[i] == 0 && data[i+1] == 0 && data[i+2] == 1 {
			types = append(types, data[i+3]&0x1F)
			i += 3
		}
	}
	return types
}

// hasNAL reports whether an access unit holds a NAL unit of a type.
func hasNAL(data []byte, kind byte) bool {
	for _, t := range nalTypes(data) {
		if t == kind {
			return true
		}
	}
	return false
}

// annexB turns a sequence header (MF_MT_MPEG_SEQUENCE_HEADER) into Annex B: Media Foundation gives it with start codes already, but an
// encoder that gives it as an avcC record (length-prefixed parameter sets) is converted, so the browser always gets start codes.
func annexB(header []byte) []byte {
	if len(header) >= 4 && header[0] == 0 && header[1] == 0 && (header[2] == 1 || (header[2] == 0 && header[3] == 1)) {
		return header
	}
	// avcC: version, profile, compatibility, level, length size, SPS count, [length uint16, SPS]..., PPS count, [length uint16, PPS]...
	if len(header) < 7 || header[0] != 1 {
		return nil
	}
	var out []byte
	rest := header[5:]
	for set := 0; set < 2; set++ {
		if len(rest) < 1 {
			return nil
		}
		count := int(rest[0])
		if set == 0 {
			count &= 0x1F
		}
		rest = rest[1:]
		for i := 0; i < count; i++ {
			if len(rest) < 2 {
				return nil
			}
			n := int(binary.BigEndian.Uint16(rest))
			if len(rest) < 2+n {
				return nil
			}
			out = append(out, 0, 0, 0, 1)
			out = append(out, rest[2:2+n]...)
			rest = rest[2+n:]
		}
	}
	return out
}

// Bit rate of the H.264 stream (0.3.0 step 5): the helper sends a frame only after the browser drew the last one, so a slow link already
// lowers the frame rate. The bit rate follows the link as well, so a frame of moving content on a poor link is small enough to arrive
// within about a frame time. The time a frame needs is its round trip plus its size over the bandwidth. The round trip is learned from
// small frames (only the cursor moved), the bandwidth from frames of about the planned size; the bit rate aims at 70 percent of the
// bandwidth, drops to it at once and climbs toward it slowly. Until a small frame gave the round trip, only a frame that takes longer
// than a second lowers the bit rate: a link that is far away but fast keeps its bit rate.
const (
	// MinBitrate is the lowest bit rate: the screen stays readable, if blurred, on a very poor link.
	MinBitrate = 250_000
	// smallFrame is the largest frame that measures the round trip, whatever the bit rate.
	smallFrame = 2 * 1024
	// verySlow is the time a frame may take before the bit rate drops while the round trip is not known yet.
	verySlow = time.Second
	// linkShare is the part of the bandwidth the bit rate aims at.
	linkShare = 0.7
	// climbAfter is how many frames in a row with room to spare let the bit rate climb one step of 15 percent.
	climbAfter = 10
	// roundTripWindow is how many small frames the round trip is the shortest of.
	roundTripWindow = 32
)

// MaxBitrate is the highest bit rate for an area: about 0.1 bit per pixel at 25 frames a second, between 2 and 16 Mbit/s.
func MaxBitrate(width, height int) int {
	return min(max(width*height*25/10, 2_000_000), 16_000_000)
}

// StartBitrate is where a session starts: half the highest, which a LAN carries easily and a slower link corrects within a second.
func StartBitrate(width, height int) int {
	return max(MaxBitrate(width, height)/2, MinBitrate)
}

// RateControl adapts the bit rate to how fast the browser acknowledges frames.
type RateControl struct {
	bitrate   int
	max       int
	roomy     int
	roundTrip []time.Duration
	// bandwidth is the smoothed estimate in bits per second, 0 until a large frame was measured.
	bandwidth float64
}

// NewRateControl starts at StartBitrate for an area.
func NewRateControl(width, height int) *RateControl {
	return &RateControl{bitrate: StartBitrate(width, height), max: MaxBitrate(width, height)}
}

// Bitrate is the bit rate to encode at now.
func (r *RateControl) Bitrate() int { return r.bitrate }

// Acked takes the time between sending a frame of a number of bytes and its acknowledgement, and reports whether the bit rate changed.
func (r *RateControl) Acked(bytes int, delay time.Duration) bool {
	// The size a frame of moving content has at this bit rate; a quarter of it (at most smallFrame) is small, half of it is large.
	planned := r.bitrate / videoFrameRate / 8
	if bytes <= min(planned/4, smallFrame) {
		if len(r.roundTrip) == roundTripWindow {
			r.roundTrip = r.roundTrip[1:]
		}
		r.roundTrip = append(r.roundTrip, delay)
		return false
	}
	if bytes < planned/2 {
		return false
	}
	if len(r.roundTrip) == 0 {
		old := r.bitrate
		if delay > verySlow {
			r.bitrate = max(MinBitrate, r.bitrate*3/4)
		}
		return r.bitrate != old
	}
	roundTrip := r.roundTrip[0]
	for _, d := range r.roundTrip {
		roundTrip = min(roundTrip, d)
	}
	transfer := max(delay-roundTrip, time.Millisecond)
	sample := float64(bytes*8) / transfer.Seconds()
	if r.bandwidth == 0 {
		r.bandwidth = sample
	} else {
		r.bandwidth = 0.7*r.bandwidth + 0.3*sample
	}
	target := min(max(int(r.bandwidth*linkShare), MinBitrate), r.max)
	old := r.bitrate
	switch {
	case target < r.bitrate*9/10:
		r.roomy = 0
		r.bitrate = target
	case target > r.bitrate:
		r.roomy++
		if r.roomy >= climbAfter {
			r.roomy = 0
			r.bitrate = min(target, r.bitrate*115/100)
		}
	default:
		r.roomy = 0
	}
	return r.bitrate != old
}
