package service

import (
	"crypto/ed25519"
	"crypto/rand"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/testpki"
)

// An agent from before the rename to Fleeto is taken over only for the gateway and instance CA of the install command.
func TestLegacyAgentIsTakenOverOnlyForTheSameInstance(t *testing.T) {
	ca, err := testpki.NewCA("instance")
	if err != nil {
		t.Fatal(err)
	}
	other, err := testpki.NewCA("other instance")
	if err != nil {
		t.Fatal(err)
	}
	legacy := &state.State{
		Server:           "agents.rmm.example:443",
		EndpointID:       "0f5d8c7e-8c1a-4b7e-9d59-5f6a1d2c3b4a",
		CACertificatePEM: string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: ca.Cert.Raw})),
	}
	opts := InstallOptions{Server: "AGENTS.rmm.example:443", Token: "fet_unused", CAFingerprint: ca.Fingerprint()}

	const uninstall = `& 'C:\Program Files\Fleetify\Agent\fleetify-agent.exe' uninstall`
	if err := legacyTakeover(legacy, opts, uninstall); err != nil {
		t.Fatalf("the same instance must be taken over: %v", err)
	}

	cases := map[string]func() (*state.State, InstallOptions){
		"another gateway": func() (*state.State, InstallOptions) {
			o := opts
			o.Server = "agents.other.example:443"
			return legacy, o
		},
		"another instance CA": func() (*state.State, InstallOptions) {
			o := opts
			o.CAFingerprint = other.Fingerprint()
			return legacy, o
		},
		"a revoked agent": func() (*state.State, InstallOptions) {
			revoked := *legacy
			revoked.Revoked = true
			return &revoked, opts
		},
	}
	for name, build := range cases {
		st, o := build()
		err := legacyTakeover(st, o, uninstall)
		if !errors.Is(err, errLegacyOtherInstance) {
			t.Errorf("%s: expected a refusal, got %v", name, err)
		} else if !strings.Contains(err.Error(), uninstall) {
			t.Errorf("%s: the refusal must name the next step with its full path: %v", name, err)
		}
	}
}

// A legacy state is only taken from folders that belong to the system. On Windows a standard user can create C:\ProgramData\Fleetify\Agent
// and write a state.json in it with the public server and instance CA (which legacyTakeover compares) and a signing key, key and endpoint
// of its own; the install would make that its pinned trust.
func TestLegacyStateIsReadOnlyFromFoldersOwnedByTheSystem(t *testing.T) {
	if platform.IsElevated() {
		t.Skip("an elevated test owns what it creates like the system; this test needs a standard user")
	}
	ca, err := testpki.NewCA("the real instance CA")
	if err != nil {
		t.Fatal(err)
	}
	opts := InstallOptions{Server: "agents.rmm.example:443", Token: "fet_unused", CAFingerprint: ca.Fingerprint()}
	signingKey, _, _ := ed25519.GenerateKey(rand.Reader)
	forged := state.State{
		Server:           opts.Server,
		EndpointID:       "00000000-0000-4000-8000-000000000001",
		InstanceID:       "00000000-0000-4000-8000-000000000002",
		CACertificatePEM: string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: ca.Cert.Raw})),
		SigningPublicKey: base64.StdEncoding.EncodeToString(signingKey),
		SigningKeyID:     "planted-key",
		Key:              state.KeyRef{Kind: "file", File: "agent-identity.key"},
	}
	dataDir := filepath.Join(t.TempDir(), "Fleetify")
	stateDir := filepath.Join(dataDir, "Agent")
	if err := os.MkdirAll(stateDir, 0o755); err != nil {
		t.Fatal(err)
	}
	data, _ := json.Marshal(forged)
	if err := os.WriteFile(filepath.Join(stateDir, state.FileName), data, 0o644); err != nil {
		t.Fatal(err)
	}
	// The comparison alone cannot tell the planted state from a real one.
	if err := legacyTakeover(&forged, opts, "uninstall"); err != nil {
		t.Fatalf("the planted state matches the install command: %v", err)
	}

	if st, err := loadLegacyState(dataDir, stateDir); err == nil {
		t.Fatalf("a legacy state in folders a standard user created was read: signing key %s", st.SigningPublicKey)
	}
}
