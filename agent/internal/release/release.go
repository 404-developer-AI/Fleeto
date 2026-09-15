// Package release verifies the Steaan release manifest an instance offers and compares versions (0.2.1).
//
// The manifest is the manifest.json of a GitHub release, signed with the offline Steaan release key: a raw ed25519 signature over the
// exact bytes, the same signature install.sh verifies. The agent trusts only the release public keys compiled into its own binary, so a
// compromised instance or gateway can offer nothing that was not released by Steaan. Versions follow the rules of the server
// (Fleetify.Core SemanticVersion) and install.sh: a pre-release orders below its release.
package release

import (
	"crypto/ed25519"
	"encoding/json"
	"errors"
	"fmt"
	"regexp"
	"strconv"
	"strings"
)

// MaxBinaryBytes is the largest binary a manifest may list.
const MaxBinaryBytes int64 = 128 * 1024 * 1024

// MaxManifestBytes is the largest manifest the agent parses.
const MaxManifestBytes = 256 * 1024

// Components named in a manifest.
const (
	ComponentAgent    = "agent"
	ComponentWatchdog = "watchdog"
)

// Binary is one agent binary of a release.
type Binary struct {
	Component    string `json:"component"`
	Platform     string `json:"platform"`
	Architecture string `json:"architecture"`
	File         string `json:"file"`
	SHA256       string `json:"sha256"`
	Size         int64  `json:"size"`
}

// Manifest is the verified part of a release manifest the agent uses.
type Manifest struct {
	Version  string
	Binaries []Binary
}

var (
	// ErrNoKeys means the binary was built without release public keys (a development build): it never installs updates.
	ErrNoKeys = errors.New("this build has no release public keys, so it cannot verify releases")
	// ErrSignature means the manifest is not signed by a trusted release key.
	ErrSignature = errors.New("the release manifest is not signed with a trusted Steaan release key")

	versionPattern = regexp.MustCompile(`^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$`)
	filePattern    = regexp.MustCompile(`^[a-z0-9]{1,16}-[a-z0-9]{1,16}/fleetify-(agent|watchdog)(\.exe)?$`)
	shaPattern     = regexp.MustCompile(`^[0-9a-f]{64}$`)
)

// Verify checks the signature against keys and parses the manifest. It never returns a manifest whose signature did not verify.
func Verify(manifest, signature []byte, keys []ed25519.PublicKey) (*Manifest, error) {
	if len(keys) == 0 {
		return nil, ErrNoKeys
	}
	if len(manifest) == 0 || len(manifest) > MaxManifestBytes || len(signature) != ed25519.SignatureSize {
		return nil, ErrSignature
	}
	verified := false
	for _, key := range keys {
		if len(key) == ed25519.PublicKeySize && ed25519.Verify(key, manifest, signature) {
			verified = true
			break
		}
	}
	if !verified {
		return nil, ErrSignature
	}
	return Parse(manifest)
}

// Parse reads a manifest that was already verified. Exported for tests and tooling; the agent always goes through Verify.
func Parse(manifest []byte) (*Manifest, error) {
	var raw struct {
		FormatVersion *int      `json:"formatVersion"`
		Version       string    `json:"version"`
		AgentBinaries []*Binary `json:"agentBinaries"`
	}
	if err := json.Unmarshal(manifest, &raw); err != nil {
		return nil, fmt.Errorf("the release manifest is not valid JSON: %w", err)
	}
	if raw.FormatVersion == nil || *raw.FormatVersion != 1 {
		return nil, errors.New("the release manifest has an unsupported format")
	}
	if _, ok := ParseVersion(raw.Version); !ok {
		return nil, errors.New("the release manifest has no valid version")
	}
	if len(raw.AgentBinaries) > 32 {
		return nil, errors.New("the release manifest lists too many agent binaries")
	}
	m := &Manifest{Version: raw.Version}
	seen := map[string]bool{}
	for _, b := range raw.AgentBinaries {
		if b == nil || (b.Component != ComponentAgent && b.Component != ComponentWatchdog) || !filePattern.MatchString(b.File) ||
			!shaPattern.MatchString(b.SHA256) || b.Size <= 0 || b.Size > MaxBinaryBytes || b.File != ExpectedFile(b.Component, b.Platform, b.Architecture) {
			return nil, errors.New("an agent binary in the release manifest is invalid")
		}
		key := b.Component + "/" + b.Platform + "/" + b.Architecture
		if seen[key] {
			return nil, errors.New("an agent binary is listed twice in the release manifest")
		}
		seen[key] = true
		m.Binaries = append(m.Binaries, *b)
	}
	return m, nil
}

