package release

import (
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"strings"
	"testing"
)

const validManifest = `{
  "formatVersion": 1,
  "version": "0.2.1",
  "images": {"web": "sha256:00"},
  "agentBinaries": [
    {"component": "agent", "platform": "windows", "architecture": "amd64", "file": "windows-amd64/fleetify-agent.exe",
     "sha256": "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", "size": 1234},
    {"component": "watchdog", "platform": "windows", "architecture": "amd64", "file": "windows-amd64/fleetify-watchdog.exe",
     "sha256": "60303ae22b998861bce3b28f33eec1be758a213c86c93c076dbe9f558c11c752", "size": 999}
  ]
}`

func TestVerifyAcceptsOnlyTrustedSignatures(t *testing.T) {
	pub, priv, _ := ed25519.GenerateKey(rand.Reader)
	otherPub, otherPriv, _ := ed25519.GenerateKey(rand.Reader)
	manifest := []byte(validManifest)
	sig := ed25519.Sign(priv, manifest)

	m, err := Verify(manifest, sig, []ed25519.PublicKey{otherPub, pub})
	if err != nil {
		t.Fatalf("a manifest signed with a trusted key was refused: %v", err)
	}
	if m.Version != "0.2.1" || len(m.Binaries) != 2 {
		t.Fatalf("unexpected manifest %+v", m)
	}
	if b, ok := m.Binary(ComponentWatchdog, "windows", "amd64"); !ok || b.Size != 999 {
		t.Fatalf("watchdog binary not found: %+v", b)
	}
	if _, ok := m.Binary(ComponentAgent, "linux", "arm64"); ok {
		t.Fatal("a binary for another platform was returned")
	}

	if _, err := Verify(manifest, ed25519.Sign(otherPriv, manifest), []ed25519.PublicKey{pub}); !errors.Is(err, ErrSignature) {
		t.Fatalf("a manifest signed with an untrusted key was accepted: %v", err)
	}
	tampered := []byte(strings.Replace(validManifest, "0.2.1", "0.2.9", 1))
	if _, err := Verify(tampered, sig, []ed25519.PublicKey{pub}); !errors.Is(err, ErrSignature) {
		t.Fatalf("a changed manifest was accepted: %v", err)
	}
	if _, err := Verify(manifest, sig, nil); !errors.Is(err, ErrNoKeys) {
		t.Fatalf("a build without release keys verified a manifest: %v", err)
	}
	if _, err := Verify(manifest, sig[:10], []ed25519.PublicKey{pub}); !errors.Is(err, ErrSignature) {
		t.Fatalf("a short signature was accepted: %v", err)
	}
}

func TestParseRefusesInvalidBinaries(t *testing.T) {
	cases := map[string]string{
		"path traversal":    `"file": "windows-amd64/../../evil.exe"`,
		"other name":        `"file": "windows-amd64/fleetify-other.exe"`,
		"platform mismatch": `"file": "linux-amd64/fleetify-agent"`,
	}
	for name, replacement := range cases {
		t.Run(name, func(t *testing.T) {
			bad := strings.Replace(validManifest, `"file": "windows-amd64/fleetify-agent.exe"`, replacement, 1)
			if _, err := Parse([]byte(bad)); err == nil {
				t.Fatal("an invalid binary was accepted")
			}
		})
	}
	for name, bad := range map[string]string{
		"format":       strings.Replace(validManifest, `"formatVersion": 1`, `"formatVersion": 2`, 1),
		"version":      strings.Replace(validManifest, `"version": "0.2.1"`, `"version": "latest"`, 1),
		"sha":          strings.Replace(validManifest, "9f86d081", "XYZd081", 1),
		"size":         strings.Replace(validManifest, `"size": 1234`, `"size": 999999999999`, 1),
		"duplicate":    strings.Replace(validManifest, `"component": "watchdog", "platform": "windows", "architecture": "amd64", "file": "windows-amd64/fleetify-watchdog.exe"`, `"component": "agent", "platform": "windows", "architecture": "amd64", "file": "windows-amd64/fleetify-agent.exe"`, 1),
		"not json":     `{`,
		"missing file": strings.Replace(validManifest, `"file": "windows-amd64/fleetify-agent.exe",`, "", 1),
	} {
		t.Run(name, func(t *testing.T) {
			if _, err := Parse([]byte(bad)); err == nil {
				t.Fatal("an invalid manifest was accepted")
			}
		})
	}
}

func TestVersionOrderMatchesTheServer(t *testing.T) {
	ordered := []string{"0.1.0", "0.2.0-alpha.1", "0.2.0-alpha.2", "0.2.0-alpha.10", "0.2.0-beta", "0.2.0", "0.2.1", "0.10.0", "1.0.0"}
	for i := 0; i < len(ordered)-1; i++ {
		if !Newer(ordered[i+1], ordered[i]) || Newer(ordered[i], ordered[i+1]) {
			t.Errorf("%s should be newer than %s", ordered[i+1], ordered[i])
		}
	}
	if Newer("0.2.1", "0.2.1") {
		t.Error("an equal version is not newer")
	}
	if Newer("0.2.1+build", "0.2.1") {
		t.Error("build metadata must not make a version newer")
	}
	if Newer("0.3.0", "unknown") || Newer("garbage", "0.1.0") {
		t.Error("an invalid version must never lead to an update")
	}
	if !Newer("0.2.1", "0.0.0-dev") {
		t.Error("a development build is older than any release")
	}
}
