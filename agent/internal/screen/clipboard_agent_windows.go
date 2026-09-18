//go:build windows

package screen

import (
	"context"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"sync"
)

// The clipboard of a remote control session, served as the user signed in on the Windows session (0.3.0 step 4). Run by
// "fleeto-agent remote-clipboard", started by UserLauncher with that user's token. It watches their clipboard and puts text and files on
// it for them; it never touches the screen, the keyboard or the network, and it can do no more than the user it runs as.
//
// The agent service cannot do this itself: Windows Explorer hands its copied files out through OLE, and a process running as SYSTEM gets
// nothing from it (seen on the test endpoint, where a copy read as an empty clipboard whatever was copied).

// RunClipboardAgent serves the clipboard until its input closes.
func RunClipboardAgent(ctx context.Context, in io.Reader, out io.Writer, logger *slog.Logger) error {
	var writeMu sync.Mutex
	write := func(frame []byte) error {
		writeMu.Lock()
		defer writeMu.Unlock()
		return WriteFrame(out, frame)
	}

	ui := startDesktopUI(write, logger, true)
	defer ui.stop()
	if !ui.running.Load() {
		return errors.New("the clipboard of this Windows session could not be opened")
	}

	frames := make(chan []byte)
	readErr := make(chan error, 1)
	go func() {
		for {
			frame, err := ReadFrame(in)
			if err != nil {
				readErr <- err
				return
			}
			frames <- frame
		}
	}()

	for {
		select {
		case <-ctx.Done():
			return nil
		case err := <-readErr:
			if errors.Is(err, io.EOF) {
				return nil
			}
			return err
		case frame := <-frames:
			handleClipboardFrame(ui, frame)
		}
	}
}

func handleClipboardFrame(ui *desktopUI, frame []byte) {
	if len(frame) == 0 {
		return
	}
	switch frame[0] {
	case FrameClipboard:
		if len(frame)-1 <= MaxClipboardBytes {
			ui.setText(string(frame[1:]))
		}
	case FramePlaceFiles:
		var place PlaceFilesBody
		if json.Unmarshal(frame[1:], &place) == nil {
			ui.placeFiles(place.Paths)
		}
	}
}
