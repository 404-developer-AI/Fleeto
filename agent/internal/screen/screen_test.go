package screen

import (
	"bytes"
	"image/jpeg"
	"image/png"
	"math/rand"
	"testing"
)

func solidImage(w, h int, b, g, r byte) *Image {
	img := &Image{Width: w, Height: h, Pix: make([]byte, w*h*4)}
	for i := 0; i < len(img.Pix); i += 4 {
		img.Pix[i], img.Pix[i+1], img.Pix[i+2], img.Pix[i+3] = b, g, r, 0xFF
	}
	return img
}

func TestTheFirstFrameIsSentWholeAndAnUnchangedFrameSendsNothing(t *testing.T) {
	var e Encoder
	img := solidImage(200, 130, 10, 20, 30)
	tiles, err := e.Encode(img, false)
	if err != nil {
		t.Fatal(err)
	}
	covered := 0
	for _, tile := range tiles {
		covered += tile.W * tile.H
		if tile.Format != FormatPNG {
			t.Fatalf("a one-color rectangle must be PNG, got %d", tile.Format)
		}
	}
	if covered != 200*130 {
		t.Fatalf("the first frame covers %d pixels, want %d", covered, 200*130)
	}
	again, err := e.Encode(img, false)
	if err != nil || len(again) != 0 {
		t.Fatalf("an unchanged frame sent %d tiles (err %v)", len(again), err)
	}
	full, _ := e.Encode(img, true)
	if len(full) != len(tiles) {
		t.Fatalf("a full refresh sent %d tiles, want %d", len(full), len(tiles))
	}
}

func TestOnlyTheChangedTilesAreSentAndTheyDecodeToThePixels(t *testing.T) {
	var e Encoder
	img := solidImage(256, 256, 255, 255, 255)
	if _, err := e.Encode(img, false); err != nil {
		t.Fatal(err)
	}
	// Change one pixel in the tile at (64, 128).
	i := (130*256 + 70) * 4
	img.Pix[i], img.Pix[i+1], img.Pix[i+2] = 0, 0, 255 // red in BGRA
	tiles, err := e.Encode(img, false)
	if err != nil {
		t.Fatal(err)
	}
	if len(tiles) != 1 || tiles[0].X != 64 || tiles[0].Y != 128 || tiles[0].W != 64 || tiles[0].H != 64 {
		t.Fatalf("changed tiles %+v", tiles)
	}
	decoded, err := png.Decode(bytes.NewReader(tiles[0].Data))
	if err != nil {
		t.Fatal(err)
	}
	r, g, b, _ := decoded.At(70-64, 130-128).RGBA()
	if r>>8 != 255 || g != 0 || b != 0 {
		t.Fatalf("decoded pixel is %d,%d,%d, want red", r>>8, g>>8, b>>8)
	}
}

func TestARectangleWithManyColorsIsJPEGAndARowRunJoinsTiles(t *testing.T) {
	var e Encoder
	img := &Image{Width: 64 * 3, Height: 64, Pix: make([]byte, 64*3*64*4)}
	rand.New(rand.NewSource(1)).Read(img.Pix)
	tiles, err := e.Encode(img, false)
	if err != nil {
		t.Fatal(err)
	}
	if len(tiles) != 1 || tiles[0].W != 64*3 || tiles[0].Format != FormatJPEG {
		t.Fatalf("tiles %+v", tiles)
	}
	if _, err := jpeg.Decode(bytes.NewReader(tiles[0].Data)); err != nil {
		t.Fatal(err)
	}
}

func TestUpdatesSplitLargeFramesAndMarkTheLast(t *testing.T) {
	var tiles []Tile
	for i := 0; i < 5; i++ {
		tiles = append(tiles, Tile{X: i * 64, Y: 0, W: 64, H: 64, Format: FormatJPEG, Data: bytes.Repeat([]byte{byte(i)}, 300*1024)})
	}
	updates, err := Updates(42, tiles)
	if err != nil {
		t.Fatal(err)
	}
	if len(updates) < 2 {
		t.Fatalf("expected the frame to be split, got %d updates", len(updates))
	}
	var got []Tile
	for n, u := range updates {
		if len(u) > MaxFrameBytes {
			t.Fatalf("update %d has %d bytes", n, len(u))
		}
		frame, last, parsed, err := ParseUpdate(u)
		if err != nil || frame != 42 {
			t.Fatalf("update %d: frame %d err %v", n, frame, err)
		}
		if last != (n == len(updates)-1) {
			t.Fatalf("update %d last=%v", n, last)
		}
		got = append(got, parsed...)
	}
	if len(got) != 5 || got[4].X != 256 || !bytes.Equal(got[3].Data, tiles[3].Data) {
		t.Fatalf("parsed tiles do not match")
	}
	empty, _ := Updates(43, nil)
	if frame, last, parsed, err := ParseUpdate(empty[0]); len(empty) != 1 || frame != 43 || !last || len(parsed) != 0 || err != nil {
		t.Fatal("an empty frame must still be one last update")
	}
}

