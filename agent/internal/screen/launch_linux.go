//go:build linux

package screen

import (
	"bufio"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log/slog"
	"os"
	"os/exec"
	"os/user"
	"path/filepath"
	"strconv"
	"strings"
	"sync"
	"syscall"
	"time"
)

// Remote control on Linux with X11 (0.3.0 step 6, decided 2026-09-19): only the console is shown, the active graphical session of seat0,
// including the sign-in screen when it runs on X11 (LightDM, SDDM). The agent runs as root; it finds the X server of that session, reads
// the display's cookie from the server's own authority file and starts its children with it: the helper (screen, input, banner) runs as
// nobody, the clipboard and the consent prompt as the user of the session. Each child drops from root to its account before it talks to
// the X server, and gets the display and cookie over its pipe, never in its environment. A Wayland session cannot be shown; the
// technician is told so, and remote background works.

// HelperCommand is the argument of fleeto-agent that runs the helper.
const HelperCommand = "remote-helper"

// ClipboardCommand is the argument of fleeto-agent that serves the clipboard of the session as its user.
const ClipboardCommand = "remote-clipboard"

// ConsentCommand is the argument of fleeto-agent that asks the person at the screen for consent (Linux).
const ConsentCommand = "remote-consent"

// SessionIDEnv is used on Windows only; the Linux helper gets its session in FrameX11.
const SessionIDEnv = "FLEETO_REMOTE_SESSION"

// batchMode is the mode of a folder of pasted files: the user of the session may pass through it to the files given to them, but not
// list it or create anything in it, so nobody can swap a file the agent writes.
const batchMode = 0o711

// errWayland is the message for a session that runs Wayland.
var errWayland = errors.New("The screen of this endpoint runs a Wayland session, which remote control cannot show. It needs an X11 " +
	"session (for example \"GNOME on Xorg\" at sign-in). Remote background works.")

// consoleX11 is the X11 display on the screen of the endpoint now.
type consoleX11 struct {
	session loginSession
	display string
	cookie  string
}

// findConsole finds the active session of seat0, its X server and the display's cookie.
func findConsole(ctx context.Context) (consoleX11, error) {
	session, err := activeSeatSession(ctx)
	if err != nil {
		return consoleX11{}, err
	}
	if session.kind == "wayland" || session.kind == "mir" {
		if session.class == "greeter" {
			return consoleX11{}, errors.New("The sign-in screen of this endpoint runs on Wayland, which remote control cannot show. Remote " +
				"control works once a user signs in to an X11 session; remote background works now.")
		}
		return consoleX11{}, errWayland
	}
	servers := xServers()
	var server *xServer
	for i := range servers {
		s := servers[i]
		if (session.display != "" && s.display == session.display) || (session.display == "" && session.vt != 0 && s.vt == session.vt) {
			server = &s
			break
		}
	}
	if server == nil && session.display == "" && len(servers) == 1 {
		server = &servers[0]
	}
	if server == nil {
		if session.kind == "x11" && session.display != "" {
			server = &xServer{display: session.display}
		} else {
			return consoleX11{}, errors.New("No X11 desktop runs on the screen of this endpoint (the active session is " + describeKind(session) +
				"). Remote control needs an X11 desktop; remote background works.")
		}
	}
	if server.wayland {
		return consoleX11{}, errWayland
	}
	cookie, err := displayCookie(*server, session)
	if err != nil {
		return consoleX11{}, err
	}
	return consoleX11{session: session, display: server.display, cookie: cookie}, nil
}

func describeKind(s loginSession) string {
	switch s.kind {
	case "tty":
		return "a text console"
	case "", "unspecified":
		return "not graphical"
	}
	return s.kind
}

// activeSeatSession is the session on the screen of seat0.
func activeSeatSession(ctx context.Context) (loginSession, error) {
	tool, err := exec.LookPath("loginctl")
	if err != nil {
		return loginSession{}, errors.New("this endpoint has no systemd-logind (loginctl), so its screen cannot be found")
	}
	ctx, cancel := context.WithTimeout(ctx, 10*time.Second)
	defer cancel()
	out, err := exec.CommandContext(ctx, tool, "show-seat", "seat0", "--property=ActiveSession", "--value").Output()
	if err != nil {
		return loginSession{}, errors.New("this endpoint has no local screen (no seat0)")
	}
	id := strings.TrimSpace(string(out))
	if id == "" {
		return loginSession{}, errors.New("Nobody uses the screen of this endpoint right now (no active session on it). Remote background works.")
	}
	return showSession(ctx, tool, id)
}

