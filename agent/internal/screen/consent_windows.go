//go:build windows

package screen

import (
	"context"
	"fmt"
	"log/slog"
	"time"
	"unsafe"

	"golang.org/x/sys/windows"
)

// Consent, the signed-in user and the staging folder of pasted files, done by the agent service itself (0.3.0 step 4): the service in
// session 0 can show a message box in any Windows session (WTSSendMessage) and reads the user of a session, so the helper only captures,
// injects and handles the clipboard.

var (
	wtsapi32                        = windows.NewLazySystemDLL("wtsapi32.dll")
	procWTSSendMessageW             = wtsapi32.NewProc("WTSSendMessageW")
	procWTSQuerySessionInformationW = wtsapi32.NewProc("WTSQuerySessionInformationW")
)

const (
	wtsUserName   = 5
	wtsDomainName = 7

	mbYesNo         = 0x00000004
	mbIconQuestion  = 0x00000020
	mbDefButton2    = 0x00000100
	mbSetForeground = 0x00010000
	mbTopmost       = 0x00040000
	idYes           = 6
	idNo            = 7
	idTimeout       = 32000
)

// SessionUser is DOMAIN\user signed in on a Windows session, "" when nobody is (the sign-in screen).
func SessionUser(session uint32) string {
	user := querySessionString(session, wtsUserName)
	if user == "" {
		return ""
	}
	if domain := querySessionString(session, wtsDomainName); domain != "" {
		return domain + `\` + user
	}
	return user
}

func querySessionString(session uint32, class uintptr) string {
	var buffer *uint16
	var bytes uint32
	ok, _, _ := procWTSQuerySessionInformationW.Call(0, uintptr(session), class, uintptr(unsafe.Pointer(&buffer)), uintptr(unsafe.Pointer(&bytes)))
	if ok == 0 || buffer == nil {
		return ""
	}
	defer windows.WTSFreeMemory(uintptr(unsafe.Pointer(buffer)))
	return windows.UTF16PtrToString(buffer)
}

// AskConsent shows the consent prompt on a Windows session and waits for the answer or the timeout (0.8.0): the window of Fleeto, drawn
// by "fleeto-agent remote-consent" as the user signed in on the session, and the plain message box of Windows when that process cannot
// start or show the window. Deny is the default in both, so a stray Enter never allows a session.
func AskConsent(ctx context.Context, session uint32, technician string, texts ConsentTexts, timeout time.Duration) (ConsentAnswer, error) {
	texts = texts.Effective()
	answer, err := askConsentWindow(ctx, session, technician, texts, timeout)
	if err == nil || ctx.Err() != nil {
		return answer, err
	}
	consentLog().Warn("the consent window could not be shown; asking with the message box of Windows", "session", session, "error", err)
	return askConsentMessageBox(session, technician, texts, timeout)
}

// askConsentWindow starts the consent process in the Windows session and waits for its answer.
func askConsentWindow(ctx context.Context, session uint32, technician string, texts ConsentTexts, timeout time.Duration) (ConsentAnswer, error) {
	child, err := spawnAsSessionUser(slog.New(slog.DiscardHandler), session, ConsentCommand, "remote consent")
	if err != nil {
		return ConsentRefused, err
	}
	defer child.Close()
	return askConsentChild(ctx, child, ConsentAskBody{Technician: technician, Seconds: int(timeout / time.Second), Texts: texts}, timeout)
}

// askConsentMessageBox asks with the message box of Windows (WTSSendMessage), shown by the service in any session. It has only Yes and
// No, so the question holds the technician, the message of the policy and what happens without an answer.
func askConsentMessageBox(session uint32, technician string, texts ConsentTexts, timeout time.Duration) (ConsentAnswer, error) {
	title := texts.Title
	seconds := int(timeout / time.Second)
	message := ConsentPlainText(technician, texts, seconds)
	titleUTF16, err := windows.UTF16FromString(title)
	if err != nil {
		return ConsentRefused, err
	}
	messageUTF16, err := windows.UTF16FromString(message)
	if err != nil {
		return ConsentRefused, err
	}
	var response uint32
	ok, _, callErr := procWTSSendMessageW.Call(0, uintptr(session),
		uintptr(unsafe.Pointer(&titleUTF16[0])), uintptr((len(titleUTF16)-1)*2),
		uintptr(unsafe.Pointer(&messageUTF16[0])), uintptr((len(messageUTF16)-1)*2),
		mbYesNo|mbIconQuestion|mbDefButton2|mbSetForeground|mbTopmost, uintptr(seconds), uintptr(unsafe.Pointer(&response)), 1)
	if ok == 0 {
		return ConsentRefused, fmtCallError("WTSSendMessage", callErr)
	}
	switch response {
	case idYes:
		return ConsentAllowed, nil
	case idNo:
		return ConsentRefused, nil
	case idTimeout:
		return ConsentTimedOut, nil
	default:
		return ConsentRefused, fmt.Errorf("the prompt closed without an answer (%d)", response)
	}
}

// StageFolder creates the folder for files pasted into a session inside the staging root (PrepareStaging), with its access list in the same
// step: SYSTEM and administrators in full, the user signed in on the Windows session reading only. Without a signed-in user only SYSTEM
// and administrators have access.
func StageFolder(dir string, session uint32) error {
	sddl := rootSDDL
	var token windows.Token
	if err := windows.WTSQueryUserToken(session, &token); err == nil {
		user, err := token.GetTokenUser()
		token.Close()
		if err != nil {
			return fmt.Errorf("read the user of Windows session %d: %w", session, err)
		}
		sddl += fmt.Sprintf("(A;OICI;GRGX;;;%s)", user.User.Sid.String())
	}
	return createFolder(dir, sddl)
}
