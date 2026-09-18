//go:build windows

package screen

import (
	"log/slog"
	"strings"
	"testing"
	"time"
)

// testImage draws a frame with text-like stripes and a moving block, so the encoder has detail and motion.
func testImage(w, h, step int) *Image {
	img := &Image{Width: w, Height: h, Pix: make([]byte, w*h*4)}
	for y := 0; y < h; y++ {
		for x := 0; x < w; x++ {
			p := (y*w + x) * 4
			v := byte(0xF0)
			if (y/12)%2 == 0 && (x/3)%4 != 0 {
				v = 0x20
			}
			img.Pix[p], img.Pix[p+1], img.Pix[p+2], img.Pix[p+3] = v, v, v, 0xFF
		}
	}
	bx := (step * 37) % max(w-100, 1)
	for y := 100; y < min(200, h); y++ {
		for x := bx; x < bx+100 && x < w; x++ {
			p := (y*w + x) * 4
			img.Pix[p], img.Pix[p+1], img.Pix[p+2] = 0x30, 0x80, 0xE0
		}
	}
	return img
}

func openTestStream(t *testing.T) *videoStream {
	t.Helper()
	v, err := newVideoStream(slog.New(slog.DiscardHandler))
	if err != nil {
		t.Skipf("Media Foundation is not available here: %v", err)
	}
	t.Cleanup(v.close)
	return v
}

func encodeFrames(t *testing.T, v *videoStream, w, h, frames int) (keys int, total time.Duration) {
	t.Helper()
	for i := 0; i < frames; i++ {
		img := testImage(w, h, i)
		started := time.Now()
		data, key, err := v.frame(img, i == 0)
		if err != nil {
			if i == 0 && strings.Contains(err.Error(), "not installed") {
				t.Skipf("no H.264 encoder here: %v", err)
			}
			t.Fatalf("frame %d: %v", i, err)
		}
		total += time.Since(started)
		if len(data) == 0 {
			t.Fatalf("frame %d changed but gave no data", i)
		}
		if i == 0 {
			if !key || !hasNAL(data, nalSPS) || !hasNAL(data, nalPPS) || !hasNAL(data, nalIDR) {
				t.Fatalf("the first frame is not a key frame with SPS and PPS: NAL types %v", nalTypes(data))
			}
		}
		if key {
			keys++
		}
	}
	return keys, total
}

func TestH264EncodesKeyThenDeltaFrames(t *testing.T) {
	for _, size := range [][2]int{{1920, 1080}, {1366, 768}, {1023, 767}} {
		v := openTestStream(t)
		keys, total := encodeFrames(t, v, size[0], size[1], 20)
		if keys != 1 {
			t.Fatalf("%dx%d: %d key frames in 20, want only the first", size[0], size[1], keys)
		}
		t.Logf("%dx%d with the %s encoder: %.1f ms a frame (capture excluded)", size[0], size[1], v.kind(), float64(total.Microseconds())/20/1000)
	}
}

func TestH264SoftwareEncoder(t *testing.T) {
	v := openTestStream(t)
	v.noHardware = true
	keys, total := encodeFrames(t, v, 1920, 1080, 20)
	if v.kind() != "software" {
		t.Fatalf("encoder %q, want software", v.kind())
	}
	if keys != 1 {
		t.Fatalf("%d key frames, want 1", keys)
	}
	t.Logf("1920x1080 with the software encoder: %.1f ms a frame", float64(total.Microseconds())/20/1000)
}

func TestH264KeyFrameOnRequest(t *testing.T) {
	v := openTestStream(t)
	encodeFrames(t, v, 1280, 720, 3)
	data, key, err := v.frame(testImage(1280, 720, 99), true)
	if err != nil {
		t.Fatal(err)
	}
	if !key || !hasNAL(data, nalSPS) {
		t.Fatalf("a requested key frame came as NAL types %v", nalTypes(data))
	}
}

func TestH264StillScreenSettlesThenStops(t *testing.T) {
	v := openTestStream(t)
	img := testImage(640, 480, 1)
	sent := 0
	for i := 0; i < settleFrames+5; i++ {
		data, _, err := v.frame(img, i == 0)
		if err != nil {
			if i == 0 && strings.Contains(err.Error(), "not installed") {
				t.Skipf("no H.264 encoder here: %v", err)
			}
			t.Fatal(err)
		}
		if data != nil {
			sent++
		}
	}
	if sent != settleFrames+1 {
		t.Fatalf("%d frames of a still screen were encoded, want %d", sent, settleFrames+1)
	}
}

func TestH264ResizeStartsWithKeyFrame(t *testing.T) {
	v := openTestStream(t)
	encodeFrames(t, v, 800, 600, 2)
	data, key, err := v.frame(testImage(1024, 768, 5), false)
	if err != nil {
		t.Fatal(err)
	}
	if !key || !hasNAL(data, nalSPS) {
		t.Fatalf("the first frame of a new size is not a key frame: %v", nalTypes(data))
	}
}

func TestH264BitrateChangesWhileRunning(t *testing.T) {
	v := openTestStream(t)
	encodeFrames(t, v, 1280, 720, 2)
	// Small frames give the round trip; a large frame that took long lowers the bit rate, and the encoder takes it without a new key frame.
	before := v.rate.Bitrate()
	for i := 0; i < 5; i++ {
		v.acked(200, 20*time.Millisecond)
	}
	v.acked(200_000, 3*time.Second)
	if v.rate.Bitrate() >= before {
		t.Fatalf("bit rate %d did not drop from %d", v.rate.Bitrate(), before)
	}
	_, key, err := v.frame(testImage(1280, 720, 7), false)
	if err != nil {
		t.Fatal(err)
	}
	if key {
		t.Fatal("a bit rate change started a key frame")
	}
}

func BenchmarkCaptureConvertEncode1080p(b *testing.B) {
	v, err := newVideoStream(slog.New(slog.DiscardHandler))
	if err != nil {
		b.Skip(err)
	}
	defer v.close()
	img := testImage(1920, 1080, 0)
	if _, _, err := v.frame(img, true); err != nil {
		b.Skip(err)
	}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		img.Pix[(i%1000)*4] ^= 0xFF // a change every frame
		if _, _, err := v.frame(img, false); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkCaptureConvertEncode1080pSoftware(b *testing.B) {
	v, err := newVideoStream(slog.New(slog.DiscardHandler))
	if err != nil {
		b.Skip(err)
	}
	defer v.close()
	v.noHardware = true
	img := testImage(1920, 1080, 0)
	if _, _, err := v.frame(img, true); err != nil {
		b.Skip(err)
	}
	b.ResetTimer()
	for i := 0; i < b.N; i++ {
		img.Pix[(i%1000)*4] ^= 0xFF
		if _, _, err := v.frame(img, false); err != nil {
			b.Fatal(err)
		}
	}
}

func BenchmarkToNV12_1080p(b *testing.B) {
	img := testImage(1920, 1080, 0)
	var dst []byte
	for i := 0; i < b.N; i++ {
		dst = ToNV12(img, dst)
	}
}
