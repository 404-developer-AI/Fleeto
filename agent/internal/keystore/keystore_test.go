package keystore

import (
	"crypto/ecdsa"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

func exerciseKey(t *testing.T, key Key) {
	t.Helper()
	digest := sha256.Sum256([]byte("fleeto"))
	sig, err := key.Sign(rand.Reader, digest[:], nil)
	if err != nil {
		t.Fatalf("sign: %v", err)
	}
	if !ecdsa.VerifyASN1(key.Public().(*ecdsa.PublicKey), digest[:], sig) {
		t.Fatal("signature does not verify")
	}
	csr, err := CreateCSR(rand.Reader, key, "host1")
	if err != nil {
		t.Fatal(err)
	}
	parsed, err := x509.ParseCertificateRequest(csr)
	if err != nil {
		t.Fatal(err)
	}
	if err := parsed.CheckSignature(); err != nil {
		t.Fatalf("CSR signature: %v", err)
	}
}

func TestFileKeyCreateOpenSignDelete(t *testing.T) {
	dir := t.TempDir()
	key, ref, err := Create(dir, state.KeyRef{Kind: KindFile}, platform.AccessCurrentUser)
	if err != nil {
		t.Fatal(err)
	}
	exerciseKey(t, key)
	if _, _, err := Create(dir, ref, platform.AccessCurrentUser); err == nil {
		t.Fatal("creating over an existing key file must fail")
	}
	reopened, err := Open(dir, ref)
	if err != nil {
		t.Fatal(err)
	}
	if !reopened.Public().(*ecdsa.PublicKey).Equal(key.Public()) {
		t.Fatal("reopened key differs")
	}
	if err := Delete(dir, ref); err != nil {
		t.Fatal(err)
	}
	if _, err := Open(dir, ref); err == nil {
		t.Fatal("open after delete must fail")
	}
}
