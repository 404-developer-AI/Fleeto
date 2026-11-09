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
	// DefaultCheckOutputLimit is how much output of a script check is reported without "output_limit_kb" (0.9.0).
	DefaultCheckOutputLimit = 8 * 1024
	// MinCheckOutputLimit and MaxCheckOutputLimit bound "output_limit_kb", whatever the configuration says.
	MinCheckOutputLimit = 1024
	MaxCheckOutputLimit = 256 * 1024
	// checkWaitDelay is how long the output of a check script may stay open after the script itself ended.
	checkWaitDelay = 5 * time.Second
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
	// Output is what the script wrote, up to the output limit; nil when the script did not start.
	Output *agentv1.ScriptOutput
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
// is the check's value; the first line of output (stdout, or stderr when stdout is empty) is its detail, and the output up to
// limitBytes is reported with it (0.9.0).
func RunCheckScript(ctx context.Context, baseDir string, access platform.Access, checkID string, script *agentv1.ScriptJob,
	timeout time.Duration, limitBytes int) CheckScriptResult {
	if err := VerifyCheckScript(script); err != nil {
		return CheckScriptResult{Error: err.Error()}
	}
	timeout = min(max(timeout, MinCheckScriptTimeout), MaxCheckScriptTimeout)
	limitBytes = min(max(limitBytes, MinCheckOutputLimit), MaxCheckOutputLimit)

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
	// Each stream keeps up to the limit, because stderr gets the whole limit when stdout is empty.
	stdout := &limitedBuffer{limit: limitBytes}
	stderr := &limitedBuffer{limit: limitBytes}
	// A script check comes from the signed configuration and always runs as the agent's own account, never as a user.
	proc, tree, err := command(runCtx, commandOptions{
		language:   script.GetLanguage(),
		scriptPath: scriptPath,
		dir:        dir,
		env:        []string{"FLEETO_CHECK_ID=" + checkID},
		stdout:     stdout,
		stderr:     stderr,
		wait:       checkWaitDelay,
	})
	if err != nil {
		return CheckScriptResult{Error: err.Error()}
	}
	defer tree.close()
	if err := proc.Start(); err != nil {
		return CheckScriptResult{Error: fmt.Sprintf("could not start %s: %v", LanguageName(script.GetLanguage()), err)}
	}
	tree.attach(proc.Pid())
	waitErr := proc.Wait()

	detail := FirstLine(stdout.Bytes())
	if detail == "" {
		detail = FirstLine(stderr.Bytes())
	}
	output := ScriptOutput(stdout, stderr, limitBytes)
	var exitErr *exec.ExitError
	switch {
	case ctx.Err() != nil:
		return CheckScriptResult{Error: "the agent stopped while the script ran"}
	case errors.Is(runCtx.Err(), context.DeadlineExceeded):
		return CheckScriptResult{Error: fmt.Sprintf("the script did not finish within %s and was ended", timeout), Detail: detail,
			Output: output}
	case waitErr == nil, errors.As(waitErr, &exitErr):
		return CheckScriptResult{ExitCode: proc.ExitCode(), Detail: detail, Output: output}
	case errors.Is(waitErr, exec.ErrWaitDelay):
		// A child process kept the output open; the script itself ended.
		return CheckScriptResult{ExitCode: proc.ExitCode(), Detail: detail, Output: output}
	default:
		return CheckScriptResult{Error: waitErr.Error(), Detail: detail, Output: output}
	}
}

// ScriptOutput is the reported output of a script check: stdout first, then stderr in what is left of limitBytes, each valid
// UTF-8 without NUL characters and cut on a character boundary. TotalBytes counts everything the script wrote.
func ScriptOutput(stdout, stderr *limitedBuffer, limitBytes int) *agentv1.ScriptOutput {
	out := cleanOutput(stdout.Bytes(), limitBytes)
	errOut := cleanOutput(stderr.Bytes(), limitBytes-len(out))
	return &agentv1.ScriptOutput{
		Stdout:     out,
		Stderr:     errOut,
		TotalBytes: stdout.Total() + stderr.Total(),
		LimitBytes: uint64(limitBytes),
	}
}

// cleanOutput makes output safe to send: no byte order mark, valid UTF-8, no NUL characters, at most limit bytes.
func cleanOutput(output []byte, limit int) string {
	if limit <= 0 {
		return ""
	}
	text := strings.ToValidUTF8(string(bytes.TrimPrefix(output, []byte{0xEF, 0xBB, 0xBF})), "�")
	text = strings.ReplaceAll(text, "\x00", "")
	if len(text) > limit {
		text = text[:limit]
		// The cut can split a character; drop its first bytes.
		for len(text) > 0 && !utf8.ValidString(text) {
			text = text[:len(text)-1]
		}
	}
	return text
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

// limitedBuffer keeps the first limit bytes written and drops the rest without failing the writer; it counts every byte.
type limitedBuffer struct {
	mu    sync.Mutex
	buf   bytes.Buffer
	limit int
	total uint64
}

func (b *limitedBuffer) Write(p []byte) (int, error) {
	b.mu.Lock()
	defer b.mu.Unlock()
	b.total += uint64(len(p))
	if room := b.limit - b.buf.Len(); room > 0 {
		b.buf.Write(p[:min(len(p), room)])
	}
	return len(p), nil
}

// Total is the number of bytes written, also those beyond the limit.
func (b *limitedBuffer) Total() uint64 {
	b.mu.Lock()
	defer b.mu.Unlock()
	return b.total
}

func (b *limitedBuffer) Bytes() []byte {
	b.mu.Lock()
	defer b.mu.Unlock()
	return append([]byte(nil), b.buf.Bytes()...)
}
