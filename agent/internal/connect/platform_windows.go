//go:build windows

package connect

import (
	"errors"
	"fmt"
	"io"
	"os"
	"os/exec"
	"path/filepath"
	"strings"
	"syscall"

	"golang.org/x/sys/windows"
	"golang.org/x/sys/windows/registry"
)

// SystemUI shows Windows message boxes and opens the default browser.
type SystemUI struct{}

const (
	mbYesNo         = 0x00000004
	mbIconQuestion  = 0x00000020
	mbIconError     = 0x00000010
	mbIconInfo      = 0x00000040
	mbSetForeground = 0x00010000
	idYes           = 6
)

func messageBox(title, message string, flags uint32) int32 {
	t, _ := windows.UTF16PtrFromString(title)
	m, _ := windows.UTF16PtrFromString(message)
	result, _ := windows.MessageBox(0, m, t, flags|mbSetForeground)
	return result
}

func (SystemUI) Confirm(title, message string) bool {
	return messageBox(title, message, mbYesNo|mbIconQuestion) == idYes
}

func (SystemUI) Error(title, message string) { messageBox(title, message, mbIconError) }

// Info shows a message.
func (SystemUI) Info(title, message string) { messageBox(title, message, mbIconInfo) }

func (SystemUI) OpenBrowser(url string) error {
	if !strings.HasPrefix(url, "http://") {
		return errors.New("refused to open a non-local address")
	}
	verb, _ := windows.UTF16PtrFromString("open")
	target, _ := windows.UTF16PtrFromString(url)
	return windows.ShellExecute(0, verb, target, nil, nil, windows.SW_SHOWNORMAL)
}

// ConfigDir is %APPDATA%\Fleeto Connect.
func ConfigDir() (string, error) {
	dir, err := os.UserConfigDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "Fleeto Connect"), nil
}

// LogDir is %LOCALAPPDATA%\Fleeto Connect.
func LogDir() (string, error) {
	dir, err := os.UserCacheDir()
	if err != nil {
		return "", err
	}
	return filepath.Join(dir, "Fleeto Connect"), nil
}

const (
	classesKey   = `Software\Classes\` + Scheme
	uninstallKey = `Software\Microsoft\Windows\CurrentVersion\Uninstall\FleetoConnect`
	exeName      = "fleeto-connect.exe"
)

// InstallDir is %LOCALAPPDATA%\Programs\Fleeto Connect: a per-user install that needs no administrator.
func InstallDir() (string, error) {
	local, err := windows.KnownFolderPath(windows.FOLDERID_LocalAppData, 0)
	if err != nil {
		return "", err
	}
	return filepath.Join(local, "Programs", "Fleeto Connect"), nil
}

// Installed reports whether this program runs from its install folder.
func Installed() bool {
	dir, err := InstallDir()
	if err != nil {
		return false
	}
	self, err := os.Executable()
	return err == nil && strings.EqualFold(filepath.Clean(filepath.Dir(self)), filepath.Clean(dir))
}

// Install copies this program into the install folder of the user and registers the fleeto-connect: link and an uninstall entry for the
// user only.
func Install(version string) error {
	dir, err := InstallDir()
	if err != nil {
		return err
	}
	self, err := os.Executable()
	if err != nil {
		return err
	}
	target := filepath.Join(dir, exeName)
	if !strings.EqualFold(filepath.Clean(self), filepath.Clean(target)) {
		if err := os.MkdirAll(dir, 0o700); err != nil {
			return fmt.Errorf("create %s: %w", dir, err)
		}
		if err := copyFile(self, target); err != nil {
			return fmt.Errorf("copy Fleeto Connect to %s: %w (close a running Fleeto Connect first)", dir, err)
		}
	}
	command := fmt.Sprintf(`"%s" "%%1"`, target)
	if err := setKey(classesKey, map[string]string{"": "URL:Fleeto Connect", "URL Protocol": ""}); err != nil {
		return err
	}
	if err := setKey(classesKey+`\DefaultIcon`, map[string]string{"": target + ",0"}); err != nil {
		return err
	}
	if err := setKey(classesKey+`\shell\open\command`, map[string]string{"": command}); err != nil {
		return err
	}
	return setKey(uninstallKey, map[string]string{
		"DisplayName": "Fleeto Connect", "DisplayVersion": version, "Publisher": "Steaan", "InstallLocation": dir,
		"DisplayIcon": target, "UninstallString": fmt.Sprintf(`"%s" --uninstall`, target),
	}, map[string]uint32{"NoModify": 1, "NoRepair": 1})
}

// Uninstall removes the link, the uninstall entry, the settings and, after this program exits, its folder.
func Uninstall() error {
	for _, key := range []string{classesKey + `\shell\open\command`, classesKey + `\shell\open`, classesKey + `\shell`, classesKey + `\DefaultIcon`, classesKey, uninstallKey} {
		if err := registry.DeleteKey(registry.CURRENT_USER, key); err != nil && !errors.Is(err, registry.ErrNotExist) && !errors.Is(err, syscall.ERROR_FILE_NOT_FOUND) {
			return fmt.Errorf("remove %s: %w", key, err)
		}
	}
	if dir, err := ConfigDir(); err == nil {
		_ = os.RemoveAll(dir)
	}
	dir, err := InstallDir()
	if err != nil {
		return err
	}
	// The running program cannot delete itself: a short-lived cmd removes the folder once it exited.
	cmd := exec.Command("cmd.exe", "/c", "ping -n 3 127.0.0.1 >nul & rmdir /s /q \""+dir+"\"")
	cmd.SysProcAttr = &syscall.SysProcAttr{HideWindow: true, CreationFlags: windows.CREATE_NO_WINDOW}
	return cmd.Start()
}

func setKey(path string, values map[string]string, dwords ...map[string]uint32) error {
	key, _, err := registry.CreateKey(registry.CURRENT_USER, path, registry.SET_VALUE)
	if err != nil {
		return fmt.Errorf("register %s: %w", path, err)
	}
	defer key.Close()
	for name, value := range values {
		if err := key.SetStringValue(name, value); err != nil {
			return fmt.Errorf("register %s: %w", path, err)
		}
	}
	for _, set := range dwords {
		for name, value := range set {
			if err := key.SetDWordValue(name, value); err != nil {
				return fmt.Errorf("register %s: %w", path, err)
			}
		}
	}
	return nil
}

func copyFile(from, to string) error {
	in, err := os.Open(from)
	if err != nil {
		return err
	}
	defer in.Close()
	temp := to + ".new"
	out, err := os.Create(temp)
	if err != nil {
		return err
	}
	if _, err := io.Copy(out, in); err != nil {
		out.Close()
		return err
	}
	if err := out.Close(); err != nil {
		return err
	}
	return os.Rename(temp, to)
}
