package keystore

import (
	"crypto"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"os"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

type fileKey struct {
	key  *ecdsa.PrivateKey
	path string
}

func (k *fileKey) Public() crypto.PublicKey { return k.key.Public() }

func (k *fileKey) Sign(r io.Reader, digest []byte, opts crypto.SignerOpts) ([]byte, error) {
	return k.key.Sign(r, digest, opts)
}

func (k *fileKey) Close() error { return nil }

func (k *fileKey) Description() string { return "file " + k.path }

func createFileKey(path string, access platform.Access) (Key, error) {
	if _, err := os.Stat(path); err == nil {
		return nil, fmt.Errorf("a key file already exists at %s", path)
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		return nil, fmt.Errorf("generate key: %w", err)
	}
	der, err := x509.MarshalPKCS8PrivateKey(key)
	if err != nil {
		return nil, fmt.Errorf("encode key: %w", err)
	}
	data := pem.EncodeToMemory(&pem.Block{Type: "PRIVATE KEY", Bytes: der})
	if err := platform.WriteFileAtomic(path, data, access); err != nil {
		return nil, err
	}
	return &fileKey{key: key, path: path}, nil
}

func openFileKey(path string) (Key, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read key file %s: %w", path, err)
	}
	block, _ := pem.Decode(data)
	if block == nil || block.Type != "PRIVATE KEY" {
		return nil, fmt.Errorf("key file %s is not a PKCS#8 PEM key", path)
	}
	parsed, err := x509.ParsePKCS8PrivateKey(block.Bytes)
	if err != nil {
		return nil, fmt.Errorf("key file %s cannot be parsed: %w", path, err)
	}
	key, ok := parsed.(*ecdsa.PrivateKey)
	if !ok || key.Curve != elliptic.P256() {
		return nil, fmt.Errorf("key file %s does not hold an ECDSA P-256 key", path)
	}
	return &fileKey{key: key, path: path}, nil
}

func deleteFileKey(path string) error {
	err := os.Remove(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}
