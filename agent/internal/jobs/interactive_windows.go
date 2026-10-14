//go:build windows

package jobs

import (
	"fmt"
	"os"
	"path/filepath"

	"golang.org/x/sys/windows"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// InteractiveDesktop is the window station and desktop a process in a signed-in user's session is created on.
const InteractiveDesktop = interactiveDesktop

// CommandLine is the program and command line of the run: the same interpreter as a job, but PowerShell may ask for input.
func (s *Interactive) CommandLine() (string, string, error) {
	if s.Language == agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL {
		root := os.Getenv("SystemRoot")
		if root == "" {
			root = `C:\Windows`
		}
		path := filepath.Join(root, "System32", "WindowsPowerShell", "v1.0", "powershell.exe")
		return path, fmt.Sprintf(`"%s" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%s"`, path, s.Path), nil
	}
	run, err := interpreterFor(s.Language, s.Path)
	return run.path, run.cmdLine, err
}

// Token is the signed-in user's primary token to create the process with, or 0 to run as SYSTEM. It stays valid until Release.
func (s *Interactive) Token() windows.Token {
	if s.session == nil {
		return 0
	}
	return s.session.token
}

// Environment is the signed-in user's own environment as a block for CreateProcessAsUser; nil for SYSTEM.
func (s *Interactive) Environment() (*uint16, error) {
	if s.session == nil {
		return nil, nil
	}
	return environmentBlock(s.session.token, nil)
}
