//go:build windows

package keystore

import (
	"crypto/ecdsa"
	"fmt"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

// The TPM path, as a per-user key (no elevation). Skipped on machines without a usable TPM.
func TestCNGUserKeyInTPMWhenAvailable(t *testing.T) {
	ref := state.KeyRef{Kind: KindCNG, Provider: ProviderPlatform, Name: fmt.Sprintf("Fleetify Agent TPM Test %d", time.Now().UnixNano())}
	key, ref, err := Create("", ref, 0)
	if err != nil {
		t.Skipf("no usable TPM: %v", err)
	}
	t.Cleanup(func() { _ = Delete("", ref) })
	exerciseKey(t, key)
	if key.Description() != "TPM ("+ProviderPlatform+")" {
		t.Fatalf("unexpected description %q", key.Description())
	}
	_ = key.Close()
	if err := Delete("", ref); err != nil {
		t.Fatalf("delete: %v", err)
	}
}

// A per-user key in the software provider needs no elevation, so the CNG code path is tested on every run.
func TestCNGUserKeyInSoftwareProvider(t *testing.T) {
	ref := state.KeyRef{Kind: KindCNG, Provider: ProviderSoftware, Name: fmt.Sprintf("Fleetify Agent Test %d", time.Now().UnixNano())}
	key, ref, err := Create("", ref, 0)
	if err != nil {
		t.Fatalf("create: %v", err)
	}
	t.Cleanup(func() { _ = Delete("", ref) })
	exerciseKey(t, key)
	_ = key.Close()

	reopened, err := Open("", ref)
	if err != nil {
		t.Fatalf("open: %v", err)
	}
	if !reopened.Public().(*ecdsa.PublicKey).Equal(key.Public()) {
		t.Fatal("reopened key differs")
	}
	exerciseKey(t, reopened)
	_ = reopened.Close()

	if err := Delete("", ref); err != nil {
		t.Fatalf("delete: %v", err)
	}
	if _, err := Open("", ref); err == nil {
		t.Fatal("open after delete must fail")
	}
}