// ExpectedFile is the only file name a binary may have: <platform>-<architecture>/fleetify-<component>[.exe].
func ExpectedFile(component, platform, architecture string) string {
	suffix := ""
	if platform == "windows" {
		suffix = ".exe"
	}
	return platform + "-" + architecture + "/fleetify-" + component + suffix
}

// Binary returns the binary of component for platform and architecture (runtime.GOOS and runtime.GOARCH).
func (m *Manifest) Binary(component, platform, architecture string) (Binary, bool) {
	for _, b := range m.Binaries {
		if b.Component == component && b.Platform == platform && b.Architecture == architecture {
			return b, true
		}
	}
	return Binary{}, false
}

// Version is a parsed semantic version.
type Version struct {
	Major, Minor, Patch int
	PreRelease          string
}

// ParseVersion parses MAJOR.MINOR.PATCH[-PRERELEASE][+BUILD]; build metadata is ignored.
func ParseVersion(s string) (Version, bool) {
	if len(s) == 0 || len(s) > 50 {
		return Version{}, false
	}
	m := versionPattern.FindStringSubmatch(s)
	if m == nil {
		return Version{}, false
	}
	major, _ := strconv.Atoi(m[1])
	minor, _ := strconv.Atoi(m[2])
	patch, _ := strconv.Atoi(m[3])
	return Version{Major: major, Minor: minor, Patch: patch, PreRelease: m[4]}, true
}

// Compare returns -1, 0 or 1. A version without pre-release is higher than the same version with one; pre-release identifiers
// compare numerically when both are numbers, a numeric identifier is lower than a text one, and a shorter list is lower.
func Compare(a, b Version) int {
	for _, pair := range [][2]int{{a.Major, b.Major}, {a.Minor, b.Minor}, {a.Patch, b.Patch}} {
		if pair[0] != pair[1] {
			return sign(pair[0] - pair[1])
		}
	}
	switch {
	case a.PreRelease == b.PreRelease:
		return 0
	case a.PreRelease == "":
		return 1
	case b.PreRelease == "":
		return -1
	}
	left, right := strings.Split(a.PreRelease, "."), strings.Split(b.PreRelease, ".")
	for i := 0; i < len(left) && i < len(right); i++ {
		ln, lerr := strconv.ParseUint(left[i], 10, 63)
		rn, rerr := strconv.ParseUint(right[i], 10, 63)
		var c int
		switch {
		case lerr == nil && rerr == nil:
			c = compareUint(ln, rn)
		case lerr == nil:
			c = -1
		case rerr == nil:
			c = 1
		default:
			c = strings.Compare(left[i], right[i])
		}
		if c != 0 {
			return c
		}
	}
	return sign(len(left) - len(right))
}

// Newer reports whether candidate is a valid version strictly higher than installed. An invalid installed version (a development
// build such as 0.0.0-dev counts as valid) is never replaced: the agent cannot tell whether that would be a downgrade.
func Newer(candidate, installed string) bool {
	c, ok := ParseVersion(candidate)
	if !ok {
		return false
	}
	i, ok := ParseVersion(installed)
	if !ok {
		return false
	}
	return Compare(c, i) > 0
}

func sign(v int) int {
	switch {
	case v < 0:
		return -1
	case v > 0:
		return 1
	default:
		return 0
	}
}

func compareUint(a, b uint64) int {
	switch {
	case a < b:
		return -1
	case a > b:
		return 1
	default:
		return 0
	}
}
