package screen

import (
	"unicode/utf8"
)

// Keyboard (decided 2026-09-16): characters must never change on the way, also when the technician's layout differs from the endpoint's
// (AZERTY against QWERTY) and on the Windows sign-in screen. So a key that produces a character is sent as that character, translated to
// a key of the endpoint's active layout (with the Shift, Ctrl+Alt or AltGr it needs there), and typed as a Unicode character when that
// layout has no key for it. Keys without a character (Enter, arrows, F-keys, modifiers) and shortcuts with Ctrl or Alt go as the physical
// key. This file decides what to inject; inject_windows.go does it.

// Scan is a PC/AT set 1 scan code with its extended (E0) flag.
type Scan struct {
	Code     uint16
	Extended bool
}

// scanCodes maps KeyboardEvent.code (the physical key, independent of layout) to its scan code.
var scanCodes = map[string]Scan{
	"Escape": {0x01, false}, "Digit1": {0x02, false}, "Digit2": {0x03, false}, "Digit3": {0x04, false}, "Digit4": {0x05, false},
	"Digit5": {0x06, false}, "Digit6": {0x07, false}, "Digit7": {0x08, false}, "Digit8": {0x09, false}, "Digit9": {0x0A, false},
	"Digit0": {0x0B, false}, "Minus": {0x0C, false}, "Equal": {0x0D, false}, "Backspace": {0x0E, false}, "Tab": {0x0F, false},
	"KeyQ": {0x10, false}, "KeyW": {0x11, false}, "KeyE": {0x12, false}, "KeyR": {0x13, false}, "KeyT": {0x14, false},
	"KeyY": {0x15, false}, "KeyU": {0x16, false}, "KeyI": {0x17, false}, "KeyO": {0x18, false}, "KeyP": {0x19, false},
	"BracketLeft": {0x1A, false}, "BracketRight": {0x1B, false}, "Enter": {0x1C, false}, "ControlLeft": {0x1D, false},
	"KeyA": {0x1E, false}, "KeyS": {0x1F, false}, "KeyD": {0x20, false}, "KeyF": {0x21, false}, "KeyG": {0x22, false},
	"KeyH": {0x23, false}, "KeyJ": {0x24, false}, "KeyK": {0x25, false}, "KeyL": {0x26, false}, "Semicolon": {0x27, false},
	"Quote": {0x28, false}, "Backquote": {0x29, false}, "ShiftLeft": {0x2A, false}, "Backslash": {0x2B, false},
	"KeyZ": {0x2C, false}, "KeyX": {0x2D, false}, "KeyC": {0x2E, false}, "KeyV": {0x2F, false}, "KeyB": {0x30, false},
	"KeyN": {0x31, false}, "KeyM": {0x32, false}, "Comma": {0x33, false}, "Period": {0x34, false}, "Slash": {0x35, false},
	"ShiftRight": {0x36, false}, "NumpadMultiply": {0x37, false}, "AltLeft": {0x38, false}, "Space": {0x39, false},
	"CapsLock": {0x3A, false}, "F1": {0x3B, false}, "F2": {0x3C, false}, "F3": {0x3D, false}, "F4": {0x3E, false},
	"F5": {0x3F, false}, "F6": {0x40, false}, "F7": {0x41, false}, "F8": {0x42, false}, "F9": {0x43, false}, "F10": {0x44, false},
	"NumLock": {0x45, true}, "ScrollLock": {0x46, false}, "Numpad7": {0x47, false}, "Numpad8": {0x48, false},
	"Numpad9": {0x49, false}, "NumpadSubtract": {0x4A, false}, "Numpad4": {0x4B, false}, "Numpad5": {0x4C, false},
	"Numpad6": {0x4D, false}, "NumpadAdd": {0x4E, false}, "Numpad1": {0x4F, false}, "Numpad2": {0x50, false},
	"Numpad3": {0x51, false}, "Numpad0": {0x52, false}, "NumpadDecimal": {0x53, false}, "IntlBackslash": {0x56, false},
	"F11": {0x57, false}, "F12": {0x58, false}, "IntlRo": {0x73, false}, "IntlYen": {0x7D, false},
	"NumpadEnter": {0x1C, true}, "ControlRight": {0x1D, true}, "NumpadDivide": {0x35, true}, "PrintScreen": {0x37, true},
	"AltRight": {0x38, true}, "Home": {0x47, true}, "ArrowUp": {0x48, true}, "PageUp": {0x49, true}, "ArrowLeft": {0x4B, true},
	"ArrowRight": {0x4D, true}, "End": {0x4F, true}, "ArrowDown": {0x50, true}, "PageDown": {0x51, true}, "Insert": {0x52, true},
	"Delete": {0x53, true}, "MetaLeft": {0x5B, true}, "MetaRight": {0x5C, true}, "ContextMenu": {0x5D, true},
}

