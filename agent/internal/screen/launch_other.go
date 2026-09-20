//go:build !windows && !linux

package screen

import (
	"context"
	"io"
	"log/slog"
	"os"
	"path/filepath"
)

// HelperCommand is the argument of fleeto-agent that runs the helper.
const HelperCommand = "remote-helper"

// SessionIDEnv tells the helper which Windows session it serves.
const SessionIDEnv = "FLEETO_REMOTE_SESSION"

// ConsentCommand is the argument of fleeto-agent that asks for consent on Linux.
const ConsentCommand = "remote-consent"

const batchMode = 0o700

// DefaultLauncher is not available on this platform.
func DefaultLauncher(*slog.Logger) Launcher {
	return func(context.Context, uint32) (Helper, error) { return nil, ErrNotSupported }
}

// DefaultClipboardLauncher is not available on this platform.
func DefaultClipboardLauncher(*slog.Logger) Launcher {
	return func(context.Context, uint32) (Helper, error) { return nil, ErrNotSupported }
}

// GrantFiles is not available on this platform.
func GrantFiles([]string, uint32) error { return ErrNotSupported }

// RunConsent is not available on this platform.
func RunConsent(context.Context, io.Reader, io.Writer, *slog.Logger) error { return ErrNotSupported }

// ClipboardCommand is the argument of fleeto-agent that serves the clipboard of a Windows session as the user signed in on it.
const ClipboardCommand = "remote-clipboard"

// RunClipboardAgent is not available off Windows.
func RunClipboardAgent(context.Context, io.Reader, io.Writer, *slog.Logger) error {
	return ErrNotSupported
}

// RunHelper is not available off Windows.
func RunHelper(context.Context, io.Reader, io.Writer, uint32, *slog.Logger) error {
	return ErrNotSupported
}

// ConsoleSession is not available off Windows.
func ConsoleSession() uint32 { return 0 }

// SessionExists is not available off Windows.
func SessionExists(uint32) bool { return true }

// SecureAttention is not available off Windows.
func SecureAttention() error { return ErrNotSupported }

// EnableSoftwareSAS does nothing off Windows.
func EnableSoftwareSAS() (bool, error) { return false, nil }

// Supported reports whether this platform serves remote control.
func Supported() bool { return false }

// Wording of the session shown, for the technician (Windows: numbered sessions).

func shownName(session uint32) string { return "Windows session " + itoa(session) }

func noConsoleText() string { return "No Windows session is attached to the console right now." }

func consoleSwitchedText(session uint32) string {
	return "The console switched to Windows session " + itoa(session) + "."
}

func nobodySignedInText() string { return "Nobody is signed in on this Windows session" }

// StagingRoot is where files pasted into remote control sessions wait: a folder in the agent's data directory.
func StagingRoot(dataDir string) string { return filepath.Join(dataDir, "RemoteClipboard") }

// PrepareStaging deletes files left from earlier sessions; files cannot be pasted on this platform.
func PrepareStaging(root string) error { return CleanStaging(root) }

// OpenAsSessionUser is not available on this platform.
func OpenAsSessionUser(string, uint32) (*os.File, error) { return nil, ErrNotSupported }
