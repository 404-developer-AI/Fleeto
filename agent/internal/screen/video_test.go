package screen

import (
	"bytes"
	"context"
	"encoding/base64"
	"encoding/json"
	"testing"
	"time"
)

func TestVideoUpdatesCarryTheHeaderAndSplitALargeFrame(t *testing.T) {
	data := bytes.Repeat([]byte{0, 0, 0, 1, 0x65, 0xAA}, MaxFrameBytes/3) // about two frames of data
	updates, err := VideoUpdates(VideoFrame{Number: 42, Key: true, Width: 1921, Height: 1081, Endpoint: 12 * time.Millisecond,
		Waited: 3 * time.Millisecond, Data: data})
	if err != nil {
		t.Fatal(err)
	}
	if len(updates) != 3 {
		t.Fatalf("%d updates, want 3", len(updates))
	}
	var joined []byte
	for i, update := range updates {
		if len(update) > MaxFrameBytes {
			t.Fatalf("update %d is %d bytes, more than a frame", i, len(update))
		}
		v, last, err := ParseVideo(update)
		if err != nil {
			t.Fatal(err)
		}
		if v.Number != 42 || !v.Key || v.Width != 1921 || v.Height != 1081 || v.Endpoint != 12*time.Millisecond || v.Waited != 3*time.Millisecond {
			t.Fatalf("update %d header %+v", i, v)
		}
		if last != (i == len(updates)-1) {
			t.Fatalf("update %d last=%v", i, last)
		}
		// The hub reads frame number and last flag the same way as for tiles.
		number, hubLast, ok := updateHeader(update)
		if !ok || number != 42 || hubLast != last {
			t.Fatalf("the hub reads update %d as %d %v %v", i, number, hubLast, ok)
		}
		joined = append(joined, v.Data...)
	}
	if !bytes.Equal(joined, data) {
		t.Fatal("the parts do not join to the frame")
	}
	if _, err := VideoUpdates(VideoFrame{Number: 1, Width: 10, Height: 10}); err == nil {
		t.Fatal("an empty frame was packed")
	}
	if !FromHelper(FrameVideo) || FromBrowser(FrameVideo) || !IsControlFrame(FrameVideo) {
		t.Fatal("FrameVideo must come from the helper only")
	}
}

func TestToNV12ConvertsWithBT709LimitedRange(t *testing.T) {
	// Three pixels wide and high: the odd edge repeats its last column and row.
	img := &Image{Width: 3, Height: 3, Pix: make([]byte, 3*3*4)}
	set := func(x, y int, r, g, b byte) {
		p := (y*3 + x) * 4
		img.Pix[p], img.Pix[p+1], img.Pix[p+2], img.Pix[p+3] = b, g, r, 0xFF
	}
	for y := 0; y < 3; y++ {
		for x := 0; x < 3; x++ {
			set(x, y, 255, 255, 255)
		}
	}
	set(2, 0, 0, 0, 0)
	set(2, 1, 0, 0, 0)
	set(2, 2, 0, 0, 0)
	nv12 := ToNV12(img, nil)
	w, h := EvenSize(3, 3)
	if w != 4 || h != 4 || len(nv12) != 4*4*3/2 {
		t.Fatalf("size %dx%d, %d bytes", w, h, len(nv12))
	}
	// Row 0: white, white, black, black (the repeated edge).
	if got := nv12[0:4]; !bytes.Equal(got, []byte{235, 235, 16, 16}) {
		t.Fatalf("luma row 0 = %v", got)
	}
	// Row 3 repeats row 2.
	if !bytes.Equal(nv12[12:16], nv12[8:12]) {
		t.Fatalf("the last row does not repeat: %v vs %v", nv12[12:16], nv12[8:12])
	}
	// White and black carry no color: chroma stays at the middle.
	for i, c := range nv12[16:] {
		if c < 127 || c > 129 {
			t.Fatalf("chroma byte %d = %d, want about 128", i, c)
		}
	}

	red := &Image{Width: 2, Height: 2, Pix: bytes.Repeat([]byte{0, 0, 255, 255}, 4)}
	out := ToNV12(red, nil)
	// BT.709 limited range: red is Y 63, Cb 102, Cr 240.
	if y, u, v := out[0], out[4], out[5]; absDiff(y, 63) > 1 || absDiff(u, 102) > 1 || absDiff(v, 240) > 1 {
		t.Fatalf("red became Y %d U %d V %d", y, u, v)
	}
}

func absDiff(a, b byte) int {
	if a > b {
		return int(a - b)
	}
	return int(b - a)
}

