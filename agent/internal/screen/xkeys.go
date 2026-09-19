package screen

// The keyboard of an X11 endpoint (0.3.0 step 6). The plan of keys.go is the same as on Windows: a character is typed with the key of the
// endpoint's layout that has it, physical keys (Enter, arrows, modifiers, shortcuts without a character on the layout) go by their place
// on the keyboard. On X11 a "virtual key" of the plan is an X keycode found in the keyboard mapping, and a physical key is the evdev code
// of its scan code plus 8, the keycode every current X server gives it. A character no key has is typed through a spare keycode that the
// injector maps to it for the moment. These functions touch no X server, so they are tested on every platform.

// ModAltGr is the third level of an X11 layout (ISO_Level3_Shift, the AltGr key); a Windows layout reports AltGr as Ctrl+Alt instead.
const ModAltGr Modifier = 1 << 3

// extendedEvdev maps the E0-prefixed scan codes to their Linux evdev codes; the others are equal to their set 1 scan code.
var extendedEvdev = map[uint16]uint8{
	0x1C: 96, 0x1D: 97, 0x35: 98, 0x37: 99, 0x38: 100, 0x47: 102, 0x48: 103, 0x49: 104, 0x4B: 105, 0x4D: 106, 0x4F: 107, 0x50: 108,
	0x51: 109, 0x52: 110, 0x53: 111, 0x5B: 125, 0x5C: 126, 0x5D: 127,
	// NumLock carries the extended flag in the scan code table (for SendInput); its evdev code is the plain one.
	0x45: 69,
}

// otherEvdev maps the scan codes above 0x58 that have their own evdev code.
var otherEvdev = map[uint16]uint8{0x73: 89, 0x7D: 124}

// xKeycode returns the X keycode of a physical key.
func xKeycode(s Scan) (uint8, bool) {
	var evdev uint8
	switch {
	case s.Extended:
		code, ok := extendedEvdev[s.Code]
		if !ok {
			return 0, false
		}
		evdev = code
	case s.Code >= 1 && s.Code <= 0x58:
		evdev = uint8(s.Code)
	default:
		code, ok := otherEvdev[s.Code]
		if !ok {
			return 0, false
		}
		evdev = code
	}
	return evdev + 8, true
}

const (
	keysymNoSymbol       = 0
	keysymISOLevel3Shift = 0xFE03
	keysymModeSwitch     = 0xFF7E
	keysymEuroSign       = 0x20AC
	unicodeKeysymBase    = 0x01000000
)

// keysymRune returns the character a keysym types, or 0 for a keysym without one (a function key, a dead key).
func keysymRune(ks uint32) rune {
	switch {
	case ks >= 0x20 && ks <= 0x7E, ks >= 0xA0 && ks <= 0xFF:
		return rune(ks)
	case ks >= unicodeKeysymBase+0x20 && ks <= unicodeKeysymBase+0x10FFFF:
		return rune(ks - unicodeKeysymBase)
	case ks == keysymEuroSign:
		return '€'
	}
	return 0
}

// runeKeysym returns the keysym for typing a character through a spare keycode.
func runeKeysym(r rune) uint32 {
	if (r >= 0x20 && r <= 0x7E) || (r >= 0xA0 && r <= 0xFF) {
		return uint32(r)
	}
	return unicodeKeysymBase + uint32(r)
}

// xLayout is the endpoint's keyboard as the X server maps it: for every keycode from min, perCode keysyms. The core mapping lists the
// first group at columns 0 and 1 (plain and Shift) and 4 and 5 (AltGr and AltGr with Shift), the second group at columns 2 and 3.
type xLayout struct {
	min     uint8
	perCode int
	syms    []uint32
	// group is the active XKB group (0 or 1): the second one is the second layout of a user who switches between two.
	group int
	// altGr says whether the AltGr key (AltRight) is ISO_Level3_Shift on this keyboard; otherwise third-level characters are typed as
	// characters without a key.
	altGr bool
}

// newXLayout builds the layout from GetKeyboardMapping.
func newXLayout(min uint8, perCode int, syms []uint32, group int) *xLayout {
	l := &xLayout{min: min, perCode: perCode, syms: syms, group: group}
	if code, ok := xKeycode(scanCodes["AltRight"]); ok {
		for _, ks := range l.keysyms(code) {
			if ks == keysymISOLevel3Shift || ks == keysymModeSwitch {
				l.altGr = true
			}
		}
	}
	return l
}

func (l *xLayout) keysyms(code uint8) []uint32 {
	if l.perCode <= 0 || code < l.min {
		return nil
	}
	start := int(code-l.min) * l.perCode
	if start+l.perCode > len(l.syms) {
		return nil
	}
	return l.syms[start : start+l.perCode]
}

// levels lists the columns of the active group with the modifiers each needs, the plain level first.
func (l *xLayout) levels() []struct {
	column int
	mods   Modifier
} {
	type level = struct {
		column int
		mods   Modifier
	}
	if l.group == 1 {
		return []level{{2, 0}, {3, ModShift}}
	}
	out := []level{{0, 0}, {1, ModShift}}
	if l.altGr {
		out = append(out, level{4, ModAltGr}, level{5, ModAltGr | ModShift})
	}
	return out
}

// KeyFor finds the keycode and modifiers that type r. A letter's lowercase and uppercase share a key: when a column repeats the plain
// keysym (as X does for "a" with no uppercase listed), the case is taken from the Shift level.
func (l *xLayout) KeyFor(r rune) (uint16, Modifier, bool) {
	count := len(l.syms) / max(l.perCode, 1)
	for _, level := range l.levels() {
		for i := 0; i < count; i++ {
			code := l.min + uint8(i)
			syms := l.keysyms(code)
			if level.column >= len(syms) {
				continue
			}
			ks := syms[level.column]
			if ks == keysymNoSymbol {
				continue
			}
			if keysymRune(ks) == r {
				return uint16(code), level.mods, true
			}
		}
	}
	return 0, 0, false
}

// ScanFor is not used on X11: a planned key carries its keycode as VK, which the injector uses as it is.
func (l *xLayout) ScanFor(uint16) Scan { return Scan{} }

// spareKeycodes lists the keycodes without any keysym, which the injector may map to a character for a moment.
func (l *xLayout) spareKeycodes() []uint8 {
	var out []uint8
	count := len(l.syms) / max(l.perCode, 1)
	for i := count - 1; i >= 0; i-- {
		code := l.min + uint8(i)
		if code < 9 {
			continue
		}
		empty := true
		for _, ks := range l.keysyms(code) {
			if ks != keysymNoSymbol {
				empty = false
				break
			}
		}
		if empty {
			out = append(out, code)
		}
	}
	return out
}