// ScanCode returns the scan code of a physical key.
func ScanCode(code string) (Scan, bool) {
	s, ok := scanCodes[code]
	return s, ok
}

// Modifier identifies a modifier key the injector holds.
type Modifier int

const (
	ModShift Modifier = 1 << iota
	ModCtrl
	ModAlt
)

// modifierScans are the keys injected when a character needs a modifier the technician does not hold.
var modifierScans = map[Modifier]Scan{
	ModShift: scanCodes["ShiftLeft"],
	ModCtrl:  scanCodes["ControlLeft"],
	ModAlt:   scanCodes["AltLeft"],
	// AltGr on an X11 layout (xkeys.go); a Windows layout never asks for it.
	ModAltGr: scanCodes["AltRight"],
}

// Layout is the endpoint's active keyboard layout.
type Layout interface {
	// KeyFor returns the virtual key and the modifiers that type r, and false when the layout has no key for it.
	KeyFor(r rune) (vk uint16, mods Modifier, ok bool)
	// ScanFor returns the scan code of a virtual key in the layout.
	ScanFor(vk uint16) Scan
}

// Input is one injected keyboard event.
type Input struct {
	Scan Scan
	// VK is set for keys typed by virtual key; zero for a pure scan code.
	VK uint16
	// Unicode is set for a character typed without a key.
	Unicode rune
	Up      bool
}

// Keyboard turns browser key events into injected keyboard events and remembers what it holds down, so it can release everything.
type Keyboard struct {
	held map[Scan]bool
}

// NewKeyboard returns a keyboard that holds nothing.
func NewKeyboard() *Keyboard { return &Keyboard{held: map[Scan]bool{}} }

var modifierCodes = map[string]Modifier{
	"ShiftLeft": ModShift, "ShiftRight": ModShift, "ControlLeft": ModCtrl, "ControlRight": ModCtrl, "AltLeft": ModAlt, "AltRight": ModAlt,
}

// heldModifiers is what the injected keys hold right now.
func (k *Keyboard) heldModifiers() (mods Modifier, scans map[Modifier][]Scan) {
	scans = map[Modifier][]Scan{}
	for code, mod := range modifierCodes {
		s := scanCodes[code]
		if k.held[s] {
			mods |= mod
			scans[mod] = append(scans[mod], s)
		}
	}
	return mods, scans
}

// Key plans the injection of one browser key event.
func (k *Keyboard) Key(ev KeyBody, layout Layout) []Input {
	scan, known := scanCodes[ev.Code]
	if _, isModifier := modifierCodes[ev.Code]; isModifier || ev.Code == "MetaLeft" || ev.Code == "MetaRight" || ev.Code == "CapsLock" ||
		ev.Code == "NumLock" || ev.Code == "ScrollLock" {
		if !known {
			return nil
		}
		return k.physical(scan, ev.Down)
	}
	if ev.Key == "Dead" {
		// A dead key composes with the next key in the technician's browser, which then reports the composed character.
		return nil
	}
	if r, size := utf8.DecodeRuneInString(ev.Key); size == len(ev.Key) && r != utf8.RuneError && r >= 0x20 {
		if !ev.Down {
			return nil // a character is typed whole on key down
		}
		if (ev.Ctrl || ev.Alt || ev.Meta) && !ev.AltGraph {
			return k.shortcut(r, scan, known, layout)
		}
		return k.character(r, layout)
	}
	if !known {
		return nil
	}
	return k.physical(scan, ev.Down)
}