func TestNALHelpers(t *testing.T) {
	unit := []byte{0, 0, 0, 1, 0x67, 1, 2, 0, 0, 1, 0x68, 3, 0, 0, 1, 0x65, 9, 9}
	if got := nalTypes(unit); !bytes.Equal(got, []byte{nalSPS, nalPPS, nalIDR}) {
		t.Fatalf("NAL types %v", got)
	}
	if !hasNAL(unit, nalIDR) || hasNAL(unit[:10], nalIDR) {
		t.Fatal("hasNAL")
	}
	// An avcC record becomes Annex B; Annex B stays as it is.
	avcc := []byte{1, 0x4D, 0x40, 0x1F, 0xFF, 0xE1, 0, 3, 0x67, 1, 2, 1, 0, 2, 0x68, 3}
	if got := annexB(avcc); !bytes.Equal(got, []byte{0, 0, 0, 1, 0x67, 1, 2, 0, 0, 0, 1, 0x68, 3}) {
		t.Fatalf("annexB(avcC) = %v", got)
	}
	if got := annexB(unit); !bytes.Equal(got, unit) {
		t.Fatal("Annex B was changed")
	}
	if annexB([]byte{1, 2}) != nil {
		t.Fatal("garbage was accepted as a sequence header")
	}
}

func TestCodecChoice(t *testing.T) {
	if ChooseCodec([]string{"h264"}, true) != CodecH264 || ChooseCodec([]string{"h264"}, false) != CodecTiles ||
		ChooseCodec(nil, true) != CodecTiles || ChooseCodec([]string{"vp9"}, true) != CodecTiles {
		t.Fatal("ChooseCodec")
	}
	if got := commonCodecs([][]string{{"h264", "vp9"}, {"vp9", "h264"}}); len(got) != 2 || got[0] != "h264" {
		t.Fatalf("commonCodecs = %v", got)
	}
	if got := commonCodecs([][]string{{"h264"}, nil}); len(got) != 0 {
		t.Fatalf("a browser without H.264 must keep everyone on tiles: %v", got)
	}
	if got := commonCodecs(nil); got != nil {
		t.Fatalf("commonCodecs(nil) = %v", got)
	}
}

// simulatedLink is a link of a bandwidth and a round trip, carrying one frame at a time (the helper waits for each acknowledgement).
type simulatedLink struct {
	bitsPerSecond int
	roundTrip     time.Duration
}

func (l simulatedLink) delay(bytes int) time.Duration {
	return l.roundTrip + time.Duration(float64(bytes*8)/float64(l.bitsPerSecond)*float64(time.Second))
}

// run sends frames over the link as an encoder at a constant bit rate makes them of a screen with moving content, with a small frame now
// and then (only the cursor moved), and returns the delay of the last large frame.
func (l simulatedLink) run(r *RateControl, frames int) time.Duration {
	var last time.Duration
	for i := 0; i < frames; i++ {
		bytes := r.Bitrate() / 25 / 8
		if i%5 == 4 {
			bytes = 250
		}
		d := l.delay(bytes)
		r.Acked(bytes, d)
		if bytes > smallFrame {
			last = d
		}
	}
	return last
}

func TestRateControlFollowsAPoorLink(t *testing.T) {
	link := simulatedLink{bitsPerSecond: 1_000_000, roundTrip: 60 * time.Millisecond}
	r := NewRateControl(1920, 1080)
	start := r.Bitrate()
	last := link.run(r, 300)
	if r.Bitrate() >= start || r.Bitrate() > 1_000_000 || r.Bitrate() < MinBitrate {
		t.Fatalf("the bit rate is %d on a 1 Mbit/s link (started at %d)", r.Bitrate(), start)
	}
	// A frame of moving content arrives within about a frame time on top of the round trip, instead of taking seconds.
	if last > link.roundTrip+80*time.Millisecond {
		t.Fatalf("a frame still takes %v on the poor link", last)
	}
	t.Logf("1 Mbit/s, 60 ms: %d bit/s, a frame of moving content in %v", r.Bitrate(), last)
}

func TestRateControlOnAVeryPoorLinkStopsAtTheMinimum(t *testing.T) {
	link := simulatedLink{bitsPerSecond: 200_000, roundTrip: 150 * time.Millisecond}
	r := NewRateControl(1920, 1080)
	link.run(r, 300)
	if r.Bitrate() != MinBitrate {
		t.Fatalf("the bit rate is %d on a 200 kbit/s link, want the minimum %d", r.Bitrate(), MinBitrate)
	}
}

func TestRateControlKeepsTheBitrateOnAFastLinkFarAway(t *testing.T) {
	// 50 Mbit/s but 250 ms round trip: every acknowledgement is slow, yet the link has room.
	link := simulatedLink{bitsPerSecond: 50_000_000, roundTrip: 250 * time.Millisecond}
	r := NewRateControl(1920, 1080)
	start := r.Bitrate()
	link.run(r, 300)
	if r.Bitrate() < start {
		t.Fatalf("the bit rate dropped from %d to %d on a fast link far away", start, r.Bitrate())
	}
}