func showSession(ctx context.Context, tool, id string) (loginSession, error) {
	out, err := exec.CommandContext(ctx, tool, "show-session", id, "--property=Type", "--property=Class", "--property=User",
		"--property=Name", "--property=Display", "--property=VTNr", "--property=Leader", "--property=Active").Output()
	if err != nil {
		return loginSession{}, fmt.Errorf("read session %s: %w", id, err)
	}
	return parseLoginSession(id, string(out))
}

// xServers lists the X servers that run, from their command lines.
func xServers() []xServer {
	entries, err := os.ReadDir("/proc")
	if err != nil {
		return nil
	}
	var out []xServer
	for _, e := range entries {
		if _, err := strconv.Atoi(e.Name()); err != nil {
			continue
		}
		raw, err := os.ReadFile(filepath.Join("/proc", e.Name(), "cmdline"))
		if err != nil || len(raw) == 0 {
			continue
		}
		args := strings.Split(strings.TrimRight(string(raw), "\x00"), "\x00")
		if s, ok := parseXServerCommand(args); ok {
			out = append(out, s)
		}
	}
	return out
}

// displayCookie finds the display's cookie: in the X server's own authority file, the XAUTHORITY of the session, the user's
// ~/.Xauthority, or the file GDM keeps for the user.
func displayCookie(server xServer, session loginSession) (string, error) {
	hostname, _ := os.Hostname()
	var candidates []string
	if server.auth != "" {
		candidates = append(candidates, server.auth)
	}
	if session.leader > 0 {
		if env, err := os.ReadFile(filepath.Join("/proc", strconv.Itoa(session.leader), "environ")); err == nil {
			for _, v := range strings.Split(string(env), "\x00") {
				if path, ok := strings.CutPrefix(v, "XAUTHORITY="); ok && path != "" {
					candidates = append(candidates, path)
				}
			}
		}
	}
	if account, err := user.LookupId(strconv.FormatUint(uint64(session.uid), 10)); err == nil && account.HomeDir != "" {
		candidates = append(candidates, filepath.Join(account.HomeDir, ".Xauthority"))
	}
	candidates = append(candidates, fmt.Sprintf("/run/user/%d/gdm/Xauthority", session.uid))
	for _, path := range candidates {
		data, err := readSmall(path, 1<<20)
		if err != nil {
			continue
		}
		if cookie, err := cookieForDisplay(data, server.display, hostname); err == nil {
			return cookie, nil
		}
	}
	return "", errors.New("the cookie of the X display " + server.display + " was not found, so the endpoint cannot open its screen")
}

// readSmall reads the start of a regular file. Some of the paths come from the user's session (XAUTHORITY, the home folder), so a
// named pipe or a device there must never block or feed the agent: the file is opened without blocking and must be a regular file.
func readSmall(path string, limit int64) ([]byte, error) {
	f, err := os.OpenFile(path, os.O_RDONLY|syscall.O_NONBLOCK, 0) // #nosec G304 -- authority files, read by the agent as root.
	if err != nil {
		return nil, err
	}
	defer f.Close()
	info, err := f.Stat()
	if err != nil || !info.Mode().IsRegular() {
		return nil, errors.New("not a regular file")
	}
	return io.ReadAll(io.LimitReader(f, limit))
}

// accountIDs returns the uid and gid of an account name, or the fallback.
func accountIDs(name string, fallbackUID, fallbackGID uint32) (uint32, uint32) {
	u, err := user.Lookup(name)
	if err != nil {
		return fallbackUID, fallbackGID
	}
	uid, err1 := strconv.ParseUint(u.Uid, 10, 32)
	gid, err2 := strconv.ParseUint(u.Gid, 10, 32)
	if err1 != nil || err2 != nil {
		return fallbackUID, fallbackGID
	}
	return uint32(uid), uint32(gid)
}

// sessionAccount is the uid and gid of the session's user.
func sessionAccount(s loginSession) (uint32, uint32, error) {
	u, err := user.LookupId(strconv.FormatUint(uint64(s.uid), 10))
	if err != nil {
		return 0, 0, fmt.Errorf("the user of session %s is unknown: %w", s.id, err)
	}
	gid, err := strconv.ParseUint(u.Gid, 10, 32)
	if err != nil {
		return 0, 0, err
	}
	return s.uid, uint32(gid), nil
}

