//go:build linux

package jobs

import (
	"context"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"strconv"
	"strings"
	"time"
)

// shutdownPath is the shutdown command of the system, by absolute path only.
func shutdownPath() (string, error) {
	for _, path := range []string{"/usr/sbin/shutdown", "/sbin/shutdown", "/usr/bin/shutdown"} {
		if info, err := os.Stat(path); err == nil && !info.IsDir() {
			return path, nil
		}
	}
	return "", errors.New("the shutdown command was not found")
}

// osRequestRestart plans a restart in one minute.
func osRequestRestart(ctx context.Context) error {
	path, err := shutdownPath()
	if err != nil {
		return err
	}
	out, err := exec.CommandContext(ctx, path, "-r", "+1", restartNotice).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%w: %s", err, strings.TrimSpace(string(out)))
	}
	return nil
}

func osCancelRestart() error {
	path, err := shutdownPath()
	if err != nil {
		return err
	}
	return exec.Command(path, "-c").Run()
}

// osRequestRestartAfter plans a restart after the notice for updates, in whole minutes rounded up; shutdown sends the message to the
// terminals of signed-in users (wall) and repeats it as the time runs out. The message never starts with a dash.
func osRequestRestartAfter(ctx context.Context, delay time.Duration, message string) error {
	path, err := shutdownPath()
	if err != nil {
		return err
	}
	minutes := noticeMinutes(uint32(min(max(delay, 0)/time.Second, time.Duration(MaxNoticeSeconds))))
	out, err := exec.CommandContext(ctx, path, "-r", "+"+strconv.FormatUint(uint64(minutes), 10), message).CombinedOutput()
	if err != nil {
		return fmt.Errorf("%w: %s", err, strings.TrimSpace(string(out)))
	}
	return nil
}
