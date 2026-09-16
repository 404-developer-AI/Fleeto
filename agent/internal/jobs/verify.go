// Package jobs verifies and runs signed jobs (0.2.0): library scripts as SYSTEM or root, with their output kept on disk until
// the gateway acknowledged it.
//
// A job is the most powerful thing the server can send, so nothing runs unless the ed25519 signature over the exact payload
// verifies against the instance signing key pinned at enrollment, the job names this instance and endpoint, it is still valid
// (5 minutes clock tolerance), the endpoint's applied configuration is managed, the script body has the signed hash and the
// language runs on this operating system. A job id runs at most once.
package jobs

import (
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"runtime"
	"strings"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

const (
	// Context is the domain separation prefix; it must equal SignatureContexts.Job on the server.
	Context = "fleeto-job-v1"
	// MaxPayloadBytes bounds what the agent parses: a script of 256 KiB plus the envelope.
	MaxPayloadBytes = 512 * 1024
	// ClockTolerance is how far past ValidUntil a job is still accepted.
	ClockTolerance = 5 * time.Minute
	// MaxOutputBytes caps the output of one job whatever the payload says. The policy of the endpoint's site picks the
	// cap the signer puts in the payload; this is the ceiling the agent holds on its own.
	MaxOutputBytes = 200 * 1024 * 1024
	// DefaultOutputBytes applies when a payload names no cap at all.
	DefaultOutputBytes = 50 * 1024 * 1024
	// ChunkBytes is the size of a full output chunk.
	ChunkBytes = 64 * 1024
	maxTimeout = 24 * time.Hour
)

// minTimeout is a variable so tests can run a timeout in a second.
var minTimeout = 30 * time.Second

// ErrNotSigned means the payload could not even be read; there is no trustworthy job id to answer.
var ErrNotSigned = errors.New("the job cannot be read")

// PeekID returns the job id of a payload without verifying it, only to answer a refusal. Never used to decide anything.
func PeekID(sj *agentv1.SignedJob) string {
	if sj == nil || len(sj.GetPayload()) == 0 || len(sj.GetPayload()) > MaxPayloadBytes {
		return ""
	}
	var payload agentv1.JobPayload
	if err := (proto.UnmarshalOptions{DiscardUnknown: true}).Unmarshal(sj.GetPayload(), &payload); err != nil || !validID(payload.GetJobId()) {
		return ""
	}
	return strings.ToLower(payload.GetJobId())
}

// Verify checks a signed job against the pinned trust and returns the payload. managed is the tier of the applied configuration.
func Verify(sj *agentv1.SignedJob, trust signedconfig.Trust, managed bool, now time.Time) (*agentv1.JobPayload, error) {
	if sj == nil || len(sj.GetPayload()) == 0 || len(sj.GetPayload()) > MaxPayloadBytes {
		return nil, ErrNotSigned
	}
	if len(trust.SigningKey) != ed25519.PublicKeySize || trust.KeyID == "" {
		return nil, errors.New("the agent has no pinned instance signing key: enroll the agent again")
	}
	if sj.GetKeyId() != trust.KeyID {
		return nil, fmt.Errorf("the job is signed with key %q, but the pinned instance signing key is %q", sj.GetKeyId(), trust.KeyID)
	}
	if len(sj.GetSignature()) != ed25519.SignatureSize {
		return nil, errors.New("the job signature is invalid")
	}
	message := make([]byte, 0, len(Context)+1+len(sj.GetPayload()))
	message = append(message, Context...)
	message = append(message, 0)
	message = append(message, sj.GetPayload()...)
	if !ed25519.Verify(trust.SigningKey, message, sj.GetSignature()) {
		return nil, errors.New("the job signature does not verify against the pinned instance signing key")
	}

	var payload agentv1.JobPayload
	if err := (proto.UnmarshalOptions{DiscardUnknown: true}).Unmarshal(sj.GetPayload(), &payload); err != nil {
		return nil, fmt.Errorf("the signed job cannot be parsed: %w", err)
	}
	if !validID(payload.GetJobId()) {
		return nil, errors.New("the job has no valid id")
	}
	if !strings.EqualFold(payload.GetInstanceId(), trust.InstanceID) {
		return nil, fmt.Errorf("the job is for instance %q, not this agent's instance", payload.GetInstanceId())
	}
	if !strings.EqualFold(payload.GetEndpointId(), trust.EndpointID) {
		return nil, fmt.Errorf("the job is for endpoint %q, not this endpoint", payload.GetEndpointId())
	}
	if payload.GetValidUntil() == nil || now.After(payload.GetValidUntil().AsTime().Add(ClockTolerance)) {
		return nil, errors.New("the job expired before it could start")
	}
	if payload.GetValidUntil().AsTime().After(now.Add(7*24*time.Hour + 2*ClockTolerance)) {
		return nil, errors.New("the job is valid for longer than 7 days")
	}
	if !managed {
		return nil, errors.New("the endpoint is agent-only: jobs do not run")
	}
	if payload.GetType() != agentv1.JobType_JOB_TYPE_SCRIPT || payload.GetScript() == nil {
		return nil, errors.New("this agent does not know the job type; update the agent")
	}
	script := payload.GetScript()
	sum := sha256.Sum256([]byte(script.GetBody()))
	if !strings.EqualFold(hex.EncodeToString(sum[:]), script.GetSha256()) || script.GetBody() == "" {
		return nil, errors.New("the script body does not match its signed hash")
	}
	if !LanguageRunsHere(script.GetLanguage()) {
		return nil, fmt.Errorf("%s scripts do not run on %s", LanguageName(script.GetLanguage()), runtime.GOOS)
	}
	return &payload, nil
}

// LanguageRunsHere reports whether this operating system runs the language.
func LanguageRunsHere(language agentv1.ScriptLanguage) bool {
	switch language {
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL, agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH:
		return runtime.GOOS == "windows"
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL, agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BASH:
		return runtime.GOOS == "linux" || runtime.GOOS == "darwin"
	default:
		return false
	}
}

// LanguageName is a readable name for logs and refusals.
func LanguageName(language agentv1.ScriptLanguage) string {
	switch language {
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL:
		return "PowerShell"
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BATCH:
		return "Batch"
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_SHELL:
		return "sh"
	case agentv1.ScriptLanguage_SCRIPT_LANGUAGE_BASH:
		return "bash"
	default:
		return "Unknown"
	}
}

// Timeout clamps the signed timeout.
func Timeout(payload *agentv1.JobPayload) time.Duration {
	t := time.Duration(payload.GetTimeoutSeconds()) * time.Second
	return min(max(t, minTimeout), maxTimeout)
}

// OutputLimit clamps the signed output limit.
func OutputLimit(payload *agentv1.JobPayload) int64 {
	limit := int64(min(payload.GetMaxOutputBytes(), MaxOutputBytes))
	if limit <= 0 {
		return DefaultOutputBytes
	}
	return limit
}

// validID accepts a canonical GUID only, because the id becomes a directory name.
func validID(id string) bool {
	if len(id) != 36 {
		return false
	}
	for i, r := range id {
		switch i {
		case 8, 13, 18, 23:
			if r != '-' {
				return false
			}
		default:
			if !strings.ContainsRune("0123456789abcdefABCDEF", r) {
				return false
			}
		}
	}
	return true
}
