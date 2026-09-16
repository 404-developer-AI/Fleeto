//go:build !windows && !linux

package jobs

import "github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"

// signedInUser exists so the shared code compiles on platforms without a supported agent; running a script as the
// signed-in user is implemented for Windows and Linux only.
type signedInUser struct {
	uid uint32
	gid uint32
}

func signedInSession(string) (*signedInUser, error) { return nil, ErrNoUserSignedIn }

// SignedInUsers is empty on platforms without a supported agent.
func SignedInUsers() ([]*agentv1.SignedInUser, error) { return []*agentv1.SignedInUser{}, nil }

func (u *signedInUser) stage(*agentv1.ScriptJob) (string, string, func(), error) {
	return "", "", func() {}, ErrNoUserSignedIn
}

func (u *signedInUser) close() {}

func (u *signedInUser) accountName() string { return "" }

func (u *signedInUser) environment(extra []string) []string { return extra }
