package screen

import (
	"encoding/binary"
	"reflect"
	"testing"
)

// xauthRecord builds one X authority record.
func xauthRecord(family uint16, address, number, name string, data []byte) []byte {
	out := binary.BigEndian.AppendUint16(nil, family)
	for _, field := range [][]byte{[]byte(address), []byte(number), []byte(name), data} {
		out = binary.BigEndian.AppendUint16(out, uint16(len(field)))
		out = append(out, field...)
	}
	return out
}

func cookie(b byte) []byte {
	c := make([]byte, 16)
	for i := range c {
		c[i] = b
	}
	return c
}

func TestTheCookieOfTheDisplayIsChosen(t *testing.T) {
	var file []byte
	file = append(file, xauthRecord(xauthFamilyLocal, "desk", "1", magicCookie, cookie(0x11))...)
	file = append(file, xauthRecord(xauthFamilyLocal, "oldname", "0", magicCookie, cookie(0x22))...)
	file = append(file, xauthRecord(xauthFamilyLocal, "desk", "0", "XDM-AUTHORIZATION-1", cookie(0x33))...)
	file = append(file, xauthRecord(xauthFamilyLocal, "desk", "0", magicCookie, cookie(0x44))...)
	file = append(file, xauthRecord(0, "10.0.0.1", "0", magicCookie, cookie(0x55))...)

	got, err := cookieForDisplay(file, ":0", "desk")
	if err != nil || got != "44444444444444444444444444444444" {
		t.Fatalf("display :0 on desk: %q %v", got, err)
	}
	// Renamed machine: the record of the display under the old name.
	if got, _ := cookieForDisplay(file, ":0.0", "renamed"); got != "22222222222222222222222222222222" {
		t.Fatalf("display :0 after a rename: %q", got)
	}
	if got, _ := cookieForDisplay(file, ":1", "desk"); got != "11111111111111111111111111111111" {
		t.Fatalf("display :1: %q", got)
	}
	if _, err := cookieForDisplay(file, ":7", "desk"); err == nil {
		t.Fatal("a display without a record got a cookie")
	}
	// A wildcard record (a display manager's server file) serves any host.
	wild := xauthRecord(xauthFamilyWild, "", "0", magicCookie, cookie(0x66))
	if got, _ := cookieForDisplay(wild, ":0", "desk"); got != "66666666666666666666666666666666" {
		t.Fatalf("wildcard: %q", got)
	}
	if _, err := cookieForDisplay(file[:len(file)-3], ":0", "desk"); err == nil {
		t.Fatal("a cut-off file was accepted")
	}
}

func TestXServerCommandLines(t *testing.T) {
	cases := []struct {
		args []string
		want xServer
		ok   bool
	}{
		{[]string{"/usr/lib/xorg/Xorg", "-core", ":0", "-seat", "seat0", "-auth", "/var/run/lightdm/root/:0", "-nolisten", "tcp", "vt7"},
			xServer{display: ":0", auth: "/var/run/lightdm/root/:0", vt: 7}, true},
		{[]string{"/usr/lib/xorg/Xorg", "vt2", "-displayfd", "3", "-auth", "/run/user/1000/gdm/Xauthority", "-nolisten", "tcp"},
			xServer{display: ":0", auth: "/run/user/1000/gdm/Xauthority", vt: 2}, true},
		{[]string{"/usr/bin/Xwayland", ":1", "-rootless", "-auth", "/run/user/1000/.mutter-Xwaylandauth"},
			xServer{display: ":1", auth: "/run/user/1000/.mutter-Xwaylandauth", wayland: true}, true},
		{[]string{"/usr/bin/bash", ":0"}, xServer{}, false},
	}
	for _, c := range cases {
		got, ok := parseXServerCommand(c.args)
		if ok != c.ok || got != c.want {
			t.Errorf("%v: got %+v %v, want %+v %v", c.args, got, ok, c.want, c.ok)
		}
	}
}

func TestLoginSessionsAndTheirNumbers(t *testing.T) {
	s, err := parseLoginSession("3", "Type=x11\nClass=user\nUser=1000\nName=anna\nDisplay=:0\nVTNr=2\nLeader=1234\nActive=yes\n")
	if err != nil || s.kind != "x11" || s.uid != 1000 || s.name != "anna" || s.display != ":0" || s.vt != 2 || s.leader != 1234 || !s.active {
		t.Fatalf("session %+v %v", s, err)
	}
	if _, err := parseLoginSession("4", "Type=tty\n"); err == nil {
		t.Fatal("a session without a user was accepted")
	}
	if sessionNumber("5") != 5 || sessionNumber("c2") != 0x80000002 || sessionNumber("") != 0 {
		t.Fatal("sessionNumber")
	}
	if n := sessionNumber("greeter"); n == 0 || n == 0xFFFFFFFF || n != sessionNumber("greeter") {
		t.Fatalf("sessionNumber(greeter) = %x", n)
	}
}

func TestPhysicalKeysGetTheirEvdevKeycodes(t *testing.T) {
	for code, want := range map[string]uint8{"KeyA": 38, "Escape": 9, "Enter": 36, "ArrowLeft": 113, "AltRight": 108, "ControlRight": 105,
		"MetaLeft": 133, "NumLock": 77, "F12": 96, "IntlBackslash": 94, "Delete": 119, "NumpadEnter": 104} {
		got, ok := xKeycode(scanCodes[code])
		if !ok || got != want {
			t.Errorf("%s: keycode %d %v, want %d", code, got, ok, want)
		}
	}
	for code := range scanCodes {
		if _, ok := xKeycode(scanCodes[code]); !ok {
			t.Errorf("%s has no X keycode", code)
		}
	}
}

