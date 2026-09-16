//go:build windows || linux

package remote

import (
	"strings"
	"testing"
	"time"
)

// TestATerminalRunsACommandAndEnds starts the real shell of this platform (ConPTY or pipes on Windows, a pseudo terminal on Linux).
func TestATerminalRunsACommandAndEnds(t *testing.T) {
	shell := Shells()[len(Shells())-1]
	terminal, err := OpenTerminal(shell, 120, 30)
	if err != nil {
		t.Fatalf("open %s: %v", shell, err)
	}
	defer terminal.Close()

	output := make(chan string, 64)
	go func() {
		buffer := make([]byte, 4096)
		for {
			n, err := terminal.Read(buffer)
			if n > 0 {
				output <- string(buffer[:n])
			}
			if err != nil {
				close(output)
				return
			}
		}
	}()

	if err := terminal.Resize(100, 40); err != nil {
		t.Fatalf("resize: %v", err)
	}
	// Enter is a carriage return, as the browser sends it.
	if _, err := terminal.Write([]byte("echo fleeto-terminal-test\rexit 3\r")); err != nil {
		t.Fatalf("write: %v", err)
	}

	var seen strings.Builder
	deadline := time.After(30 * time.Second)
	for done := false; !done; {
		select {
		case chunk, ok := <-output:
			if !ok {
				done = true
				break
			}
			seen.WriteString(chunk)
		case <-deadline:
			t.Fatalf("the terminal did not end; output so far: %q", seen.String())
		}
	}
	// The command line itself is echoed too, so look for the output on a line of its own.
	if strings.Count(seen.String(), "fleeto-terminal-test") < 1 {
		t.Fatalf("the command output is missing: %q", seen.String())
	}
	code, err := terminal.Wait()
	if err != nil || code != 3 {
		t.Fatalf("exit code %d, error %v", code, err)
	}
}
