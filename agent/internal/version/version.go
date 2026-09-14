// Package version holds build-time values injected with -ldflags -X.
package version

import (
	"crypto/ed25519"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"fmt"
	"strings"
)

// Version is the release version, e.g. "0.1.0". Server and agent share one version number.
var Version = "0.0.0-dev"

// ReleasePublicKeys holds the Steaan release public keys (current and standby) as base64 raw ed25519 keys separated
// by ';'. Self-updates are installed only with a valid signature from one of these keys. Empty in development builds.
var ReleasePublicKeys = ""

// ParseReleasePublicKeys decodes ReleasePublicKeys. An invalid entry is an error: a release build with a broken key
// list must be noticed, never silently trust fewer keys than intended.
func ParseReleasePublicKeys(value string) ([]ed25519.PublicKey, error) {
	var keys []ed25519.PublicKey
	for _, part := range strings.Split(value, ";") {
		part = strings.TrimSpace(part)
		if part == "" {
			continue
		}
		raw, err := base64.StdEncoding.DecodeString(part)
		if err != nil {
			return nil, fmt.Errorf("release public key is not valid base64: %w", err)
		}
		if len(raw) != ed25519.PublicKeySize {
			return nil, fmt.Errorf("release public key has %d bytes, expected %d", len(raw), ed25519.PublicKeySize)
		}
		keys = append(keys, ed25519.PublicKey(raw))
	}
	return keys, nil
}

// KeyID returns the first 16 lowercase hex characters of SHA-256 over the key, matching KeyIds.For on the server.
func KeyID(key []byte) string {
	sum := sha256.Sum256(key)
	return hex.EncodeToString(sum[:])[:16]
}
