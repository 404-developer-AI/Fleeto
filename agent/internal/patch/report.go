// Package patch reads which updates are missing on the endpoint (0.9.0, PATCHING.md section 4) and reports them. A scan never
// installs or configures anything.
package patch

import (
	"cmp"
	"context"
	"crypto/rand"
	"fmt"
	"runtime"
	"slices"
	"strings"
	"unicode/utf8"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

const (
	// MaxUpdates bounds the updates in one report; the server keeps the same limit.
	MaxUpdates = 2000

	maxNameLen      = 200
	maxVersionLen   = 200
	maxOriginLen    = 100
	maxAdvisoryLen  = 100
	maxSeverityLen  = 20
	maxProblemLen   = 1000
	maxRequestIDLen = 200

	classificationSecurity = "security"
	classificationOther    = "other"

	// maxOutputBytes bounds what the agent keeps of the output of one command.
	maxOutputBytes = 16 << 20
)

// cmdResult is the outcome of one command.
type cmdResult struct {
	Stdout string
	Stderr string
	// Code is the exit code; -1 when the command did not start or was stopped.
	Code int
	// Err is set when the command did not start, or was stopped by the context.
	Err error
}

// system is what a scan needs of the endpoint, so the engines run on captured output in tests.
type system interface {
	// Run runs a fixed command with an argument vector, never through a shell. env is added to the environment of the agent.
	Run(ctx context.Context, env []string, name string, args ...string) cmdResult
	// LookPath reports whether a command is installed.
	LookPath(name string) bool
	// Exists reports whether a file exists.
	Exists(path string) bool
	// ReadDirNames lists the names in a directory; nil when it cannot be read.
	ReadDirNames(path string) []string
}

// problems collects what went wrong in one scan, once each, in the order it happened.
type problems struct {
	items []string
}

func (p *problems) add(message string) {
	message = strings.TrimSpace(message)
	if message == "" || slices.Contains(p.items, message) {
		return
	}
	p.items = append(p.items, message)
}

func (p *problems) String() string { return strings.Join(p.items, " ") }

// cut makes s valid UTF-8, removes control characters and keeps at most limit bytes, ending on a whole character.
func cut(s string, limit int) string {
	s = strings.ToValidUTF8(s, "")
	s = strings.Map(func(r rune) rune {
		if r < 0x20 || r == 0x7f {
			return ' '
		}
		return r
	}, s)
	s = strings.TrimSpace(s)
	if len(s) <= limit {
		return s
	}
	end := limit
	for end > 0 && !utf8.RuneStart(s[end]) {
		end--
	}
	return s[:end]
}

// joinProblem puts first before rest.
func joinProblem(first, rest string) string {
	if rest == "" {
		return first
	}
	return first + " " + rest
}

// Finalize makes a report safe to send: a valid scan id, valid and bounded strings, and at most MaxUpdates updates, security
// updates first, so a cut drops the less important ones.
func Finalize(r *agentv1.PatchScanReport) {
	if !scanIDPattern.MatchString(r.GetScanId()) {
		r.ScanId = newUUID()
	}
	r.Engine = cut(r.GetEngine(), 20)
	r.Problem = cut(r.GetProblem(), maxProblemLen)
	r.RequestId = cut(r.GetRequestId(), maxRequestIDLen)
	updates := r.GetUpdates()[:0]
	for _, u := range r.GetUpdates() {
		if u == nil {
			continue
		}
		u.Name = cut(u.GetName(), maxNameLen)
		if u.GetName() == "" {
			continue
		}
		u.Id = cut(u.GetId(), maxNameLen+1+maxNameLen)
		u.Version = cut(u.GetVersion(), maxVersionLen)
		u.InstalledVersion = cut(u.GetInstalledVersion(), maxVersionLen)
		if u.GetClassification() != classificationSecurity {
			u.Classification = classificationOther
		}
		u.Origin = cut(u.GetOrigin(), maxOriginLen)
		u.Advisory = cut(u.GetAdvisory(), maxAdvisoryLen)
		u.Severity = cut(u.GetSeverity(), maxSeverityLen)
		updates = append(updates, u)
	}
	slices.SortFunc(updates, func(a, b *agentv1.PatchUpdate) int {
		aSec, bSec := a.GetClassification() == classificationSecurity, b.GetClassification() == classificationSecurity
		if aSec != bSec {
			if aSec {
				return -1
			}
			return 1
		}
		return cmp.Compare(a.GetId(), b.GetId())
	})
	updates = slices.CompactFunc(updates, func(a, b *agentv1.PatchUpdate) bool { return a.GetId() == b.GetId() })
	if len(updates) > MaxUpdates {
		updates = updates[:MaxUpdates]
		r.Truncated = true
	}
	r.Updates = updates
}

func newUUID() string {
	var b [16]byte
	_, _ = rand.Read(b[:])
	b[6] = b[6]&0x0f | 0x40
	b[8] = b[8]&0x3f | 0x80
	return fmt.Sprintf("%x-%x-%x-%x-%x", b[0:4], b[4:6], b[6:8], b[8:10], b[10:16])
}

// debianArch is the dpkg name of the architecture the agent was built for, used when apt names none for a package.
func debianArch() string {
	switch runtime.GOARCH {
	case "386":
		return "i386"
	case "arm":
		return "armhf"
	default:
		return runtime.GOARCH
	}
}
