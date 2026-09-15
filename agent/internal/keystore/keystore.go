// Package keystore holds the agent identity key: an ECDSA P-256 key generated on the endpoint that never leaves it.
//
// Windows service mode uses a non-exportable CNG machine key, in the TPM when one is available. Development mode and
// the other platforms use a PKCS#8 file readable only by its owner.
package keystore

import (
	"crypto"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/x509"
	"crypto/x509/pkix"
	"errors"
	"fmt"
	"io"
	"path/filepath"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

const (
	// KindFile is a PKCS#8 key file in the state directory.
	KindFile = "file"
	// KindCNG is a Windows CNG persisted key.
	KindCNG = "cng"
	// DefaultFileName is the key file name for KindFile.
	DefaultFileName = "agent-identity.key"
	// DefaultCNGName is the CNG key name for KindCNG.
	DefaultCNGName = "Fleeto Agent Identity"
)

// ErrUnsupported is returned when a key kind is not available on this platform.
var ErrUnsupported = errors.New("this key store is not supported on this platform")

// Key is the identity key. Sign returns an ASN.1 DER ECDSA signature over a SHA-256 digest.
type Key interface {
	crypto.Signer
	// Close releases handles. The key itself stays in the store.
	Close() error
	// Description names the storage for status output and logs, e.g. "TPM (Microsoft Platform Crypto Provider)".
	Description() string
}

// Create generates a new key as described by ref and returns the key plus the completed reference.
func Create(dir string, ref state.KeyRef, access platform.Access) (Key, state.KeyRef, error) {
	switch ref.Kind {
	case KindFile:
		if ref.File == "" {
			ref.File = DefaultFileName
		}
		key, err := createFileKey(filepath.Join(dir, ref.File), access)
		return key, ref, err
	case KindCNG:
		if ref.Name == "" {
			ref.Name = DefaultCNGName
		}
		return createCNGKey(ref)
	default:
		return nil, ref, fmt.Errorf("unknown key store %q", ref.Kind)
	}
}

// Open loads an existing key.
func Open(dir string, ref state.KeyRef) (Key, error) {
	switch ref.Kind {
	case KindFile:
		return openFileKey(filepath.Join(dir, ref.File))
	case KindCNG:
		return openCNGKey(ref)
	default:
		return nil, fmt.Errorf("unknown key store %q", ref.Kind)
	}
}

// Delete removes the key from its store. A missing key is not an error.
func Delete(dir string, ref state.KeyRef) error {
	switch ref.Kind {
	case KindFile:
		return deleteFileKey(filepath.Join(dir, ref.File))
	case KindCNG:
		return deleteCNGKey(ref)
	default:
		return fmt.Errorf("unknown key store %q", ref.Kind)
	}
}

// CreateCSR builds a PKCS#10 request signed by key. The subject is informational only: the signer sets subject and
// extensions of the issued certificate itself.
func CreateCSR(rand io.Reader, key crypto.Signer, hostname string) ([]byte, error) {
	pub, ok := key.Public().(*ecdsa.PublicKey)
	if !ok || pub.Curve != elliptic.P256() {
		return nil, errors.New("the identity key is not an ECDSA P-256 key")
	}
	template := &x509.CertificateRequest{
		Subject:            pkix.Name{CommonName: hostname, Organization: []string{"Fleeto"}},
		SignatureAlgorithm: x509.ECDSAWithSHA256,
	}
	der, err := x509.CreateCertificateRequest(rand, template, key)
	if err != nil {
		return nil, fmt.Errorf("create certificate signing request: %w", err)
	}
	return der, nil
}

// SamePublicKey reports whether the certificate carries the public key of key.
func SamePublicKey(cert *x509.Certificate, key crypto.Signer) bool {
	certPub, ok := cert.PublicKey.(*ecdsa.PublicKey)
	if !ok {
		return false
	}
	keyPub, ok := key.Public().(*ecdsa.PublicKey)
	if !ok {
		return false
	}
	return certPub.Equal(keyPub)
}
