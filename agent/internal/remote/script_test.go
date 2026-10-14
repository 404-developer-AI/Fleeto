//go:build windows || linux

package remote

import (
	"crypto/sha256"
	"encoding/hex"
	"os"
	"runtime"
	"strings"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

// TestALibraryScriptAsksForInputAndEnds runs a real script that reads a line (set /p, read) the way a technician answers it in the
// terminal, and checks that its output arrives, its exit code is reported and the staged script is removed afterwards.
func TestALibraryScriptAsksForInputAndEnds(t *testing.T) {
	request := ScriptRequest{Name: "Greeting", Version: 1, RunAs: RunAsSystem}
	if runtime.GOOS == "windows" {
		request.Language = "batch"
		request.Body = "@echo off\r\nset /p name=Your name? \r\necho hello %name%\r\nexit /b 4\r\n"
	} else {
		request.Language = "shell"
		request.Body = "printf 'Your name? '\nread name\necho \"hello $name\"\nexit 4\n"
	}
	sum := sha256.Sum256([]byte(request.Body))
	request.Sha256 = hex.EncodeToString(sum[:])
	dir := t.TempDir()

	terminal, account, err := OpenScript(dir, platform.AccessCurrentUser)(request, 120, 30)
	if err != nil {
		t.Fatalf("open the script: %v", err)
	}
	defer terminal.Close()
	if account != "" {
		t.Fatalf("a script as the system has no account, got %q", account)
	}

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

	var seen strings.Builder
	answered := false
	deadline := time.After(30 * time.Second)
	for done := false; !done; {
		select {
		case chunk, ok := <-output:
			if !ok {
				done = true
				break
			}
			seen.WriteString(chunk)
			if !answered && strings.Contains(seen.String(), "Your name?") {
				answered = true
				// Enter is a carriage return, as the browser sends it.
				if _, err := terminal.Write([]byte("fleeto\r")); err != nil {
					t.Fatalf("write: %v", err)
				}
			}
		case <-deadline:
			t.Fatalf("the script did not end; output so far %q", seen.String())
		}
	}
	if !strings.Contains(seen.String(), "hello fleeto") {
		t.Fatalf("the answer did not reach the script: %q", seen.String())
	}
	if code, err := terminal.Wait(); err != nil || code != 4 {
		t.Fatalf("exit code %d, error %v", code, err)
	}

	// The staged script is removed once it ended.
	for wait := time.Now().Add(10 * time.Second); ; {
		entries, _ := os.ReadDir(dir)
		if len(entries) == 0 {
			break
		}
		if time.Now().After(wait) {
			t.Fatalf("the staged script was left behind: %v", entries)
		}
		time.Sleep(50 * time.Millisecond)
	}
}

func TestAScriptThatDoesNotMatchItsHashDoesNotRun(t *testing.T) {
	request := ScriptRequest{Name: "Changed", Version: 1, RunAs: RunAsSystem, Language: "shell", Body: "echo hi", Sha256: strings.Repeat("0", 64)}
	if runtime.GOOS == "windows" {
		request.Language = "batch"
	}
	dir := t.TempDir()
	if _, _, err := OpenScript(dir, platform.AccessCurrentUser)(request, 80, 24); err == nil || !strings.Contains(err.Error(), "hash") {
		t.Fatalf("a script that does not match its hash must be refused, got %v", err)
	}
	if entries, _ := os.ReadDir(dir); len(entries) != 0 {
		t.Fatalf("nothing may be staged for a refused script: %v", entries)
	}
}