func TestRateControlClimbsBackWhenTheLinkRecovers(t *testing.T) {
	r := NewRateControl(1920, 1080)
	simulatedLink{bitsPerSecond: 500_000, roundTrip: 20 * time.Millisecond}.run(r, 200)
	low := r.Bitrate()
	simulatedLink{bitsPerSecond: 100_000_000, roundTrip: 2 * time.Millisecond}.run(r, 500)
	if r.Bitrate() != MaxBitrate(1920, 1080) {
		t.Fatalf("the bit rate went from %d to %d after the link recovered, want the maximum %d", low, r.Bitrate(), MaxBitrate(1920, 1080))
	}
}

func startWithCodecs(tech *technician, codecs ...string) {
	body, _ := json.Marshal(StartBody{Monitor: 0, Codecs: codecs})
	tech.p.Handle(context.Background(), append([]byte{FrameStart}, body...))
}

func codecsOf(t *testing.T, frame []byte) []string {
	t.Helper()
	var body StartBody
	if err := json.Unmarshal(frame[1:], &body); err != nil {
		t.Fatal(err)
	}
	return body.Codecs
}

func TestTheHubAsksForH264OnlyWhenEveryBrowserDecodesIt(t *testing.T) {
	h := newSessionsHarness(t, nil)
	anna := h.join("a", "Anna", nil)
	startWithCodecs(anna, CodecH264)
	helper := h.helper(0)
	if got := codecsOf(t, waitFor(t, helper, FrameStart)); len(got) != 1 || got[0] != CodecH264 {
		t.Fatalf("one browser with H.264: codecs %v", got)
	}

	// A browser without H.264 joins: everyone gets tiles.
	bert := h.join("b", "Bert", nil)
	startWithCodecs(bert)
	if got := codecsOf(t, waitFor(t, helper, FrameStart)); len(got) != 0 {
		t.Fatalf("with a browser without H.264: codecs %v", got)
	}

	// Anna changes monitor: still tiles, because Bert is still there.
	startWithCodecs(anna, CodecH264)
	if got := codecsOf(t, waitFor(t, helper, FrameStart)); len(got) != 0 {
		t.Fatalf("after Anna's second Start: codecs %v", got)
	}

	// Bert leaves: the helper is told it may send H.264 again.
	bert.p.Close()
	if got := codecsOf(t, waitFor(t, helper, FrameStart)); len(got) != 1 || got[0] != CodecH264 {
		t.Fatalf("after the browser without H.264 left: codecs %v", got)
	}
}

func TestTheHubPassesVideoFramesWithFlowControl(t *testing.T) {
	h := newSessionsHarness(t, func(o *SessionsOptions) { o.LagAllowance = time.Hour })
	anna := h.join("a", "Anna", nil)
	startWithCodecs(anna, CodecH264)
	helper := h.helper(0)
	waitFor(t, helper, FrameStart)
	updates, err := VideoUpdates(VideoFrame{Number: 5, Key: true, Width: 10, Height: 10, Data: bytes.Repeat([]byte{1}, MaxFrameBytes)})
	if err != nil {
		t.Fatal(err)
	}
	go func() {
		for _, update := range updates {
			_ = WriteFrame(helper.outW, update)
		}
	}()
	for range updates {
		anna.next(FrameVideo)
	}
	noHelperFrame(t, helper, FrameAck)
	anna.p.Handle(context.Background(), append([]byte{FrameAck}, `{"frame":5}`...))
	waitFor(t, helper, FrameAck)
}

// VideoVector is the FrameVideo that tests/browser/remote-video.test.mjs parses and decodes: both sides must agree on the layout.
const VideoVector = "HwAAAAcDB38EOAAALuAAAAu4AAAAAWdNQCiqAAAAAWjuAAAAAWWIhA=="

func TestTheBrowserTestsVideoVectorMatchesTheLayout(t *testing.T) {
	unit := []byte{0, 0, 0, 1, 0x67, 0x4d, 0x40, 0x28, 0xAA, 0, 0, 0, 1, 0x68, 0xEE, 0, 0, 0, 1, 0x65, 0x88, 0x84}
	updates, err := VideoUpdates(VideoFrame{Number: 7, Key: true, Width: 1919, Height: 1080, Endpoint: 12 * time.Millisecond,
		Waited: 3 * time.Millisecond, Data: unit})
	if err != nil || len(updates) != 1 {
		t.Fatalf("updates: %v", err)
	}
	if got := base64.StdEncoding.EncodeToString(updates[0]); got != VideoVector {
		t.Fatalf("the FrameVideo layout changed: %s; update tests/browser/remote-video.test.mjs too", got)
	}
}
