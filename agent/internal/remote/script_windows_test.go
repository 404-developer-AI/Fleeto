//go:build windows

package remote

import (
	"crypto/sha256"
	"encoding/hex"
	"strings"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/jobs"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// TestAPowerShellScriptReadsInputThroughPipes covers the way a script runs as the signed-in user on Windows (and as SYSTEM on Server
// 2016): with redirected streams, where Read-Host still has to get the line the browser sends.
func TestAPowerShellScriptReadsInputThroughPipes(t *testing.T) {
	body := "$name = Read-Host 'Your name'\nWrite-Output \"hello $name\"\nexit 6\n"
	sum := sha256.Sum256([]byte(body))
	staged, err := jobs.StageInteractive(&agentv1.ScriptJob{Language: agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL, Name: "Greeting", Version: 1,
		Body: body, Sha256: hex.EncodeToString(sum[:])}, t.TempDir(), platform.AccessCurrentUser, false, "")
	if err != nil {
		t.Fatal(err)
	}
	defer staged.Release()
	path, cmdLine, err := staged.CommandLine()
	if err != nil {
		t.Fatal(err)
	}
	if strings.Contains(cmdLine, "-NonInteractive") {
		t.Fatalf("an interactive script must be allowed to ask for input: %s", cmdLine)
	}
	terminal, err := openPipesIn(path, cmdLine, staged.Dir)
	if err != nil {
		t.Fatal(err)
	}
	defer terminal.Close()
	if _, err := terminal.Write([]byte("fleeto\r")); err != nil {
		t.Fatal(err)
	}

	var seen strings.Builder
	done := make(chan struct{})
	go func() {
		defer close(done)
		buffer := make([]byte, 4096)
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
	case <-time.After(60 * time.Second):
		t.Fatal("the script did not end")
	}
	if !strings.Contains(seen.String(), "hello fleeto") {
		t.Fatalf("the answer did not reach Read-Host: %q", seen.String())
	}
	if code, err := terminal.Wait(); err != nil || code != 6 {
		t.Fatalf("exit code %d, error %v", code, err)
	}
}