func TestHelperFramesRoundTripAndRejectBadLengths(t *testing.T) {
	var buf bytes.Buffer
	if err := WriteFrame(&buf, []byte{FrameNotice, '{', '}'}); err != nil {
		t.Fatal(err)
	}
	frame, err := ReadFrame(&buf)
	if err != nil || !bytes.Equal(frame, []byte{FrameNotice, '{', '}'}) {
		t.Fatalf("frame %v err %v", frame, err)
	}
	if err := WriteFrame(&buf, nil); err == nil {
		t.Fatal("an empty frame must be refused")
	}
	if _, err := ReadFrame(bytes.NewReader([]byte{0xFF, 0xFF, 0xFF, 0xFF})); err == nil {
		t.Fatal("an oversized length must be refused")
	}
}

// qwertz is a small German-like layout: y and z swapped, @ on AltGr+Q, no key for the euro sign here, é missing.
type qwertz struct{}

const (
	vkShift = 0x10
	vkQ     = 0x51
	vkY     = 0x59
	vkZ     = 0x5A
	vkC     = 0x43
	vk2     = 0x32
)

func (qwertz) KeyFor(r rune) (uint16, Modifier, bool) {
	switch r {
	case 'z':
		return vkZ, 0, true
	case 'y':
		return vkY, 0, true
	case 'Z':
		return vkZ, ModShift, true
	case 'c':
		return vkC, 0, true
	case '@':
		return vkQ, ModCtrl | ModAlt, true
	case '"':
		return vk2, ModShift, true
	}
	return 0, 0, false
}

func (qwertz) ScanFor(vk uint16) Scan {
	switch vk {
	case vkZ:
		return Scan{Code: 0x15}
	case vkY:
		return Scan{Code: 0x2C}
	case vkQ:
		return Scan{Code: 0x10}
	case vkC:
		return Scan{Code: 0x2E}
	case vk2:
		return Scan{Code: 0x03}
	}
	return Scan{}
}

func TestACharacterUsesTheKeyOfTheEndpointLayout(t *testing.T) {
	k := NewKeyboard()
	// The technician presses the physical Z key of a QWERTY board: the endpoint (QWERTZ) must get "z", which is its Y-position key.
	got := k.Key(KeyBody{Code: "KeyZ", Key: "z", Down: true}, qwertz{})
	want := []Input{{Scan: Scan{Code: 0x15}, VK: vkZ}, {Scan: Scan{Code: 0x15}, VK: vkZ, Up: true}}
	if !equalInputs(got, want) {
		t.Fatalf("got %+v want %+v", got, want)
	}
	if up := k.Key(KeyBody{Code: "KeyZ", Key: "z", Down: false}, qwertz{}); len(up) != 0 {
		t.Fatalf("the key up of a typed character must inject nothing, got %+v", up)
	}
}

func TestShiftIsAddedOrReleasedAsTheEndpointLayoutNeeds(t *testing.T) {
	k := NewKeyboard()
	shift := scanCodes["ShiftLeft"]
	k.Key(KeyBody{Code: "ShiftLeft", Key: "Shift", Down: true, Shift: true}, qwertz{})
	// With Shift held, "Z" needs Shift on the endpoint too: nothing is released or added.
	got := k.Key(KeyBody{Code: "KeyZ", Key: "Z", Down: true, Shift: true}, qwertz{})
	if !equalInputs(got, []Input{{Scan: Scan{Code: 0x15}, VK: vkZ}, {Scan: Scan{Code: 0x15}, VK: vkZ, Up: true}}) {
		t.Fatalf("Z with shift: %+v", got)
	}
	// "@" typed with Shift+2 on a US board: the endpoint needs AltGr (Ctrl+Alt) and no Shift.
	got = k.Key(KeyBody{Code: "Digit2", Key: "@", Down: true, Shift: true}, qwertz{})
	ctrl, alt := scanCodes["ControlLeft"], scanCodes["AltLeft"]
	want := []Input{
		{Scan: shift, Up: true}, {Scan: ctrl}, {Scan: alt},
		{Scan: Scan{Code: 0x10}, VK: vkQ}, {Scan: Scan{Code: 0x10}, VK: vkQ, Up: true},
		{Scan: alt, Up: true}, {Scan: ctrl, Up: true}, {Scan: shift},
	}
	if !equalInputs(got, want) {
		t.Fatalf("@: got %+v want %+v", got, want)
	}
}

