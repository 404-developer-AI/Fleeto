//go:build windows

package jobs

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
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
