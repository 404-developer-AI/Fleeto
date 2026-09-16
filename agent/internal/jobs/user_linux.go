//go:build linux

package jobs

import (
	"context"
	"fmt"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"strconv"
	"strings"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// signedInUser is the session a job runs in when it must run as the signed-in user: the active session of systemd-logind,
// seated (a local screen) or remote (SSH is not counted; a graphical remote session is). Root is never chosen.
type signedInUser struct {
	name        string
	uid         uint32
	gid         uint32
	home        string
	sessionID   string
	display     string
	sessionType string
}

// stageDirRoot holds the scripts of jobs that run as a user. It is in /tmp because the agent's own state directory is
// readable by root only, and a user must be able to read the script the interpreter opens.
const stageDirRoot = "/tmp"

// signedInSession finds the session to run in. loginctl is part of systemd, which the agent already requires.
func signedInSession() (*signedInUser, error) {
	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
	defer cancel()

	tool, err := exec.LookPath("loginctl")
	if err != nil {
		return nil, ErrNoUserSignedIn
	}
	list, err := commandOutput(ctx, tool, "list-sessions", "--no-legend")
	if err != nil {
		return nil, ErrNoUserSignedIn
	}

	var fallback *signedInUser
	for line := range strings.SplitSeq(list, "\n") {
		// SESSION UID USER SEAT TTY
		fields := strings.Fields(line)
		if len(fields) < 3 {
			continue
		}
		found, err := describeSession(ctx, tool, fields[0])
		if err != nil || found == nil {
			continue
		}
		if found.display != "" || found.sessionType == "x11" || found.sessionType == "wayland" {
			return found, nil
		}
		if fallback == nil {
			fallback = found
		}
	}
	if fallback != nil {
		return fallback, nil
	}
	return nil, ErrNoUserSignedIn
}

// describeSession returns the session when it is active and belongs to a real user, and nil otherwise.
func describeSession(ctx context.Context, tool, id string) (*signedInUser, error) {
	out, err := commandOutput(ctx, tool, "show-session", id, "--property=Active", "--property=Name", "--property=User",
		"--property=Display", "--property=Type", "--property=State")
	if err != nil {
		return nil, err
	}
	values := map[string]string{}
	for line := range strings.SplitSeq(out, "\n") {
		if key, value, ok := strings.Cut(strings.TrimSpace(line), "="); ok {
			values[key] = value
		}
	}
	if values["Active"] != "yes" || values["State"] != "active" {
		return nil, nil
	}
	uid, err := strconv.ParseUint(values["User"], 10, 32)
	if err != nil || uid == 0 {
		return nil, nil
	}
	account, err := user.LookupId(strconv.FormatUint(uid, 10))
	if err != nil {
		return nil, err
	}
	gid, err := strconv.ParseUint(account.Gid, 10, 32)
	if err != nil {
		return nil, err
	}
	return &signedInUser{
		name:        values["Name"],
		uid:         uint32(uid),
		gid:         uint32(gid),
		home:        account.HomeDir,
		sessionID:   id,
		display:     values["Display"],
		sessionType: values["Type"],
	}, nil
}

func commandOutput(ctx context.Context, name string, args ...string) (string, error) {
	out, err := exec.CommandContext(ctx, name, args...).Output()
	return string(out), err
}

// stage writes the script where only this user and root can read it. The directory stays owned by root so the user
// cannot replace the script between writing it and reading it; the script itself is theirs to read and nothing more.
func (u *signedInUser) stage(script *agentv1.ScriptJob) (scriptPath, workDir string, release func(), err error) {
	dir, err := os.MkdirTemp(stageDirRoot, "fleeto-job-")
	if err != nil {
		return "", "", func() {}, err
	}
	release = func() { _ = os.RemoveAll(dir) }
	if err := os.Chmod(dir, 0o755); err != nil {
		release()
		return "", "", func() {}, err
	}

	name, body := scriptFile(script)
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, body, 0o400); err != nil {
		release()
		return "", "", func() {}, err
	}
	if err := os.Chown(path, int(u.uid), int(u.gid)); err != nil {
		release()
		return "", "", func() {}, fmt.Errorf("hand the script to %s: %w", u.name, err)
	}

	// The script runs from the user's home directory, where they may write; the staging directory is theirs to read only.
	work := u.home
	if info, err := os.Stat(work); err != nil || !info.IsDir() {
		work = dir
	}
	return path, work, release, nil
}

func (u *signedInUser) close() {}

// accountName is the account the script runs under, for the job history.
func (u *signedInUser) accountName() string { return u.name }

// environment is a login-like environment: enough for a script to find the user's session, without copying root's.
func (u *signedInUser) environment(extra []string) []string {
	env := []string{
		"USER=" + u.name,
		"LOGNAME=" + u.name,
		"HOME=" + u.home,
		"SHELL=/bin/sh",
		"PATH=/usr/local/bin:/usr/bin:/bin",
		"XDG_RUNTIME_DIR=/run/user/" + strconv.FormatUint(uint64(u.uid), 10),
		"XDG_SESSION_ID=" + u.sessionID,
		"DBUS_SESSION_BUS_ADDRESS=unix:path=/run/user/" + strconv.FormatUint(uint64(u.uid), 10) + "/bus",
	}
	if u.display != "" {
		env = append(env, "DISPLAY="+u.display)
	}
	if u.sessionType == "wayland" {
		env = append(env, "WAYLAND_DISPLAY=wayland-0")
	}
	return append(env, extra...)
}
