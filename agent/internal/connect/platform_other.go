//go:build !windows

package connect

import (
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
)

// SystemUI asks and reports with zenity (part of Ubuntu desktop) and opens the default browser with xdg-open. Without zenity a question
// is answered no and an error goes to notify-send, so a link is never followed without the technician's answer.
type SystemUI struct{}

func (SystemUI) Confirm(title, message string) bool {
	path, err := exec.LookPath("zenity")
	if err != nil {
		notify(title, "Install zenity so Fleeto Connect can ask whether it may connect to an instance.")
		return false
	}
	return exec.Command(path, "--question", "--title="+title, "--text="+message, "--no-markup", "--width=420").Run() == nil
}

func (SystemUI) Error(title, message string) {
	if path, err := exec.LookPath("zenity"); err == nil {
		_ = exec.Command(path, "--error", "--title="+title, "--text="+message, "--no-markup", "--width=420").Run()
		return
	}
	notify(title, message)
}

// Info shows a message.
func (SystemUI) Info(title, message string) {
	if path, err := exec.LookPath("zenity"); err == nil {
		_ = exec.Command(path, "--info", "--title="+title, "--text="+message, "--no-markup", "--width=420").Run()
		return
	}
	notify(title, message)
}

func (SystemUI) OpenBrowser(url string) error {
	if !strings.HasPrefix(url, "http://") {
		return errors.New("refused to open a non-local address")
	}
	return exec.Command("xdg-open", url).Start()
}

func notify(title, message string) {
	if path, err := exec.LookPath("notify-send"); err == nil {
		_ = exec.Command(path, title, message).Run()
	}
}

// ConfigDir is ~/.config/fleeto-connect.
func ConfigDir() (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "fleeto-connect"), nil
}

// LogDir is ~/.cache/fleeto-connect.
func LogDir() (string, error) {
	dir, err := os.UserCacheDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "fleeto-connect"), nil
}

// Installed reports true: the .deb installs Fleeto Connect and registers its link.
func Installed() bool { return true }

// Install is the job of the .deb on Linux.
func Install(string) error {
	return errors.New("install Fleeto Connect on Ubuntu with its .deb package from the Fleeto web UI")
}

// Uninstall is the job of apt on Linux.
func Uninstall() error {
	return errors.New("remove Fleeto Connect with: sudo apt remove fleeto-connect")
}
