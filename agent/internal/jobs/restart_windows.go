//go:build windows

package jobs

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strconv"
	"strings"
	"time"
)

func shutdownExe() string {
	systemRoot := os.Getenv("SystemRoot")
	if systemRoot == "" {
		systemRoot = `C:\Windows`
	}
	return filepath.Join(systemRoot, "System32", "shutdown.exe")
}

// osRequestRestart plans a restart in 60 seconds with the reason "planned, application maintenance".
func osRequestRestart(ctx context.Context) error {
	out, err := exec.CommandContext(ctx, shutdownExe(), "/r", "/t", "60", "/d", "p:4:1", "/c", restartNotice).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%w: %s", err, strings.TrimSpace(string(out)))
	}
	return nil
}

func osCancelRestart() error {
	return exec.Command(shutdownExe(), "/a").Run()
}

// osRequestRestartAfter plans a restart after the notice for updates, with the reason "planned, operating system hot fix". Windows
// shows the message with the time left to every signed-in user. The message is one argument of the vector, never a command line of
// its own.
func osRequestRestartAfter(ctx context.Context, delay time.Duration, message string) error {
	seconds := strconv.Itoa(int(max(delay, 0) / time.Second))
	out, err := exec.CommandContext(ctx, shutdownExe(), "/r", "/t", seconds, "/d", "p:2:17", "/c", message).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%w: %s", err, strings.TrimSpace(string(out)))
	}
	return nil
}
