// Package screen serves the screen of a remote control session (0.3.0 step 3): capture, change detection and tile encoding, and
// keyboard and mouse input. On Windows the agent service cannot see a desktop (it runs in session 0), so it starts a helper process as
// SYSTEM in the chosen Windows session (Launch) and passes it the session frames of this package over the helper's standard input and
// output. The helper captures, encodes and injects; the agent only encrypts and relays.
package screen

import (
	"encoding/binary"
	"errors"
	"fmt"
	"io"
)

// Frame types of remote control, inside the encrypted session (the first byte of a decrypted frame, next to the remote background
// frames of internal/remote). Bodies are JSON unless stated otherwise. The web viewer (remote-control.js) holds the same list.
const (
	// FrameStart (browser to endpoint): show a monitor. Body {monitor}: an index from ScreenInfo, -1 for all monitors together.
	FrameStart byte = 0x10
	// FrameInfo (endpoint to browser): the monitors, the one shown and its size, and the desktop (Default, Winlogon for the sign-in
	// screen and UAC). Sent after Start and whenever it changes.
	FrameInfo byte = 0x11
	// FrameAck (browser to endpoint): the browser drew a frame. Body {frame}. The endpoint sends the next frame only after it.
	FrameAck byte = 0x12
	// FramePointer (browser to endpoint): mouse position in pixels of the shown image, the buttons held and a wheel step.
	FramePointer byte = 0x13
	// FrameKey (browser to endpoint): a key went down or up, with the physical key (code), the character (key) and the modifiers.
	FrameKey byte = 0x14
	// FrameType (browser to endpoint): type a text as keystrokes ("Type clipboard"). Body {text}.
	FrameType byte = 0x15
	// FrameSecureAttention (browser to endpoint): Ctrl+Alt+Del. Handled by the agent service itself (SendSAS), not the helper.
	FrameSecureAttention byte = 0x16
	// FrameReleaseKeys (browser to endpoint): release every key the technician holds (the window lost focus).
	FrameReleaseKeys byte = 0x17
	// FrameUpdate (endpoint to browser): changed tiles of one frame. Binary, see AppendUpdate.
	FrameUpdate byte = 0x18
	// FrameNotice (endpoint to browser): something the technician should know. Body {message}.
	FrameNotice byte = 0x19

	// 0.3.0 step 4: clipboard, several technicians, consent.

	// FrameClipboard (both ways): the clipboard text. Binary body: the text as UTF-8, at most MaxClipboardBytes.
	FrameClipboard byte = 0x1A
	// FrameParticipants (endpoint to browser): the technicians who see the screen. Body {participants: [{id, name, you}]}.
	FrameParticipants byte = 0x1B
	// FramePeerPointer (endpoint to browser): where another technician points. Body {id, x, y} in pixels of the shown image.
	FramePeerPointer byte = 0x1C
	// FrameConsent (endpoint to browser): the consent prompt. Body {state: "waiting" or "granted", user, secondsLeft, message}.
	FrameConsent byte = 0x1D
	// FrameClipboardFiles (endpoint to browser): the files copied on the endpoint, offered for download. Body {files: [{index, name,
	// size}], folders}.
	FrameClipboardFiles byte = 0x1E
)

// Frames between the agent service and its helper only (0.3.0 step 4); they never cross the relay.
const (
	// FrameBanner (agent to helper): show the banner with these technicians, or hide it for none. Body {names}.
	FrameBanner byte = 0x20
	// FramePlaceFiles (agent to helper): put these staged files on the clipboard. Body {paths}.
	FramePlaceFiles byte = 0x21
	// FrameCopiedFiles (helper to agent): the files on the clipboard now, none when it holds no files. Body {files: [{path, name, size,
	// dir}]}.
	FrameCopiedFiles byte = 0x22
)

// IsControlFrame reports whether a frame type belongs to remote control.
func IsControlFrame(kind byte) bool { return kind >= FrameStart && kind <= 0x1F }

// FromBrowser reports whether the browser may send a frame type of remote control.
func FromBrowser(kind byte) bool {
	switch kind {
	case FrameStart, FrameAck, FramePointer, FrameKey, FrameType, FrameSecureAttention, FrameReleaseKeys, FrameClipboard:
		return true
	}
	return false
}

// FromAgent reports whether a frame type is one the agent service sends its helper on its own.
func FromAgent(kind byte) bool { return kind == FrameBanner || kind == FramePlaceFiles }

// FromHelper reports whether the helper may send a frame type to the agent service.
func FromHelper(kind byte) bool {
	switch kind {
	case FrameInfo, FrameUpdate, FrameNotice, FrameClipboard, FrameCopiedFiles:
		return true
	}
	return false
}

// MaxFrameBytes is the largest frame the helper sends; with the encryption overhead it stays well inside the relay's 1 MiB message limit.
const MaxFrameBytes = 768 * 1024

// maxPipeFrame bounds what either side reads from the helper pipe.
const maxPipeFrame = MaxFrameBytes + 64*1024

// StartBody is the body of FrameStart.
type StartBody struct {
	Monitor int `json:"monitor"`
}