func TestAltGrOfTheTechnicianIsReleasedAroundACharacterThatDoesNotNeedIt(t *testing.T) {
	k := NewKeyboard()
	// Chrome on Windows reports AltGr as ControlLeft then AltRight.
	k.Key(KeyBody{Code: "ControlLeft", Key: "Control", Down: true, Ctrl: true}, qwertz{})
	k.Key(KeyBody{Code: "AltRight", Key: "AltGraph", Down: true, Ctrl: true, Alt: true, AltGraph: true}, qwertz{})
	got := k.Key(KeyBody{Code: "Digit3", Key: "\"", Down: true, Ctrl: true, Alt: true, AltGraph: true}, qwertz{})
	ctrl, altRight, shift := scanCodes["ControlLeft"], scanCodes["AltRight"], scanCodes["ShiftLeft"]
	if len(got) != 8 || got[0] != (Input{Scan: ctrl, Up: true}) && got[0] != (Input{Scan: altRight, Up: true}) {
		t.Fatalf("got %+v", got)
	}
	var sawShift, sawKey bool
	for _, in := range got {
		sawShift = sawShift || in == Input{Scan: shift}
		sawKey = sawKey || in.VK == vk2
	}
	if !sawShift || !sawKey {
		t.Fatalf("the quote needs Shift+2 on the endpoint: %+v", got)
	}
	if k.Held() != 2 {
		t.Fatalf("the technician still holds AltGr: %d keys held", k.Held())
	}
}

func TestACharacterTheLayoutLacksIsTypedAsUnicode(t *testing.T) {
	k := NewKeyboard()
	got := k.Key(KeyBody{Code: "Digit2", Key: "é", Down: true}, qwertz{})
	if !equalInputs(got, []Input{{Unicode: 'é'}, {Unicode: 'é', Up: true}}) {
		t.Fatalf("got %+v", got)
	}
	if got := k.Key(KeyBody{Code: "BracketLeft", Key: "Dead", Down: true}, qwertz{}); len(got) != 0 {
		t.Fatalf("a dead key must inject nothing, got %+v", got)
	}
}

func TestShortcutsUseTheLetterAndNamedKeysThePhysicalKey(t *testing.T) {
	k := NewKeyboard()
	k.Key(KeyBody{Code: "ControlLeft", Key: "Control", Down: true, Ctrl: true}, qwertz{})
	got := k.Key(KeyBody{Code: "KeyC", Key: "c", Down: true, Ctrl: true}, qwertz{})
	if !equalInputs(got, []Input{{Scan: Scan{Code: 0x2E}, VK: vkC}, {Scan: Scan{Code: 0x2E}, VK: vkC, Up: true}}) {
		t.Fatalf("ctrl+c: %+v", got)
	}
	enter := k.Key(KeyBody{Code: "NumpadEnter", Key: "Enter", Down: true}, qwertz{})
	if !equalInputs(enter, []Input{{Scan: Scan{Code: 0x1C, Extended: true}}}) {
		t.Fatalf("numpad enter: %+v", enter)
	}
	if k.Held() != 2 {
		t.Fatalf("held %d, want Ctrl and Enter", k.Held())
	}
	released := k.ReleaseAll()
	if len(released) != 2 || k.Held() != 0 {
		t.Fatalf("release all: %+v", released)
	}
	for _, in := range released {
		if !in.Up {
			t.Fatalf("release all pressed a key: %+v", in)
		}
	}
}

func TestTypeTypesLinesAndReleasesHeldKeysFirst(t *testing.T) {
	k := NewKeyboard()
	k.Key(KeyBody{Code: "ControlLeft", Key: "Control", Down: true, Ctrl: true}, qwertz{})
	got := k.Type("z\r\ny", qwertz{})
	enter := scanCodes["Enter"]
	want := []Input{
		{Scan: scanCodes["ControlLeft"], Up: true},
		{Scan: Scan{Code: 0x15}, VK: vkZ}, {Scan: Scan{Code: 0x15}, VK: vkZ, Up: true},
		{Scan: enter}, {Scan: enter, Up: true},
		{Scan: Scan{Code: 0x2C}, VK: vkY}, {Scan: Scan{Code: 0x2C}, VK: vkY, Up: true},
	}
	if !equalInputs(got, want) {
		t.Fatalf("got %+v want %+v", got, want)
	}
}

func equalInputs(a, b []Input) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}