// DefaultLauncher starts the helper for the console as nobody (0.3.0 step 6).
func DefaultLauncher(logger *slog.Logger) Launcher {
	return func(ctx context.Context, _ uint32) (Helper, error) {
		console, err := findConsole(ctx)
		if err != nil {
			return nil, err
		}
		uid, gid := accountIDs("nobody", 65534, 65534)
		return spawnX11Child(logger, HelperCommand, "remote control helper", console, uid, gid)
	}
}

// DefaultClipboardLauncher starts the clipboard process of the console as the user of the session.
func DefaultClipboardLauncher(logger *slog.Logger) Launcher {
	return func(ctx context.Context, _ uint32) (Helper, error) {
		console, err := findConsole(ctx)
		if err != nil {
			return nil, err
		}
		if console.session.class == "greeter" || console.session.uid == 0 {
			return nil, errors.New("nobody is signed in on the screen of this endpoint, so its clipboard cannot be used")
		}
		uid, gid, err := sessionAccount(console.session)
		if err != nil {
			return nil, err
		}
		return spawnX11Child(logger, ClipboardCommand, "remote clipboard", console, uid, gid)
	}
}

// spawnX11Child starts one command of the agent binary as root with pipes, and gives it the display, the cookie and the account to drop
// to as its first frame. It ends with the agent: when the agent stops, even when it is killed, its input closes and it exits. (No
// parent-death signal: Go ties it to the thread that started the child, which may end while the agent runs.)
func spawnX11Child(logger *slog.Logger, command, label string, console consoleX11, uid, gid uint32) (Helper, error) {
	exe, err := os.Executable()
	if err != nil {
		return nil, fmt.Errorf("find the agent binary: %w", err)
	}
	cmd := exec.Command(exe, command) // #nosec G204 -- the agent's own binary with a fixed command.
	cmd.Env = []string{"PATH=/usr/local/bin:/usr/bin:/bin", "LANG=C.UTF-8", "HOME=/"}
	cmd.Dir = "/"
	cmd.SysProcAttr = &syscall.SysProcAttr{Setsid: true}
	stdin, err := cmd.StdinPipe()
	if err != nil {
		return nil, err
	}
	stdout, err := cmd.StdoutPipe()
	if err != nil {
		return nil, err
	}
	stderr, err := cmd.StderrPipe()
	if err != nil {
		return nil, err
	}
	if err := cmd.Start(); err != nil {
		return nil, fmt.Errorf("start the %s: %w", label, err)
	}
	h := &linuxHelper{cmd: cmd, in: stdin, out: stdout}
	go func() {
		scanner := bufio.NewScanner(stderr)
		for scanner.Scan() {
			logger.Info(label+": "+scanner.Text(), "session", console.session.id, "display", console.display)
		}
	}()
	body, _ := json.Marshal(X11Body{Display: console.display, Cookie: console.cookie, UID: uid, GID: gid, Session: console.session.id})
	if err := WriteFrame(stdin, append([]byte{FrameX11}, body...)); err != nil {
		_ = h.Close()
		return nil, fmt.Errorf("start the %s: %w", label, err)
	}
	return h, nil
}

type linuxHelper struct {
	cmd  *exec.Cmd
	in   io.WriteCloser
	out  io.ReadCloser
	once sync.Once
}

func (h *linuxHelper) In() io.Writer  { return h.in }
func (h *linuxHelper) Out() io.Reader { return h.out }

// Close closes the child's input, which makes it release every key and button and exit, and kills it when it does not within 2 seconds.
func (h *linuxHelper) Close() error {
	h.once.Do(func() {
		_ = h.in.Close()
		done := make(chan struct{})
		go func() {
			_ = h.cmd.Wait()
			close(done)
		}()
		select {
		case <-done:
		case <-time.After(2 * time.Second):
			_ = h.cmd.Process.Kill()
			<-done
		}
	})
	return nil
}

// ConsoleSession is a number for the session on the screen of seat0, so the hub notices when it changes (a user signs in or out).
func ConsoleSession() uint32 {
	session, err := activeSeatSession(context.Background())
	if err != nil {
		return 0
	}
	return sessionNumber(session.id)
}

// SessionExists is true: only the console is shown on Linux, and it always exists.
func SessionExists(uint32) bool { return true }

// SecureAttention is a Windows key sequence; X11 has none.
func SecureAttention() error {
	return errors.New("Ctrl+Alt+Del is a Windows sign-in key and does nothing on Linux.")
}

