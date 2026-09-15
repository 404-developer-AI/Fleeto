package jobs

import (
	"bytes"
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"os"
	"os/exec"
	"strings"
	"sync"
	"time"
	"unicode"
	"unicode/utf8"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

const (
	// MaxCheckScriptTimeout is the longest a script check runs, whatever the configuration says.
	MaxCheckScriptTimeout = 5 * time.Minute
	// MinCheckScriptTimeout is the shortest timeout a script check gets.
	MinCheckScriptTimeout = 5 * time.Second
	// maxCheckOutput is how much output of a script check is kept in memory; only its first line is reported.
	maxCheckOutput = 64 * 1024
	// maxDetailRunes bounds the detail reported for a script check.
	maxDetailRunes = 200
)

// checkScriptSlots bounds how many script checks run at the same time, so many checks with the same interval do not start a
// crowd of interpreters.
var checkScriptSlots = make(chan struct{}, 2)

// CheckScriptResult is the outcome of one script check run. Error is set when the script could not run or did not finish.
type CheckScriptResult struct {
	ExitCode int
	Detail   string
	Error    string
}

// VerifyCheckScript checks the script of a script check from the verified signed configuration: its body has the signed hash and
// its language runs on this operating system.
func VerifyCheckScript(script *agentv1.ScriptJob) error {
	if script == nil || script.GetBody() == "" {
		return errors.New("the configuration carries no script for this check")
	}
	if len(script.GetBody()) > MaxPayloadBytes {
		return errors.New("the script of this check is too large")
	}
	sum := sha256.Sum256([]byte(script.GetBody()))
	if !strings.EqualFold(hex.EncodeToString(sum[:]), script.GetSha256()) {
		return errors.New("the script body does not match its signed hash")
	}
	if !LanguageRunsHere(script.GetLanguage()) {
		return fmt.Errorf("%s scripts do not run on this operating system", LanguageName(script.GetLanguage()))
	}
	return nil
}

// RunCheckScript runs a verified check script in a new protected directory under baseDir and removes it afterwards. The exit code
// is the check's value; the first line of output (stdout, or stderr when stdout is empty) is its detail.
func RunCheckScript(ctx context.Context, baseDir string, access platform.Access, checkID string, script *agentv1.ScriptJob,
	timeout time.Duration) CheckScriptResult {
	if err := VerifyCheckScript(script); err != nil {
		return CheckScriptResult{Error: err.Error()}
	}
	timeout = min(max(timeout, MinCheckScriptTimeout), MaxCheckScriptTimeout)

	select {
	case checkScriptSlots <- struct{}{}:
		defer func() { <-checkScriptSlots }()
	case <-ctx.Done():
		return CheckScriptResult{Error: "the agent stopped before the script could run"}
	}

	if err := platform.EnsureProtectedDir(baseDir, access); err != nil {
		return CheckScriptResult{Error: "could not prepare the script directory: " + err.Error()}
	}
	dir, err := os.MkdirTemp(baseDir, "run-")
	if err != nil {
		return CheckScriptResult{Error: "could not prepare the script directory: " + err.Error()}
	}
	defer os.RemoveAll(dir)
	scriptPath, err := writeScript(dir, script, access)
	if err != nil {
		return CheckScriptResult{Error: "could not write the script: " + err.Error()}
	}

	runCtx, cancel := context.WithTimeout(ctx, timeout)
	defer cancel()
	cmd, tree, err := command(runCtx, script.GetLanguage(), scriptPath, dir)
	if err != nil {
		return CheckScriptResult{Error: err.Error()}
	}
	defer tree.close()
	stdout := &limitedBuffer{limit: maxCheckOutput}
	stderr := &limitedBuffer{limit: maxCheckOutput}
	cmd.Stdout = stdout
	cmd.Stderr = stderr
	cmd.Env = append(os.Environ(), "FLEETO_CHECK_ID="+checkID)
	cmd.WaitDelay = 5 * time.Second
	cmd.Cancel = tree.kill
	if err := cmd.Start(); err != nil {
		return CheckScriptResult{Error: fmt.Sprintf("could not start %s: %v", LanguageName(script.GetLanguage()), err)}
	}
	tree.attach(cmd)
	waitErr := cmd.Wait()

	detail := FirstLine(stdout.Bytes())
	if detail == "" {
		detail = FirstLine(stderr.Bytes())
	}
	var exitErr *exec.ExitError
	switch {
	case ctx.Err() != nil:
		return CheckScriptResult{Error: "the agent stopped while the script ran"}
	case errors.Is(runCtx.Err(), context.DeadlineExceeded):
		return CheckScriptResult{Error: fmt.Sprintf("the script did not finish within %s and was ended", timeout), Detail: detail}
	case waitErr == nil:
		return CheckScriptResult{ExitCode: 0, Detail: detail}
	case errors.As(waitErr, &exitErr):
		return CheckScriptResult{ExitCode: exitErr.ExitCode(), Detail: detail}
	case cmd.ProcessState != nil && cmd.ProcessState.Exited():
		// A child process kept the output open; the script itself ended.
		return CheckScriptResult{ExitCode: cmd.ProcessState.ExitCode(), Detail: detail}
	default:
		return CheckScriptResult{Error: waitErr.Error(), Detail: detail}
	}
}

// FirstLine returns the first non-empty line of output as valid UTF-8 without control characters, at most 200 characters.
func FirstLine(output []byte) string {
	text := strings.ToValidUTF8(string(bytes.TrimPrefix(output, []byte{0xEF, 0xBB, 0xBF})), "�")
	for _, line := range strings.FieldsFunc(text, func(r rune) bool { return r == '\n' || r == '\r' }) {
		line = strings.Map(func(r rune) rune {
			if unicode.IsControl(r) {
				return ' '
			}
			return r
		}, line)
		line = strings.TrimSpace(line)
		if line == "" {
			continue
		}
		if utf8.RuneCountInString(line) > maxDetailRunes {
			line = string([]rune(line)[:maxDetailRunes])
		}
		return line
	}
	return ""
}

// limitedBuffer keeps the first limit bytes written and drops the rest without failing the writer.
type limitedBuffer struct {
	mu    sync.Mutex
	buf   bytes.Buffer
	limit int
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	if room := b.limit - b.buf.Len(); room > 0 {
		b.buf.Write(p[:min(len(p), room)])
	}
	return len(p), nil
}

func (b *limitedBuffer) Bytes() []byte {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]byte(nil), b.buf.Bytes()...)
}
