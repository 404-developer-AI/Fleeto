//go:build windows

package screen

import (
	"errors"
	"fmt"
	"os"
	"runtime"

	"golang.org/x/sys/windows"
)

// OpenAsSessionUser opens a file copied on the endpoint with the rights of the user signed in on the Windows session (security review of
// 0.3.0 step 7): the path comes from that user's clipboard, so the agent (SYSTEM) must not read for them what they may not read
// themselves. The open runs on a thread of its own that impersonates the user and is thrown away afterwards.
func OpenAsSessionUser(path string, session uint32) (*os.File, error) {
	var token windows.Token
	if err := windows.WTSQueryUserToken(session, &token); err != nil {
		return nil, fmt.Errorf("nobody is signed in on Windows session %d: %w", session, err)
	}
	defer token.Close()
	var impersonation windows.Token
	if err := windows.DuplicateTokenEx(token, windows.TOKEN_IMPERSONATE|windows.TOKEN_QUERY, nil, windows.SecurityImpersonation,
		windows.TokenImpersonation, &impersonation); err != nil {
		return nil, err
	}
	defer impersonation.Close()

	type result struct {
		file *os.File
		err  error
	}
	done := make(chan result, 1)
	go func() {
		// Never unlocked: the thread ends with this goroutine, so no other code ever runs with the user's token.
		runtime.LockOSThread()
		if err := windows.SetThreadToken(nil, impersonation); err != nil {
			done <- result{err: err}
			return
		}
		f, err := os.Open(path) // #nosec G304 -- opened with the rights of the user whose clipboard named it.
		_ = windows.RevertToSelf()
		if err != nil {
			done <- result{err: err}
			return
		}
		if info, err := f.Stat(); err != nil || !info.Mode().IsRegular() {
			_ = f.Close()
			done <- result{err: errors.New("only a regular file can be downloaded")}
			return
		}
		done <- result{file: f}
	}()
	r := <-done
	return r.file, r.err
}
