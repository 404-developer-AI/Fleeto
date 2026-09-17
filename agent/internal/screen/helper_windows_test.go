//go:build windows

package screen

import (
	"bytes"
	"context"
	"encoding/json"
	"image/jpeg"
	"image/png"
	"io"
	"log/slog"
	"os"
	"testing"
	"time"
)

// TestTheHelperCapturesThisDesktop runs the real helper against the desktop of the test process: Start, then Info and a whole first frame
// whose tiles decode. It needs an interactive desktop, so it runs only with FLEETO_SCREEN_TEST=1 (not in CI). It sends no input.
func TestTheHelperCapturesThisDesktop(t *testing.T) {
	if os.Getenv("FLEETO_SCREEN_TEST") != "1" {
		t.Skip("set FLEETO_SCREEN_TEST=1 on a machine with an interactive desktop")
	}
	toHelperR, toHelperW := io.Pipe()
	fromHelperR, fromHelperW := io.Pipe()
	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()
	done := make(chan error, 1)
	go func() {
		done <- RunHelper(ctx, toHelperR, fromHelperW, 1, slog.New(slog.NewTextHandler(os.Stderr, nil)))
	}()

	start, _ := json.Marshal(StartBody{Monitor: -2})
	go func() { _ = WriteFrame(toHelperW, append([]byte{FrameStart}, start...)) }()

	frame, err := ReadFrame(fromHelperR)
	if err != nil || frame[0] != FrameInfo {
		t.Fatalf("expected info, got %v %v", frame, err)
	}
	var info InfoBody
	if err := json.Unmarshal(frame[1:], &info); err != nil || info.Width <= 0 || info.Height <= 0 || len(info.Monitors) == 0 {
		t.Fatalf("info %s", frame[1:])
	}
	t.Logf("desktop %q, monitor %d of %d, %dx%d", info.Desktop, info.Monitor, len(info.Monitors), info.Width, info.Height)

	covered := 0
	started := time.Now()
	var id uint32
	for {
		update, err := ReadFrame(fromHelperR)
		if err != nil {
			t.Fatal(err)
		}
		frameID, last, tiles, err := ParseUpdate(update)
		if err != nil {
			t.Fatal(err)
		}
		id = frameID
		for _, tile := range tiles {
			covered += tile.W * tile.H
			switch tile.Format {
			case FormatPNG:
				_, err = png.Decode(bytes.NewReader(tile.Data))
			case FormatJPEG:
				_, err = jpeg.Decode(bytes.NewReader(tile.Data))
			}
			if err != nil {
				t.Fatalf("tile at %d,%d does not decode: %v", tile.X, tile.Y, err)
			}
		}
		if last {
			break
		}
	}
	t.Logf("first frame %d: %d pixels in %s", id, covered, time.Since(started))
	if covered != info.Width*info.Height {
		t.Fatalf("the first frame covers %d pixels, want %d", covered, info.Width*info.Height)
	}
	ack, _ := json.Marshal(AckBody{Frame: id})
	go func() { _ = WriteFrame(toHelperW, append([]byte{FrameAck}, ack...)) }()
	time.Sleep(200 * time.Millisecond)
	_ = toHelperW.Close()
	go func() { _, _ = io.Copy(io.Discard, fromHelperR) }()
	select {
	case err := <-done:
		if err != nil {
			t.Fatalf("the helper ended with %v", err)
		}
	case <-time.After(10 * time.Second):
		t.Fatal("the helper did not end when its input closed")
	}
}
