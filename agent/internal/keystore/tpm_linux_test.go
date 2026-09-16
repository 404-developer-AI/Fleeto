//go:build linux

package keystore

import (
	"context"
	"crypto"
	"crypto/ecdsa"
	"crypto/rand"
	"crypto/sha256"
	"os"
	"os/exec"
	"path/filepath"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

// startSoftwareTPM runs swtpm on a Unix socket for this test and points the key store at it. It skips the test where swtpm is not
// installed, so a developer machine without one still runs the rest of the suite.
func startSoftwareTPM(t *testing.T) {
	t.Helper()
	tool, err := exec.LookPath("swtpm")
	if err != nil {
		t.Skip("swtpm is not installed; the TPM key store is tested where it is (CI and endpoints with a TPM)")
	}
	dir, err := os.MkdirTemp("", "fleeto-swtpm-")
	if err != nil {
		t.Fatalf("temporary directory: %v", err)
	}
	t.Cleanup(func() { _ = os.RemoveAll(dir) })
	socket := filepath.Join(dir, "sock")
	ctx, cancel := context.WithCancel(context.Background())
	cmd := exec.CommandContext(ctx, tool, "socket", "--tpm2",
		"--tpmstate", "dir="+dir,
		"--server", "type=unixio,path="+socket,
		"--ctrl", "type=unixio,path="+filepath.Join(dir, "ctrl"),
		"--flags", "not-need-init,startup-clear")
	if err := cmd.Start(); err != nil {
		t.Fatalf("start swtpm: %v", err)
	}
	t.Cleanup(func() {
		cancel()
		_ = cmd.Wait()
	})
	deadline := time.Now().Add(10 * time.Second)
	for {
		if _, err := os.Stat(socket); err == nil {
			break
		}
		if time.Now().After(deadline) {
			t.Fatal("swtpm did not create its socket in time")
		}
		time.Sleep(50 * time.Millisecond)
	}
	t.Setenv(tpmDeviceEnv, socket)
}

func TestTPMKeySignsAndSurvivesAReopen(t *testing.T) {
	startSoftwareTPM(t)
	if !TPMAvailable() {
		t.Fatal("the software TPM was started but TPMAvailable reports none")
	}
	dir := t.TempDir()

	key, ref, err := Create(dir, state.KeyRef{Kind: KindTPM}, platform.AccessCurrentUser)
	if err != nil {
		t.Fatalf("create the TPM key: %v", err)
	}
	t.Cleanup(func() { _ = key.Close() })
	if ref.Kind != KindTPM || ref.File != DefaultTPMFileName {
		t.Fatalf("unexpected key reference %+v", ref)
	}
	if key.Description() != "TPM 2.0" {
		t.Errorf("description = %q, want TPM 2.0", key.Description())
	}

	digest := sha256.Sum256([]byte("fleeto"))
	signature, err := key.Sign(rand.Reader, digest[:], crypto.SHA256)
	if err != nil {
		t.Fatalf("sign with the TPM key: %v", err)
	}
	public, ok := key.Public().(*ecdsa.PublicKey)
	if !ok {
		t.Fatalf("the TPM key has no ECDSA public key but %T", key.Public())
	}
	if !ecdsa.VerifyASN1(public, digest[:], signature) {
		t.Fatal("the signature of the TPM key does not verify")
	}

	// The blobs on disk are the identity: after a restart the same key opens and signs again.
	reopened, err := Open(dir, ref)
	if err != nil {
		t.Fatalf("open the TPM key again: %v", err)
	}
	defer reopened.Close()
	again, err := reopened.Sign(rand.Reader, digest[:], crypto.SHA256)
	if err != nil {
		t.Fatalf("sign with the reopened key: %v", err)
	}
	if !ecdsa.VerifyASN1(public, digest[:], again) {
		t.Fatal("the reopened TPM key signs with another key")
	}
}

func TestTPMKeyRefusesOtherDigestsAndIsDeleted(t *testing.T) {
	startSoftwareTPM(t)
	dir := t.TempDir()
	key, ref, err := Create(dir, state.KeyRef{Kind: KindTPM}, platform.AccessCurrentUser)
	if err != nil {
		t.Fatalf("create the TPM key: %v", err)
	}
	defer key.Close()

	if _, err := key.Sign(rand.Reader, make([]byte, 64), crypto.SHA512); err == nil {
		t.Error("the TPM identity key signs SHA-256 digests only")
	}

	if err := Delete(dir, ref); err != nil {
		t.Fatalf("delete the TPM key: %v", err)
	}
	if _, err := os.Stat(filepath.Join(dir, ref.File)); !os.IsNotExist(err) {
		t.Errorf("the key blob is still there: %v", err)
	}
	if err := Delete(dir, ref); err != nil {
		t.Errorf("deleting a key that is gone is not an error: %v", err)
	}
	if _, err := Open(dir, ref); err == nil {
		t.Error("a deleted TPM key cannot be opened")
	}
}
