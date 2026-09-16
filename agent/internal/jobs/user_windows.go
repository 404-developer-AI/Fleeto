//go:build windows

package jobs

import (
	"fmt"
	"os"
	"path/filepath"
	"unsafe"

	"golang.org/x/sys/windows"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// signedInUser is the session a job runs in when it must run as the signed-in user. The agent runs as SYSTEM, so it may
// ask the terminal services API for that session's token; the token is what makes the script run as them, in their
// session, with their profile.
type signedInUser struct {
	session uint32
	token   windows.Token
	sid     string
	// account is DOMAIN\name, or the SID when the name cannot be looked up.
	account string
	profile string
}

// signedInSession picks the active session: the console session when someone is signed in there, otherwise the first
// active session (a remote desktop session counts). Connected-but-signed-out and disconnected sessions are skipped.
func signedInSession() (*signedInUser, error) {
	sessions, err := activeSessions()
	if err != nil {
		return nil, err
	}
	console := windows.WTSGetActiveConsoleSessionId()
	order := sessions
	if slicesContains(sessions, console) {
		order = append([]uint32{console}, sessions...)
	}

	for _, id := range order {
		user, err := openSession(id)
		if err != nil {
			continue
		}
		return user, nil
	}
	return nil, ErrNoUserSignedIn
}

func slicesContains(values []uint32, value uint32) bool {
	for _, v := range values {
		if v == value {
			return true
		}
	}
	return false
}

// activeSessions lists the sessions with a signed-in user, newest last. Session 0 is the services session and never has one.
func activeSessions() ([]uint32, error) {
	var count uint32
	var first *windows.WTS_SESSION_INFO
	if err := windows.WTSEnumerateSessions(windows.Handle(0), 0, 1, &first, &count); err != nil {
		return nil, fmt.Errorf("list the sessions of this endpoint: %w", err)
	}
	defer windows.WTSFreeMemory(uintptr(unsafe.Pointer(first)))

	var ids []uint32
	for _, info := range unsafe.Slice(first, int(count)) {
		if info.State == windows.WTSActive && info.SessionID != 0 {
			ids = append(ids, info.SessionID)
		}
	}
	if len(ids) == 0 {
		return nil, ErrNoUserSignedIn
	}
	return ids, nil
}

// openSession takes the token of the user in this session and turns it into one a process can be created with.
func openSession(id uint32) (*signedInUser, error) {
	var token windows.Token
	if err := windows.WTSQueryUserToken(id, &token); err != nil {
		return nil, err
	}
	defer token.Close()

	var primary windows.Token
	if err := windows.DuplicateTokenEx(token, windows.MAXIMUM_ALLOWED, nil, windows.SecurityImpersonation,
		windows.TokenPrimary, &primary); err != nil {
		return nil, err
	}

	account, err := primary.GetTokenUser()
	if err != nil {
		primary.Close()
		return nil, err
	}
	profile, err := primary.GetUserProfileDirectory()
	if err != nil {
		profile = ""
	}
	sid := account.User.Sid.String()
	name := sid
	if user, domain, _, err := account.User.Sid.LookupAccount(""); err == nil {
		name = domain + `\` + user
	}
	return &signedInUser{session: id, token: primary, sid: sid, account: name, profile: profile}, nil
}

// accountName is the account the script runs under, for the job history.
func (u *signedInUser) accountName() string { return u.account }

func (u *signedInUser) close() {
	if u != nil && u.token != 0 {
		u.token.Close()
		u.token = 0
	}
}

// stage writes the script into a directory only SYSTEM, the administrators and this user may open. The user may read the
// script but not replace it, so what runs is what was signed.
func (u *signedInUser) stage(script *agentv1.ScriptJob) (scriptPath, workDir string, release func(), err error) {
	base := os.Getenv("ProgramData")
	if base == "" {
		base = `C:\ProgramData`
	}
	dir, err := os.MkdirTemp(filepath.Join(base, "Fleeto"), "job-")
	if err != nil {
		return "", "", func() {}, err
	}
	release = func() { _ = os.RemoveAll(dir) }
	if err := grantReadTo(dir, u.sid); err != nil {
		release()
		return "", "", func() {}, err
	}

	name, body := scriptFile(script)
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, body, 0o600); err != nil {
		release()
		return "", "", func() {}, err
	}
	if err := grantReadTo(path, u.sid); err != nil {
		release()
		return "", "", func() {}, err
	}

	// The script runs from the user's own profile, where they may write; the staging directory is theirs to read only.
	work := u.profile
	if info, err := os.Stat(work); err != nil || !info.IsDir() {
		work = dir
	}
	return path, work, release, nil
}

// grantReadTo replaces the access of a file or directory with SYSTEM and administrators in full, and this user reading only.
func grantReadTo(path, sid string) error {
	sddl := fmt.Sprintf("D:P(A;OICI;FA;;;SY)(A;OICI;FA;;;BA)(A;OICI;GRGX;;;%s)", sid)
	descriptor, err := windows.SecurityDescriptorFromString(sddl)
	if err != nil {
		return fmt.Errorf("build security descriptor: %w", err)
	}
	dacl, _, err := descriptor.DACL()
	if err != nil {
		return fmt.Errorf("read DACL: %w", err)
	}
	if err := windows.SetNamedSecurityInfo(path, windows.SE_FILE_OBJECT,
		windows.DACL_SECURITY_INFORMATION|windows.PROTECTED_DACL_SECURITY_INFORMATION, nil, nil, dacl, nil); err != nil {
		return fmt.Errorf("give %s access to %s: %w", sid, path, err)
	}
	return nil
}