// EnableSoftwareSAS does nothing on Linux.
func EnableSoftwareSAS() (bool, error) { return false, nil }

// Supported reports whether this platform serves remote control.
func Supported() bool { return true }

// SessionUser is the user signed in on the screen, "" on the sign-in screen.
func SessionUser(uint32) string {
	session, err := activeSeatSession(context.Background())
	if err != nil || session.class != "user" || session.uid == 0 {
		return ""
	}
	return session.name
}

// StageFolder creates the folder of a session's pasted files: owned by root, passable (not listable) for others, inside a root that is
// the same. The files in it are handed to the user of the session one by one (GrantFiles).
func StageFolder(dir string, _ uint32) error {
	root := filepath.Dir(dir)
	if err := os.MkdirAll(root, batchMode); err != nil {
		return err
	}
	if err := os.Chmod(root, batchMode); err != nil {
		return err
	}
	if err := os.Mkdir(dir, batchMode); err != nil && !errors.Is(err, os.ErrExist) {
		return err
	}
	return os.Chmod(dir, batchMode)
}

// GrantFiles gives pasted files to the user signed in on the screen, readable by them only, before they go on the clipboard.
func GrantFiles(paths []string, _ uint32) error {
	session, err := activeSeatSession(context.Background())
	if err != nil {
		return err
	}
	if session.class != "user" || session.uid == 0 {
		return errors.New("nobody is signed in on the screen of this endpoint, so pasted files cannot be given to anyone")
	}
	uid, gid, err := sessionAccount(session)
	if err != nil {
		return err
	}
	for _, p := range paths {
		info, err := os.Lstat(p)
		if err != nil || !info.Mode().IsRegular() {
			return fmt.Errorf("%s is not a pasted file", filepath.Base(p))
		}
		if err := os.Lchown(p, int(uid), int(gid)); err != nil {
			return err
		}
		if err := os.Chmod(p, 0o600); err != nil {
			return err
		}
	}
	return nil
}

// AskConsent shows the consent prompt on the screen as the user of the session and waits for the answer or the timeout.
func AskConsent(ctx context.Context, _ uint32, technician string, timeout time.Duration) (ConsentAnswer, error) {
	console, err := findConsole(ctx)
	if err != nil {
		return ConsentRefused, err
	}
	uid, gid, err := sessionAccount(console.session)
	if err != nil {
		return ConsentRefused, err
	}
	child, err := spawnX11Child(slog.New(slog.DiscardHandler), ConsentCommand, "remote consent", console, uid, gid)
	if err != nil {
		return ConsentRefused, err
	}
	defer child.Close()
	ask, _ := json.Marshal(ConsentAskBody{Technician: technician, Seconds: int(timeout / time.Second)})
	if err := WriteFrame(child.In(), append([]byte{FrameConsentAsk}, ask...)); err != nil {
		return ConsentRefused, err
	}
	answers := make(chan ConsentAnswerBody, 1)
	go func() {
		frame, err := ReadFrame(child.Out())
		var answer ConsentAnswerBody
		if err == nil && frame[0] == FrameConsentAnswer {
			_ = json.Unmarshal(frame[1:], &answer)
		}
		answers <- answer
	}()
	select {
	case <-ctx.Done():
		return ConsentRefused, ctx.Err()
	case <-time.After(timeout + 10*time.Second):
		return ConsentRefused, errors.New("the consent prompt did not answer")
	case answer := <-answers:
		switch answer.Answer {
		case "allowed":
			return ConsentAllowed, nil
		case "timeout":
			return ConsentTimedOut, nil
		case "refused":
			return ConsentRefused, nil
		}
		return ConsentRefused, errors.New("the consent prompt could not be shown on the screen")
	}
}

// Wording of the session shown, for the technician: Linux shows the console only, so no session numbers.

func shownName(uint32) string { return "the screen of this endpoint" }

func noConsoleText() string { return "Nobody uses the screen of this endpoint right now." }

func consoleSwitchedText(uint32) string {
	return "Someone signed in or out on the screen of this endpoint; the screen is shown again."
}

func nobodySignedInText() string {
	return "Nobody is signed in on the screen (it shows the sign-in screen)"
}

// StagingRoot is where files pasted into remote control sessions wait on Linux: a folder of its own in /run (memory, empty after a
// restart), so the path to the files passes no folder of the agent that the user of the session may not enter.
func StagingRoot(string) string { return "/run/fleeto-remote-clipboard" }
