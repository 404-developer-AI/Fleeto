package jobs

import (
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"runtime"
	"strings"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// MaxInteractiveBody is the largest script body a remote background session may run, the limit of the script library.
const MaxInteractiveBody = 256 * 1024

// Interactive is a library script staged to run in a terminal of a remote background session (0.6.0): the technician sees its output as
// it comes and answers its prompts. It is not a job: nothing is signed or spooled, and the session that asked for it is what the audit log
// records. The body is checked against its hash and language like a job's, so a damaged body never runs.
type Interactive struct {
	// Path is the staged script.
	Path string
	// Dir is the working directory of the run.
	Dir string
	// Account is the user the script runs as, empty for SYSTEM or root.
	Account  string
	Language agentv1.ScriptLanguage
	session  *signedInUser
	release  func()
}

// StageInteractive writes the script where the account that runs it can read it: a new directory under dir, protected like the state of
// the service, for SYSTEM or root, or the staging directory of a job for the signed-in user (userID names one, empty takes whoever is
// signed in, the console first). Release removes it.
func StageInteractive(script *agentv1.ScriptJob, dir string, access platform.Access, asUser bool, userID string) (*Interactive, error) {
	body := script.GetBody()
	if body == "" || len(body) > MaxInteractiveBody || strings.ContainsRune(body, 0) {
		return nil, errors.New("the script is empty or larger than the script library allows")
	}
	sum := sha256.Sum256([]byte(body))
	if !strings.EqualFold(hex.EncodeToString(sum[:]), script.GetSha256()) {
		return nil, errors.New("the script does not match its hash; open the script from the library again")
	}
	if !LanguageRunsHere(script.GetLanguage()) {
		return nil, fmt.Errorf("%s scripts do not run on %s", LanguageName(script.GetLanguage()), runtime.GOOS)
	}

	if asUser {
		session, err := signedInSession(userID)
		if err != nil {
			return nil, err
		}
		path, work, release, err := session.stage(script)
		if err != nil {
			session.close()
			return nil, err
		}
		return &Interactive{Path: path, Dir: work, Account: session.accountName(), Language: script.GetLanguage(), session: session,
			release: release}, nil
	}

	if err := platform.EnsureProtectedDir(dir, access); err != nil {
		return nil, err
	}
	work, err := os.MkdirTemp(dir, "script-")
	if err != nil {
		return nil, err
	}
	if err := platform.EnsureProtectedDir(work, access); err != nil {
		_ = os.RemoveAll(work)
		return nil, err
	}
	path, err := writeScript(work, script, access)
	if err != nil {
		_ = os.RemoveAll(work)
		return nil, err
	}
	return &Interactive{Path: path, Dir: work, Language: script.GetLanguage(), release: func() { _ = os.RemoveAll(work) }}, nil
}

// Release closes the user's token and removes the staged script. Call it once the script has ended.
func (s *Interactive) Release() {
	if s.session != nil {
		s.session.close()
		s.session = nil
	}
	if s.release != nil {
		s.release()
		s.release = nil
	}
}