// physical presses or releases one key by its scan code.
func (k *Keyboard) physical(scan Scan, down bool) []Input {
	if down {
		k.held[scan] = true
		return []Input{{Scan: scan}}
	}
	if !k.held[scan] {
		// Released without a press we injected (it went down before the window had focus): still release it on the endpoint.
		return []Input{{Scan: scan, Up: true}}
	}
	delete(k.held, scan)
	return []Input{{Scan: scan, Up: true}}
}

// shortcut types a key with the technician's Ctrl, Alt or Win held: the letter as the endpoint's layout has it (Ctrl+C stays Ctrl+C
// whatever the layouts), else the physical key.
func (k *Keyboard) shortcut(r rune, scan Scan, known bool, layout Layout) []Input {
	lower := r
	if 'A' <= r && r <= 'Z' {
		lower = r + ('a' - 'A')
	}
	if vk, _, ok := layout.KeyFor(lower); ok {
		s := layout.ScanFor(vk)
		return []Input{{Scan: s, VK: vk}, {Scan: s, VK: vk, Up: true}}
	}
	if !known {
		return nil
	}
	return []Input{{Scan: scan}, {Scan: scan, Up: true}}
}

// character types one character: its key in the endpoint's layout with exactly the modifiers it needs, the technician's own modifiers
// released around it, or a Unicode character when the layout has no key for it.
func (k *Keyboard) character(r rune, layout Layout) []Input {
	held, heldScans := k.heldModifiers()
	vk, need, ok := layout.KeyFor(r)
	if !ok {
		need = 0
	}
	var out []Input
	// Release the technician's modifiers that the character does not need (AltGr arrives as Ctrl+Alt from the browser), and press what
	// it needs that is not held.
	for _, mod := range []Modifier{ModShift, ModCtrl, ModAlt} {
		if held&mod != 0 && need&mod == 0 {
			for _, s := range heldScans[mod] {
				out = append(out, Input{Scan: s, Up: true})
			}
		}
	}
	for _, mod := range []Modifier{ModCtrl, ModAlt, ModAltGr, ModShift} {
		if need&mod != 0 && held&mod == 0 {
			out = append(out, Input{Scan: modifierScans[mod]})
		}
	}
	if ok {
		s := layout.ScanFor(vk)
		out = append(out, Input{Scan: s, VK: vk}, Input{Scan: s, VK: vk, Up: true})
	} else {
		out = append(out, Input{Unicode: r}, Input{Unicode: r, Up: true})
	}
	for _, mod := range []Modifier{ModShift, ModAltGr, ModAlt, ModCtrl} {
		if need&mod != 0 && held&mod == 0 {
			out = append(out, Input{Scan: modifierScans[mod], Up: true})
		}
	}
	for _, mod := range []Modifier{ModCtrl, ModAlt, ModShift} {
		if held&mod != 0 && need&mod == 0 {
			for _, s := range heldScans[mod] {
				out = append(out, Input{Scan: s})
			}
		}
	}
	return out
}

// Type plans typing a text ("Type clipboard"): every character as character(), a line break as Enter and a tab as Tab. Everything the
// technician holds is released first.
func (k *Keyboard) Type(text string, layout Layout) []Input {
	out := k.ReleaseAll()
	count := 0
	for _, r := range text {
		if count >= MaxTypeRunes {
			break
		}
		count++
		switch r {
		case '\r':
			continue
		case '\n':
			s := scanCodes["Enter"]
			out = append(out, Input{Scan: s}, Input{Scan: s, Up: true})
		case '\t':
			s := scanCodes["Tab"]
			out = append(out, Input{Scan: s}, Input{Scan: s, Up: true})
		default:
			if r < 0x20 {
				continue
			}
			out = append(out, k.character(r, layout)...)
		}
	}
	return out
}

// ReleaseAll releases every key the injector holds (focus lost, session ended), so nothing stays stuck on the endpoint.
func (k *Keyboard) ReleaseAll() []Input {
	var out []Input
	for scan := range k.held {
		out = append(out, Input{Scan: scan, Up: true})
	}
	k.held = map[Scan]bool{}
	return out
}

// Held reports how many keys the injector holds (for tests).
func (k *Keyboard) Held() int { return len(k.held) }
