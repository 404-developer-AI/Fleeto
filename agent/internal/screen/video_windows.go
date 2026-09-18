//go:build windows

package screen

import (
	"bytes"
	"errors"
	"log/slog"
	"time"
)

// settleFrames is how many frames of a screen that stopped changing are still encoded: at a constant bit rate the encoder sharpens a
// still picture over the next frames, so text is crisp again shortly after scrolling stops.
const settleFrames = 8

// videoStream sends the screen of a helper as H.264. It lives on the helper's capture thread; the encoder itself runs on the Media
// Foundation thread.
type videoStream struct {
	logger *slog.Logger
	mf     *mfThread
	enc    *mfEncoder
	// noHardware is set once the hardware encoder failed in this helper: from then on only the software encoder is tried.
	noHardware bool

	width, height int
	nv12          []byte
	prev          []byte
	settle        int
	rate          *RateControl
	started       time.Time
}

// errNoEncoder says that the endpoint cannot encode H.264 at all, so trying again later is pointless.
var errNoEncoder = errors.New("no H.264 encoder")

func newVideoStream(logger *slog.Logger) (*videoStream, error) {
	mf, err := startMF()
	if err != nil {
		return nil, err
	}
	return &videoStream{logger: logger, mf: mf, started: time.Now()}, nil
}

// kind says which encoder runs: "hardware" or "software", "" before the first frame.
func (v *videoStream) kind() string {
	if v.enc == nil {
		return ""
	}
	return v.enc.kind
}

// open (re)opens the encoder for an area; a new encoder starts with a key frame.
func (v *videoStream) open(width, height int) error {
	v.closeEncoder()
	w, h := EvenSize(width, height)
	if v.rate == nil || v.width != width || v.height != height {
		v.rate = NewRateControl(w, h)
	}
	var enc *mfEncoder
	var err error
	hardware := !v.noHardware
	v.mf.do(func() { enc, err = openH264(w, h, v.rate.Bitrate(), hardware) })
	if err != nil {
		return err
	}
	v.enc = enc
	v.width, v.height = width, height
	v.prev = v.prev[:0]
	v.logger.Info("remote control sends H.264", "encoder", enc.name, "width", w, "height", h, "bitrate", v.rate.Bitrate())
	return nil
}

// frame encodes a captured image. It returns nil without an error when the screen did not change and has settled, so nothing is sent.
func (v *videoStream) frame(img *Image, key bool) (data []byte, isKey bool, err error) {
	if v.enc == nil || img.Width != v.width || img.Height != v.height {
		if err := v.open(img.Width, img.Height); err != nil {
			return nil, false, err
		}
		key = true
	}
	changed := !bytes.Equal(img.Pix, v.prev)
	switch {
	case changed:
		v.prev = append(v.prev[:0], img.Pix...)
		v.settle = settleFrames
	case key:
	case v.settle > 0:
		v.settle--
	default:
		return nil, false, nil
	}
	v.nv12 = ToNV12(img, v.nv12)
	data, err = v.encode(key)
	if err != nil && v.enc != nil && v.enc.kind == "hardware" {
		// A hardware encoder that fails is not tried again in this session; the software encoder takes over with a key frame.
		v.logger.Warn("the hardware H.264 encoder failed; the software encoder takes over", "error", err)
		v.noHardware = true
		if err = v.open(img.Width, img.Height); err == nil {
			key = true
			data, err = v.encode(true)
		}
	}
	if err != nil {
		return nil, false, err
	}
	isKey = hasNAL(data, nalIDR)
	if key && !isKey {
		// The encoder ignored the request: a new encoder always starts with a key frame.
		if err := v.open(img.Width, img.Height); err != nil {
			return nil, false, err
		}
		if data, err = v.encode(true); err != nil {
			return nil, false, err
		}
		if isKey = hasNAL(data, nalIDR); !isKey {
			return nil, false, errors.New("the H.264 encoder does not start with a key frame")
		}
	}
	if isKey && !hasNAL(data, nalSPS) {
		if len(v.enc.header) == 0 {
			return nil, false, errors.New("the H.264 encoder gives no sequence header")
		}
		data = append(append([]byte(nil), v.enc.header...), data...)
	}
	return data, isKey, nil
}

func (v *videoStream) encode(key bool) (data []byte, err error) {
	at := time.Since(v.started)
	v.mf.do(func() { data, err = v.enc.encode(v.nv12, key, at) })
	return data, err
}

// acked lets the bit rate follow the link.
func (v *videoStream) acked(bytes int, delay time.Duration) {
	if v.enc == nil || v.rate == nil || !v.rate.Acked(bytes, delay) {
		return
	}
	bitrate := v.rate.Bitrate()
	v.mf.do(func() { v.enc.setBitrate(bitrate) })
	v.logger.Debug("remote control bit rate changed", "bitrate", bitrate, "delay", delay)
}

// reset forgets the last image, so the next frame is encoded even when the screen did not change.
func (v *videoStream) reset() {
	v.prev = v.prev[:0]
}

func (v *videoStream) closeEncoder() {
	if v.enc == nil {
		return
	}
	enc := v.enc
	v.mf.do(enc.close)
	v.enc = nil
}

func (v *videoStream) close() {
	v.closeEncoder()
	v.mf.stop()
}
