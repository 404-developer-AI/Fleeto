//go:build windows

package screen

import (
	"encoding/json"
	"log/slog"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

var procIsWindowVisible = user32.NewProc("IsWindowVisible")

// TestTheBannerAndClipboardWorkOnThisDesktop runs the helper's desktop thread on the desktop of the test process: the banner shows and
// hides, text and files a technician places are on the clipboard, and nothing is echoed back as a copy on the endpoint. It changes the
// clipboard of the machine, so it runs only with FLEETO_SCREEN_TEST=1 (not in CI).
func TestTheBannerAndClipboardWorkOnThisDesktop(t *testing.T) {
	if os.Getenv("FLEETO_SCREEN_TEST") != "1" {
		t.Skip("set FLEETO_SCREEN_TEST=1 on a machine with an interactive desktop")
	}
	written := make(chan []byte, 16)
	ui := startDesktopUI(func(frame []byte) error { written <- frame; return nil }, slog.New(slog.NewTextHandler(os.Stderr, nil)))
	if !ui.running.Load() {
		t.Fatal("the desktop thread did not start")
	}
	defer ui.stop()

	visible := func() bool {
		v, _, _ := procIsWindowVisible.Call(ui.hwnd)
		return v != 0
	}
	ui.setBanner([]string{"Anna", "Bert"})
	waitUntil(t, "the banner to show", visible)
	ui.setBanner(nil)
	waitUntil(t, "the banner to hide", func() bool { return !visible() })

	ui.setText("Fleeto clipboard test ü")
	waitUntil(t, "the text on the clipboard", func() bool {
		text := make(chan string, 1)
		ui.do(func() {
			if openClipboard(ui.hwnd) {
				value, _ := clipboardText()
				procCloseClipboard.Call()
				text <- value
			}
			close(text)
		})
		return <-text == "Fleeto clipboard test ü"
	})

	staged := filepath.Join(t.TempDir(), "pasted.txt")
	if err := os.WriteFile(staged, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	ui.placeFiles([]string{staged})
	waitUntil(t, "the files on the clipboard", func() bool {
		names := make(chan []string, 1)
		ui.do(func() {
			if openClipboard(ui.hwnd) {
				names <- dropFileNames(ui.hwnd)
				procCloseClipboard.Call()
			}
			close(names)
		})
		got := <-names
		return len(got) == 1 && got[0] == staged
	})

	// What a technician placed is never offered back to the technicians as a copy on the endpoint.
	select {
	case frame := <-written:
		var body any
		_ = json.Unmarshal(frame[1:], &body)
		t.Fatalf("the desktop thread sent %x %v", frame[0], body)
	case <-time.After(300 * time.Millisecond):
	}
}

func waitUntil(t *testing.T, what string, condition func() bool) {
	t.Helper()
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		if condition() {
			return
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Fatalf("timed out waiting for %s", what)
}

// TestACopyOnTheEndpointIsNoticedWithoutANotification proves the second way the endpoint watches its clipboard: with the clipboard format
// listener removed, the sequence number check alone still offers what another program copied. It changes the clipboard of the machine, so
// it runs only with FLEETO_SCREEN_TEST=1 (not in CI).
func TestACopyOnTheEndpointIsNoticedWithoutANotification(t *testing.T) {
	if os.Getenv("FLEETO_SCREEN_TEST") != "1" {
		t.Skip("set FLEETO_SCREEN_TEST=1 on a machine with an interactive desktop")
	}
	written := make(chan []byte, 32)
	ui := startDesktopUI(func(frame []byte) error { written <- frame; return nil }, slog.New(slog.NewTextHandler(os.Stderr, nil)))
	if !ui.running.Load() {
		t.Fatal("the desktop thread did not start")
	}
	defer ui.stop()
	// Only the poll may notice the copy.
	ui.doWait(func() { procRemoveClipboardFormatListener.Call(ui.hwnd) })

	if err := exec.Command("powershell", "-NoProfile", "-Command", "Set-Clipboard -Value 'copied by another program'").Run(); err != nil {
		t.Fatalf("could not copy from another program: %v", err)
	}

	deadline := time.After(10 * time.Second)
	for {
		select {
		case frame := <-written:
			if frame[0] == FrameClipboard && strings.Contains(string(frame[1:]), "copied by another program") {
				return
			}
		case <-deadline:
			t.Fatal("the copy was not noticed without a notification")
		}
	}
}

// TestAFileCopiedOnTheEndpointIsOffered copies a file to the clipboard the way a program on the endpoint does (CF_HDROP) and expects the
// endpoint to offer it. It changes the clipboard of the machine, so it runs only with FLEETO_SCREEN_TEST=1 (not in CI).
func TestAFileCopiedOnTheEndpointIsOffered(t *testing.T) {
	if os.Getenv("FLEETO_SCREEN_TEST") != "1" {
		t.Skip("set FLEETO_SCREEN_TEST=1 on a machine with an interactive desktop")
	}
	file := filepath.Join(t.TempDir(), "copied.txt")
	if err := os.WriteFile(file, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	written := make(chan []byte, 32)
	ui := startDesktopUI(func(frame []byte) error { written <- frame; return nil }, slog.New(slog.NewTextHandler(os.Stderr, nil)))
	if !ui.running.Load() {
		t.Fatal("the desktop thread did not start")
	}
	defer ui.stop()

	if err := exec.Command("powershell", "-NoProfile", "-Command", "Set-Clipboard -Path '"+file+"'").Run(); err != nil {
		t.Fatalf("could not copy a file from another program: %v", err)
	}

	deadline := time.After(10 * time.Second)
	for {
		select {
		case frame := <-written:
			if frame[0] != FrameCopiedFiles {
				continue
			}
			var body CopiedFilesBody
			if err := json.Unmarshal(frame[1:], &body); err != nil {
				t.Fatal(err)
			}
			if len(body.Files) != 1 || !strings.EqualFold(body.Files[0].Path, file) || body.Files[0].Name != "copied.txt" {
				t.Fatalf("offered %+v", body.Files)
			}
			return
		case <-deadline:
			t.Fatal("a file copied on the endpoint was not offered")
		}
	}
}
