package screen

import (
	"encoding/binary"
	"encoding/hex"
	"errors"
	"strconv"
	"strings"
)

// X11 authorization (0.3.0 step 6). The agent reads the MIT-MAGIC-COOKIE-1 of the display from the X server's own authority file (the
// "-auth" argument of the X server), or from the session's XAUTHORITY, and hands it to its helper over the pipe. These functions parse
// without touching the system, so they are tested on every platform.

const (
	xauthFamilyLocal = 256
	xauthFamilyWild  = 65535
	magicCookie      = "MIT-MAGIC-COOKIE-1"
)

// xauthEntry is one record of an X authority file.
type xauthEntry struct {
	family  uint16
	address string
	number  string
	name    string
	data    []byte
}

// parseXauthority reads the records of an X authority file.
func parseXauthority(file []byte) ([]xauthEntry, error) {
	var entries []xauthEntry
	rest := file
	field := func() ([]byte, error) {
		if len(rest) < 2 {
			return nil, errors.New("the X authority file is cut off")
		}
		n := int(binary.BigEndian.Uint16(rest))
		if len(rest) < 2+n {
			return nil, errors.New("the X authority file is cut off")
		}
		value := rest[2 : 2+n]
		rest = rest[2+n:]
		return value, nil
	}
	for len(rest) > 0 {
		if len(rest) < 2 {
			return nil, errors.New("the X authority file is cut off")
		}
		e := xauthEntry{family: binary.BigEndian.Uint16(rest)}
		rest = rest[2:]
		parts := make([][]byte, 4)
		for i := range parts {
			value, err := field()
			if err != nil {
				return nil, err
			}
			parts[i] = value
		}
		e.address, e.number, e.name, e.data = string(parts[0]), string(parts[1]), string(parts[2]), append([]byte(nil), parts[3]...)
		entries = append(entries, e)
		if len(entries) > 4096 {
			return nil, errors.New("the X authority file has too many records")
		}
	}
	return entries, nil
}

// cookieForDisplay returns the hex MIT-MAGIC-COOKIE-1 of a display (":0", ":1.0") from an X authority file: a record for that display
// number first, then a record for any display. hostname is this machine's name, which local records carry as their address.
func cookieForDisplay(file []byte, display, hostname string) (string, error) {
	entries, err := parseXauthority(file)
	if err != nil {
		return "", err
	}
	number, ok := displayNumber(display)
	if !ok {
		return "", errors.New("the display name is invalid")
	}
	want := strconv.Itoa(number)
	// Best first: this display on this host (or any host), this display under another host name (the machine was renamed), any display.
	var otherHost, anyDisplay []byte
	for _, e := range entries {
		if e.name != magicCookie || len(e.data) != 16 || (e.family != xauthFamilyLocal && e.family != xauthFamilyWild) {
			continue
		}
		switch {
		case e.number == want && (e.family == xauthFamilyWild || hostname == "" || e.address == hostname):
			return hex.EncodeToString(e.data), nil
		case e.number == want && otherHost == nil:
			otherHost = e.data
		case e.number == "" && anyDisplay == nil:
			anyDisplay = e.data
		}
	}
	if otherHost != nil {
		return hex.EncodeToString(otherHost), nil
	}
	if anyDisplay != nil {
		return hex.EncodeToString(anyDisplay), nil
	}
	return "", errors.New("the X authority file has no cookie for display " + display)
}

// displayNumber reads the number of a local display name: ":0" and ":1.0" give 0 and 1.
func displayNumber(display string) (int, bool) {
	_, after, found := strings.Cut(display, ":")
	if !found {
		return 0, false
	}
	after, _, _ = strings.Cut(after, ".")
	n, err := strconv.Atoi(after)
	if err != nil || n < 0 || n > 65535 {
		return 0, false
	}
	return n, true
}

// xServer describes an X server process from its command line.
type xServer struct {
	display string
	auth    string
	vt      int
	// wayland is set for Xwayland: the screen belongs to a Wayland compositor, and remote control cannot use it.
	wayland bool
	// pid and uid are the process and its owner, filled in from /proc; 0 when unknown.
	pid int
	uid uint32
}

// parseXServerCommand reads the display, the authority file and the virtual terminal from the command line of an X server (Xorg, X,
// Xorg.bin, Xwayland); ok is false for any other program.
func parseXServerCommand(args []string) (xServer, bool) {
	if len(args) == 0 {
		return xServer{}, false
	}
	base := args[0]
	if i := strings.LastIndexByte(base, '/'); i >= 0 {
		base = base[i+1:]
	}
	var s xServer
	switch base {
	case "Xorg", "X", "Xorg.bin", "Xorg.wrap":
	case "Xwayland":
		s.wayland = true
	default:
		return xServer{}, false
	}
	for i := 1; i < len(args); i++ {
		a := args[i]
		switch {
		case strings.HasPrefix(a, ":") && s.display == "":
			if _, ok := displayNumber(a); ok {
				s.display = a
			}
		case a == "-auth" && i+1 < len(args):
			s.auth = args[i+1]
			i++
		case strings.HasPrefix(a, "vt") && len(a) > 2:
			if n, err := strconv.Atoi(a[2:]); err == nil {
				s.vt = n
			}
		}
	}
	if s.display == "" {
		s.display = ":0"
	}
	return s, true
}

// loginSession is what loginctl says about a session.
type loginSession struct {
	id      string
	uid     uint32
	name    string
	kind    string // x11, wayland, tty, mir, unspecified
	class   string // user, greeter, lock-screen
	display string
	vt      int
	leader  int
	active  bool
}

// parseLoginSession reads the output of "loginctl show-session ID -p ...".
func parseLoginSession(id, out string) (loginSession, error) {
	values := map[string]string{}
	for _, line := range strings.Split(out, "\n") {
		if key, value, ok := strings.Cut(strings.TrimSpace(line), "="); ok {
			values[key] = value
		}
	}
	uid, err := strconv.ParseUint(values["User"], 10, 32)
	if err != nil {
		return loginSession{}, errors.New("the session has no user")
	}
	s := loginSession{id: id, uid: uint32(uid), name: values["Name"], kind: values["Type"], class: values["Class"], display: values["Display"],
		active: values["Active"] == "yes"}
	s.vt, _ = strconv.Atoi(values["VTNr"])
	s.leader, _ = strconv.Atoi(values["Leader"])
	return s, nil
}

// sessionNumber turns a logind session id into the number the hub uses to notice that the console moved to another session: "5" is 5,
// "c2" (a greeter of some display managers) is 0x80000002. 0 and 0xFFFFFFFF mean no session.
func sessionNumber(id string) uint32 {
	if n, err := strconv.ParseUint(id, 10, 31); err == nil && n > 0 {
		return uint32(n)
	}
	digits := strings.TrimLeftFunc(id, func(r rune) bool { return r < '0' || r > '9' })
	if n, err := strconv.ParseUint(digits, 10, 31); err == nil && digits != "" {
		return 0x80000000 | uint32(n)
	}
	if id == "" {
		return 0
	}
	// Any other id: a stable number from its bytes, never 0 or all ones.
	var h uint32 = 2166136261
	for i := 0; i < len(id); i++ {
		h = (h ^ uint32(id[i])) * 16777619
	}
	return h&0x7FFFFFFE | 1
}
