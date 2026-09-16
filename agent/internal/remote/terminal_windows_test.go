//go:build windows

package remote

import (
	"strings"
	"testing"
	"time"
)

// TestThePipeTerminalRunsCommandsWithoutAPseudoConsole covers Windows Server 2016, which has no ConPTY.
func TestThePipeTerminalRunsCommandsWithoutAPseudoConsole(t *testing.T) {
	path, args, err := shellCommand("cmd")
	if err != nil {
		t.Fatal(err)
	}
	terminal, err := openPipes(path, args, "cmd")
	if err != nil {
		t.Fatal(err)
	}
	defer terminal.Close()
	if terminal.PTY() {
		t.Fatal("a pipe terminal must report that it has no pseudo terminal")
	}
	if _, err := terminal.Write([]byte("echo fleeto-pipe-test\rexit 5\r")); err != nil {
		t.Fatal(err)
	}
	var seen strings.Builder
	buffer := make([]byte, 4096)
	done := make(chan struct{})
	go func() {
		defer close(done)
		for {
			n, err := terminal.Read(buffer)
			seen.Write(buffer[:n])
			if err != nil {
				return
			}
		}
	}()
	select {
	case <-done:
	case <-time.After(30 * time.Second):
		t.Fatal("the pipe terminal did not end")
	}
	if !strings.Contains(seen.String(), "fleeto-pipe-test") {
		t.Fatalf("output missing: %q", seen.String())
	}
	if code, err := terminal.Wait(); err != nil || code != 5 {
		t.Fatalf("exit code %d, error %v", code, err)
	}
}
