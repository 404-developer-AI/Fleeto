//go:build linux

package distupdates

import (
	"bytes"
	"context"
	"errors"
	"os"
	"os/exec"
	"time"
)

// commandTimeout bounds one systemctl call.
const commandTimeout = time.Minute

// Apply brings the automatic updates of the distribution in line with the configuration and returns the state to report and whether
// it changed anything. stateDir is the state directory of the agent. It is cheap and idempotent, so the agent calls it with every
// inventory.
func Apply(stateDir string, want bool) (string, bool, error) {
	return System{StateDir: stateDir, Runner: execRunner{}}.Apply(context.Background(), want)
}

// Restore undoes what the agent changed, for the uninstall of the agent; it runs before the state directory is deleted.
func Restore(stateDir string) error {
	return System{StateDir: stateDir, Runner: execRunner{}}.Restore(context.Background())
}

// execRunner runs a command with an argument vector, never through a shell.
type execRunner struct{}

func (execRunner) Run(ctx context.Context, name string, args ...string) (string, int, error) {
	path, err := exec.LookPath(name)
	if err != nil {
		return "", -1, err
	}
	ctx, cancel := context.WithTimeout(ctx, commandTimeout)
	defer cancel()
	cmd := exec.CommandContext(ctx, path, args...) // #nosec G204 -- systemctl with fixed verbs and unit names.
	cmd.Env = append(os.Environ(), "LC_ALL=C", "SYSTEMD_PAGER=", "SYSTEMD_COLORS=0")
	var stdout bytes.Buffer
	cmd.Stdout = &stdout
	err = cmd.Run()
	var exit *exec.ExitError
	switch {
	case err == nil:
		return stdout.String(), 0, nil
	case ctx.Err() != nil:
		return stdout.String(), -1, ctx.Err()
	case errors.As(err, &exit):
		return stdout.String(), exit.ExitCode(), nil
	}
	return stdout.String(), -1, err
}
