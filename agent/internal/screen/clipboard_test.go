package screen

import (
	"encoding/binary"
	"strings"
	"testing"
	"unicode/utf16"
)

func TestDropFilesHasTheHeaderWideNamesAndADoubleNul(t *testing.T) {
	data, err := dropFiles([]string{`C:\Stage\1\a.txt`, `C:\Stage\1\ü.pdf`})
	if err != nil {
		t.Fatal(err)
	}
	if binary.LittleEndian.Uint32(data[0:]) != 20 || binary.LittleEndian.Uint32(data[16:]) != 1 {
		t.Fatalf("header % x", data[:20])
	}
	units := make([]uint16, (len(data)-20)/2)
	for i := range units {
		units[i] = binary.LittleEndian.Uint16(data[20+2*i:])
	}
	if units[len(units)-1] != 0 || units[len(units)-2] != 0 {
		t.Fatal("the list does not end in a double NUL")
	}
	names := strings.Split(string(utf16.Decode(units[:len(units)-2])), "\x00")
	if len(names) != 2 || names[0] != `C:\Stage\1\a.txt` || names[1] != `C:\Stage\1\ü.pdf` {
		t.Fatalf("names %q", names)
	}
	if _, err := dropFiles(nil); err == nil {
		t.Fatal("an empty list was accepted")
	}
	if _, err := dropFiles([]string{"C:\a\x00b"}); err == nil {
		t.Fatal("a path with a NUL was accepted")
	}
}

func TestClipboardTextRoundTripsThroughUTF16(t *testing.T) {
	text := "Wachtwoord: Ünïcødé 🙂\r\nline two"
	data := utf16Text(text)
	units := make([]uint16, len(data)/2)
	for i := range units {
		units[i] = binary.LittleEndian.Uint16(data[2*i:])
	}
	if units[len(units)-1] != 0 {
		t.Fatal("no NUL terminator")
	}
	if got := textFromUTF16(units); got != text {
		t.Fatalf("got %q", got)
	}
	if got := textFromUTF16([]uint16{'a', 'b'}); got != "ab" {
		t.Fatalf("text without NUL: %q", got)
	}
}

func TestTheBannerAndConsentNameTheTechnicians(t *testing.T) {
	if got := bannerText([]string{"Anna", "Bert", "Carl"}); got != "Remote control session by Anna, Bert and Carl" {
		t.Fatalf("banner %q", got)
	}
	if bannerText(nil) != "" {
		t.Fatal("a banner without technicians")
	}
	if msg := ConsentMessage("Anna", 30); !strings.Contains(msg, "Anna wants to view and control") || !strings.Contains(msg, "30 seconds") {
		t.Fatalf("consent %q", msg)
	}
}
