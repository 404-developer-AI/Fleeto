package service

import (
	"encoding/pem"
	"errors"
	"strings"
	"testing"

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

	if err := legacyTakeover(legacy, opts); err != nil {
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
		err := legacyTakeover(st, o)
		if !errors.Is(err, errLegacyOtherInstance) {
			t.Errorf("%s: expected a refusal, got %v", name, err)
		} else if !strings.Contains(err.Error(), "fleetify-agent uninstall") {
			t.Errorf("%s: the refusal must name the next step: %v", name, err)
		}
	}
}
