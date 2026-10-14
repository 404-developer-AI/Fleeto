package remote

import (
	"fmt"
	"runtime"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// ScriptRequest is a library script a technician runs in a terminal of a remote background session (0.6.0), sent by the browser on the
// end-to-end encrypted channel. The browser got it from web, which only offers the scripts the endpoint's policy allows and records the
// run in the audit log; the endpoint checks the body against its hash and language and reports the run as an action of the session.
type ScriptRequest struct {
	Name     string `json:"name"`
	Version  uint32 `json:"version"`
	Language string `json:"language"`
	Body     string `json:"body"`
	Sha256   string `json:"sha256"`
	// RunAs is "system" (SYSTEM or root) or "user" (the signed-in user).
	RunAs string `json:"runAs"`
	// User is the SID or uid of the signed-in user to run as; empty takes whoever is signed in, the console first.
	User string `json:"user,omitempty"`
}

// ScriptOpener starts a library script in a terminal with an initial size. The account is the user it runs as, empty for SYSTEM or root.
type ScriptOpener func(request ScriptRequest, cols, rows int) (terminal Terminal, account string, err error)

const (
	// RunAsSystem runs the script as SYSTEM on Windows and root on Linux, like the terminal.
	RunAsSystem = "system"
	// RunAsUser runs the script as the signed-in user.
	RunAsUser = "user"
)

// Job turns the request into the script of a job, for the staging and checks the jobs package already has.
func (r ScriptRequest) Job() (*agentv1.ScriptJob, error) {
	language, ok := map[string]agentv1.ScriptLanguage{
		"powershell": agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL,
		"batch":      agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH,
		"shell":      agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL,
		"bash":       agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BASH,
	}[strings.ToLower(r.Language)]
	if !ok {
		return nil, fmt.Errorf("this endpoint does not know the script language %q", r.Language)
	}
	if r.RunAs != RunAsSystem && r.RunAs != RunAsUser {
		return nil, fmt.Errorf("choose whether the script runs as the system or as the signed-in user")
	}
	if r.RunAs == RunAsSystem && r.User != "" {
		return nil, fmt.Errorf("a user can only be chosen when the script runs as the signed-in user")
	}
	return &agentv1.ScriptJob{Language: language, Name: r.Name, Version: r.Version, Body: r.Body, Sha256: strings.ToLower(r.Sha256)}, nil
}

// scriptTerminal removes the staged script once the script has ended.
type scriptTerminal struct {
	Terminal
}

// withRelease starts the clean-up of a staged script: once the script has ended, release runs.
func withRelease(terminal Terminal, release func()) Terminal {
	go func() {
		_, _ = terminal.Wait()
		release()
	}()
	return scriptTerminal{Terminal: terminal}
}

// scriptTarget is how a run is named in the audit log: the script and its version, never its body.
func scriptTarget(request ScriptRequest) string {
	return fmt.Sprintf("%s (version %d)", clipText(request.Name, 200), request.Version)
}

var runtimeIsWindows = runtime.GOOS == "windows"

func firstNonEmptyText(values ...string) string {
	for _, v := range values {
		if v != "" {
			return v
		}
	}
	return ""
}

func clipText(s string, max int) string {
	if len(s) <= max {
		return s
	}
	return strings.ToValidUTF8(s[:max], "")
}
