//go:build windows

package screen

import (
	"strings"
	"testing"

	"golang.org/x/sys/windows"
)

// The environment block must never pass a string with an embedded NUL to the UTF-16 conversion (that panics); it ends each variable and
// the whole block with a NUL. This is the bug that made remote control flap on the first real endpoint.
func TestEnvironmentBlockIsDoubleNullTerminatedAndNeverPanics(t *testing.T) {
	block := environmentBlock([]string{`FLEETO_REMOTE_SESSION=1`, `SystemRoot=C:\Windows`, "BAD=has\x00nul", ""})
	if n := len(block); n < 2 || block[n-1] != 0 || block[n-2] != 0 {
		t.Fatalf("the block must end in two NULs, got %v", block[max(0, len(block)-3):])
	}
	// The bad variable (embedded NUL) is skipped, not passed to StringToUTF16.
	text := windows.UTF16ToString(block)
	if !strings.HasPrefix(text, "FLEETO_REMOTE_SESSION=1") || strings.Contains(text, "BAD=") {
		t.Fatalf("unexpected first entries: %q", text)
	}
}