// Monitor is one display of the endpoint, in virtual desktop pixels.
type Monitor struct {
	Index   int    `json:"index"`
	Name    string `json:"name"`
	X       int    `json:"x"`
	Y       int    `json:"y"`
	Width   int    `json:"width"`
	Height  int    `json:"height"`
	Primary bool   `json:"primary"`
}

// InfoBody is the body of FrameInfo.
type InfoBody struct {
	Monitors []Monitor `json:"monitors"`
	// Monitor is the index shown, -1 for all together.
	Monitor int    `json:"monitor"`
	Width   int    `json:"width"`
	Height  int    `json:"height"`
	Desktop string `json:"desktop"`
	// Session is the Windows session shown.
	Session uint32 `json:"session"`
}

// AckBody is the body of FrameAck.
type AckBody struct {
	Frame uint32 `json:"frame"`
}

// PointerBody is the body of FramePointer: x and y in pixels of the shown image, buttons as in the DOM (1 left, 2 right, 4 middle) and
// a wheel step (positive is away from the user, in notches).
type PointerBody struct {
	X       int `json:"x"`
	Y       int `json:"y"`
	Buttons int `json:"buttons"`
	Wheel   int `json:"wheel,omitempty"`
	HWheel  int `json:"hwheel,omitempty"`
}

// KeyBody is the body of FrameKey, as the browser's KeyboardEvent describes it.
type KeyBody struct {
	Code     string `json:"code"`
	Key      string `json:"key"`
	Down     bool   `json:"down"`
	Ctrl     bool   `json:"ctrl"`
	Alt      bool   `json:"alt"`
	Shift    bool   `json:"shift"`
	Meta     bool   `json:"meta"`
	AltGraph bool   `json:"altGraph"`
}

// TypeBody is the body of FrameType.
type TypeBody struct {
	Text string `json:"text"`
}

// MaxTypeRunes bounds one Type clipboard.
const MaxTypeRunes = 10000

// MaxClipboardBytes is the most clipboard text that is synchronised, as UTF-8.
const MaxClipboardBytes = 512 * 1024

// MaxCopiedFiles is how many files copied on the endpoint are offered for download at once.
const MaxCopiedFiles = 100

// ParticipantInfo is one technician in FrameParticipants.
type ParticipantInfo struct {
	ID   string `json:"id"`
	Name string `json:"name"`
	You  bool   `json:"you"`
}

// PeerPointerBody is the body of FramePeerPointer.
type PeerPointerBody struct {
	ID string `json:"id"`
	X  int    `json:"x"`
	Y  int    `json:"y"`
}

// ConsentBody is the body of FrameConsent.
type ConsentBody struct {
	State       string `json:"state"`
	User        string `json:"user,omitempty"`
	SecondsLeft int    `json:"secondsLeft,omitempty"`
	Message     string `json:"message,omitempty"`
}

// BannerBody is the body of FrameBanner.
type BannerBody struct {
	Names []string `json:"names"`
}

// PlaceFilesBody is the body of FramePlaceFiles.
type PlaceFilesBody struct {
	Paths []string `json:"paths"`
}

// CopiedFile is a file on the endpoint clipboard.
type CopiedFile struct {
	Path string `json:"path"`
	Name string `json:"name"`
	Size int64  `json:"size"`
	Dir  bool   `json:"dir"`
}

// CopiedFilesBody is the body of FrameCopiedFiles.
type CopiedFilesBody struct {
	Files []CopiedFile `json:"files"`
}

// OfferedFile is a copied file as the browser sees it: no path, an index to download it by.
type OfferedFile struct {
	Index int    `json:"index"`
	Name  string `json:"name"`
	Size  int64  `json:"size"`
}

// ClipboardFilesBody is the body of FrameClipboardFiles.
type ClipboardFilesBody struct {
	Files []OfferedFile `json:"files"`
	// Folders is how many copied folders are not offered (only files can be downloaded).
	Folders int `json:"folders,omitempty"`
}

// NoticeBody is the body of FrameNotice.
type NoticeBody struct {
	Message string `json:"message"`
}

// WriteFrame writes one frame on the helper pipe: a 32-bit big-endian length, then the frame (type byte and body).
func WriteFrame(w io.Writer, frame []byte) error {
	if len(frame) == 0 || len(frame) > maxPipeFrame {
		return fmt.Errorf("a helper frame of %d bytes is not allowed", len(frame))
	}
	buf := make([]byte, 4+len(frame))
	binary.BigEndian.PutUint32(buf, uint32(len(frame)))
	copy(buf[4:], frame)
	_, err := w.Write(buf)
	return err
}

// ReadFrame reads one frame from the helper pipe.
func ReadFrame(r io.Reader) ([]byte, error) {
	var header [4]byte
	if _, err := io.ReadFull(r, header[:]); err != nil {
		return nil, err
	}
	n := binary.BigEndian.Uint32(header[:])
	if n == 0 || n > maxPipeFrame {
		return nil, errors.New("the helper pipe carries a frame of an invalid length")
	}
	frame := make([]byte, n)
	if _, err := io.ReadFull(r, frame); err != nil {
		return nil, err
	}
	return frame, nil
}
