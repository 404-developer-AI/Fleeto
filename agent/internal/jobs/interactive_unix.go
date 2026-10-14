//go:build !windows

package jobs

import (
	"syscall"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// Command is the interpreter and its arguments, the same as a job's.
func (s *Interactive) Command() (string, []string) {
	if s.Language == agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BASH {
		return "/bin/bash", []string{s.Path}
	}
	return "/bin/sh", []string{s.Path}
}

// Credential is the account of the signed-in user to drop to, or nil to stay root.
func (s *Interactive) Credential() *syscall.Credential {
	if s.session == nil {
		return nil
	}
	return &syscall.Credential{Uid: s.session.uid, Gid: s.session.gid}
}

// UserID is the uid of the signed-in user the script runs as, or -1 for root.
func (s *Interactive) UserID() int {
	if s.session == nil {
		return -1
	}
	return int(s.session.uid)
}

// Environment is the signed-in user's login-like environment with a terminal type, or nil for root, which gets the terminal's own.
func (s *Interactive) Environment() []string {
	if s.session == nil {
		return nil
	}
	return s.session.environment([]string{"TERM=xterm-256color"})
}