// testXLayout is a small Belgian (AZERTY) mapping: keycode 24 is a/A, 10 is &/1 with | on AltGr, 26 is e/E with € on AltGr, 108 is AltGr.
func testXLayout(group int) *xLayout {
	const per = 6
	syms := make([]uint32, (120-8)*per)
	set := func(code int, cols ...uint32) {
		copy(syms[(code-8)*per:], cols)
	}
	set(24, 'a', 'A', 'q', 'Q', 'a', 'A')
	set(10, '&', '1', '1', '!', '|', 0xA1)
	set(26, 'e', 'E', 'e', 'E', keysymEuroSign, 'E')
	set(38, 'q', 'Q', 'a', 'A', '@', 0)
	set(108, keysymISOLevel3Shift, 0, 0, 0, 0, 0)
	return newXLayout(8, per, syms, group)
}

func TestAnX11LayoutFindsTheKeyAndLevelOfACharacter(t *testing.T) {
	l := testXLayout(0)
	for r, want := range map[rune]struct {
		code uint16
		mods Modifier
	}{
		'a': {24, 0}, 'A': {24, ModShift}, '1': {10, ModShift}, '|': {10, ModAltGr}, '€': {26, ModAltGr}, '@': {38, ModAltGr}, '¡': {10, ModAltGr | ModShift},
	} {
		code, mods, ok := l.KeyFor(r)
		if !ok || code != want.code || mods != want.mods {
			t.Errorf("%q: keycode %d mods %v ok %v, want %+v", r, code, mods, ok, want)
		}
	}
	if _, _, ok := l.KeyFor('ß'); ok {
		t.Error("a character the layout lacks was found")
	}
	// The second group (a second layout the user switched to).
	if code, mods, ok := testXLayout(1).KeyFor('a'); !ok || code != 38 || mods != 0 {
		t.Errorf("second group 'a': %d %v %v", code, mods, ok)
	}
	// Without ISO_Level3_Shift on AltGr the third level is not used.
	noAltGr := testXLayout(0)
	noAltGr.altGr = false
	if _, _, ok := noAltGr.KeyFor('€'); ok {
		t.Error("a third-level character was found without an AltGr key")
	}
	spares := l.spareKeycodes()
	if len(spares) == 0 || spares[0] != 119 {
		t.Fatalf("spare keycodes %v", spares)
	}
}

func TestTheKeyPlanTypesAnAltGrCharacterOnX11(t *testing.T) {
	k := NewKeyboard()
	l := testXLayout(0)
	inputs := k.Key(KeyBody{Code: "Digit1", Key: "|", Down: true, AltGraph: true}, l)
	altGr := modifierScans[ModAltGr]
	want := []Input{{Scan: altGr}, {VK: 10}, {VK: 10, Up: true}, {Scan: altGr, Up: true}}
	if !reflect.DeepEqual(inputs, want) {
		t.Fatalf("plan %+v, want %+v", inputs, want)
	}
	// A character no key has is typed by itself.
	if got := k.Key(KeyBody{Code: "KeyS", Key: "ß", Down: true}, l); len(got) != 2 || got[0].Unicode != 'ß' {
		t.Fatalf("plan for ß: %+v", got)
	}
	if runeKeysym('ß') != 0xDF || runeKeysym('ł') != unicodeKeysymBase+'ł' || keysymRune(unicodeKeysymBase+'ł') != 'ł' || keysymRune(0xFF0D) != 0 {
		t.Fatal("keysym conversion")
	}
}

func TestFilePathsOnAnX11Clipboard(t *testing.T) {
	paths := []string{"/home/anna/Report 2026.pdf", "/tmp/a#b%c"}
	list := uriList(paths)
	if list != "file:///home/anna/Report%202026.pdf\r\nfile:///tmp/a%23b%25c\r\n" {
		t.Fatalf("uri-list %q", list)
	}
	if got := pathsFromURIList(list); !reflect.DeepEqual(got, paths) {
		t.Fatalf("paths from the uri-list %v", got)
	}
	gnome := gnomeCopiedFiles(paths)
	if got := pathsFromURIList(gnome); !reflect.DeepEqual(got, paths) || gnome[:5] != "copy\n" {
		t.Fatalf("gnome-copied-files %q gives %v", gnome, got)
	}
	mixed := "# comment\r\nhttps://example.com/x\r\nfile://otherhost/etc/passwd\r\nfile://localhost/srv/a/../b.txt\r\nrelative\r\n"
	if got := pathsFromURIList(mixed); !reflect.DeepEqual(got, []string{"/srv/b.txt"}) {
		t.Fatalf("only local files count: %v", got)
	}
}

func TestTheConsentTextIsWrapped(t *testing.T) {
	lines := wrapText(ConsentMessage("Anna Admin", 30), 40)
	for _, l := range lines {
		if len([]rune(l)) > 40 {
			t.Fatalf("line %q is longer than 40 characters", l)
		}
	}
	if lines[0] != "Anna Admin wants to view and control" || lines[3] != "" || lines[4] != "Allow the session?" {
		t.Fatalf("lines %q", lines)
	}
}
