//go:build linux

// TPM 2.0 storage of the agent identity key on Linux (0.2.1). The key is generated inside the TPM and never leaves it: what is stored
// on disk is the key blob the TPM itself encrypted, which only this TPM can load. A copy of the disk, or a cloned virtual machine with
// a new virtual TPM, therefore carries no working identity.
//
// The key is an ECDSA P-256 signing key under the owner hierarchy storage key (the TCG reference ECC SRK template), created and loaded
// again from the blob for each operation, so no handle is persisted in the TPM's limited NV storage.

package keystore

import (
	"crypto"
	"crypto/ecdsa"
	"crypto/elliptic"
	"encoding/asn1"
	"encoding/pem"
	"errors"
	"fmt"
	"io"
	"math/big"
	"os"
	"path/filepath"
	"sync"

	"github.com/google/go-tpm/tpm2"
	"github.com/google/go-tpm/tpm2/transport"
	"github.com/google/go-tpm/tpm2/transport/linuxtpm"
	"github.com/google/go-tpm/tpm2/transport/linuxudstpm"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

// DefaultTPMFileName is the file holding the TPM key blobs for KindTPM.
const DefaultTPMFileName = "agent-identity.tpmkey"

// tpmDeviceEnv points at another TPM device or socket; tests use it with a software TPM.
const tpmDeviceEnv = "FLEETO_TPM_DEVICE"

// tpmDevices are the device files tried in order: the kernel resource manager first, so the agent shares the TPM with other software.
var tpmDevices = []string{"/dev/tpmrm0", "/dev/tpm0"}

// PEM types of the two blobs; both are TPM-encrypted or public data, so the file holds no usable secret on its own.
const (
	pemTypePublic  = "TPM2 PUBLIC KEY"
	pemTypePrivate = "TPM2 PRIVATE KEY"
)

// TPMAvailable reports whether this endpoint has a usable TPM 2.0: a device that opens and answers a command.
func TPMAvailable() bool {
	tpm, err := openTPM()
	if err != nil {
		return false
	}
	defer tpm.Close()
	_, err = tpm2.GetCapability{Capability: tpm2.TPMCapTPMProperties, Property: uint32(tpm2.TPMPTFamilyIndicator), PropertyCount: 1}.Execute(tpm)
	return err == nil
}

func openTPM() (transport.TPMCloser, error) {
	if path := os.Getenv(tpmDeviceEnv); path != "" {
		info, err := os.Stat(path)
		if err != nil {
			return nil, fmt.Errorf("open the TPM at %s: %w", path, err)
		}
		if info.Mode()&os.ModeSocket != 0 {
			return linuxudstpm.Open(path)
		}
		return linuxtpm.Open(path)
	}
	var errs []error
	for _, path := range tpmDevices {
		tpm, err := linuxtpm.Open(path)
		if err == nil {
			return tpm, nil
		}
		errs = append(errs, err)
	}
	return nil, fmt.Errorf("no TPM 2.0 device could be opened: %w", errors.Join(errs...))
}

// tpmKey is an identity key held in the TPM. The blobs are loaded again for every signature, so nothing but this file survives a
// restart and no transient handle leaks.
type tpmKey struct {
	path    string
	public  tpm2.TPM2BPublic
	private tpm2.TPM2BPrivate
	pub     *ecdsa.PublicKey
	mu      sync.Mutex
}

func (k *tpmKey) Public() crypto.PublicKey { return k.pub }

func (k *tpmKey) Close() error { return nil }

func (k *tpmKey) Description() string { return "TPM 2.0" }

// Sign signs a SHA-256 digest in the TPM and returns an ASN.1 DER ECDSA signature.
func (k *tpmKey) Sign(_ io.Reader, digest []byte, opts crypto.SignerOpts) ([]byte, error) {
	if opts == nil || opts.HashFunc() != crypto.SHA256 || len(digest) != crypto.SHA256.Size() {
		return nil, errors.New("the TPM identity key signs SHA-256 digests only")
	}
	k.mu.Lock()
	defer k.mu.Unlock()
	tpm, err := openTPM()
	if err != nil {
		return nil, err
	}
	defer tpm.Close()
	parent, closeParent, err := createParent(tpm)
	if err != nil {
		return nil, err
	}
	defer closeParent()
	loaded, err := tpm2.Load{ParentHandle: parent, InPrivate: k.private, InPublic: k.public}.Execute(tpm)
	if err != nil {
		return nil, fmt.Errorf("load the identity key into the TPM: %w", err)
	}
	defer func() { _, _ = tpm2.FlushContext{FlushHandle: loaded.ObjectHandle}.Execute(tpm) }()

	signed, err := tpm2.Sign{
		KeyHandle: tpm2.NamedHandle{Handle: loaded.ObjectHandle, Name: loaded.Name},
		Digest:    tpm2.TPM2BDigest{Buffer: digest},
		InScheme: tpm2.TPMTSigScheme{
			Scheme:  tpm2.TPMAlgECDSA,
			Details: tpm2.NewTPMUSigScheme(tpm2.TPMAlgECDSA, &tpm2.TPMSSchemeHash{HashAlg: tpm2.TPMAlgSHA256}),
		},
		Validation: tpm2.TPMTTKHashCheck{Tag: tpm2.TPMSTHashCheck},
	}.Execute(tpm)
	if err != nil {
		return nil, fmt.Errorf("sign with the TPM identity key: %w", err)
	}
	ecdsaSig, err := signed.Signature.Signature.ECDSA()
	if err != nil {
		return nil, fmt.Errorf("read the TPM signature: %w", err)
	}
	return encodeSignature(ecdsaSig.SignatureR.Buffer, ecdsaSig.SignatureS.Buffer)
}

// encodeSignature turns the r and s values of the TPM into the ASN.1 DER encoding crypto/x509 and TLS expect.
func encodeSignature(r, s []byte) ([]byte, error) {
	signature := struct{ R, S *big.Int }{new(big.Int).SetBytes(r), new(big.Int).SetBytes(s)}
	der, err := asn1.Marshal(signature)
	if err != nil {
		return nil, fmt.Errorf("encode the TPM signature: %w", err)
	}
	return der, nil
}

// createParent creates the storage key the identity key lives under. It is derived from the owner hierarchy seed, so the same TPM
// always produces the same parent and no handle has to be persisted.
func createParent(tpm transport.TPM) (tpm2.AuthHandle, func(), error) {
	primary, err := tpm2.CreatePrimary{
		PrimaryHandle: tpm2.TPMRHOwner,
		InPublic:      tpm2.New2B(tpm2.ECCSRKTemplate),
	}.Execute(tpm)
	if err != nil {
		return tpm2.AuthHandle{}, nil, fmt.Errorf("create the TPM storage key: %w", err)
	}
	handle := tpm2.AuthHandle{Handle: primary.ObjectHandle, Name: primary.Name, Auth: tpm2.PasswordAuth(nil)}
	return handle, func() { _, _ = tpm2.FlushContext{FlushHandle: primary.ObjectHandle}.Execute(tpm) }, nil
}

// identityTemplate is the ECDSA P-256 signing key: it cannot leave this TPM (FixedTPM, FixedParent), is generated inside it
// (SensitiveDataOrigin) and is not subject to dictionary attack lockout, since it has no authorisation value to guess.
func identityTemplate() tpm2.TPMTPublic {
	return tpm2.TPMTPublic{
		Type:    tpm2.TPMAlgECC,
		NameAlg: tpm2.TPMAlgSHA256,
		ObjectAttributes: tpm2.TPMAObject{
			FixedTPM:            true,
			FixedParent:         true,
			SensitiveDataOrigin: true,
			UserWithAuth:        true,
			NoDA:                true,
			SignEncrypt:         true,
		},
		Parameters: tpm2.NewTPMUPublicParms(tpm2.TPMAlgECC, &tpm2.TPMSECCParms{
			Scheme: tpm2.TPMTECCScheme{
				Scheme:  tpm2.TPMAlgECDSA,
				Details: tpm2.NewTPMUAsymScheme(tpm2.TPMAlgECDSA, &tpm2.TPMSSigSchemeECDSA{HashAlg: tpm2.TPMAlgSHA256}),
			},
			CurveID: tpm2.TPMECCNistP256,
		}),
	}
}

func createTPMKey(path string, access platform.Access) (Key, error) {
	if _, err := os.Stat(path); err == nil {
		return nil, fmt.Errorf("a TPM key file already exists at %s", path)
	}
	tpm, err := openTPM()
	if err != nil {
		return nil, err
	}
	defer tpm.Close()
	parent, closeParent, err := createParent(tpm)
	if err != nil {
		return nil, err
	}
	defer closeParent()

	created, err := tpm2.Create{ParentHandle: parent, InPublic: tpm2.New2B(identityTemplate())}.Execute(tpm)
	if err != nil {
		return nil, fmt.Errorf("create the identity key in the TPM: %w", err)
	}
	key, err := newTPMKey(path, created.OutPublic, created.OutPrivate)
	if err != nil {
		return nil, err
	}
	data := append(
		pem.EncodeToMemory(&pem.Block{Type: pemTypePublic, Bytes: tpm2.Marshal(created.OutPublic)}),
		pem.EncodeToMemory(&pem.Block{Type: pemTypePrivate, Bytes: tpm2.Marshal(created.OutPrivate)})...)
	if err := platform.WriteFileAtomic(path, data, access); err != nil {
		return nil, err
	}
	return key, nil
}

func openTPMKey(path string) (Key, error) {
	data, err := os.ReadFile(path)
	if err != nil {
		return nil, fmt.Errorf("read TPM key file %s: %w", path, err)
	}
	var public *tpm2.TPM2BPublic
	var private *tpm2.TPM2BPrivate
	for rest := data; len(rest) > 0; {
		var block *pem.Block
		block, rest = pem.Decode(rest)
		if block == nil {
			break
		}
		switch block.Type {
		case pemTypePublic:
			public, err = tpm2.Unmarshal[tpm2.TPM2BPublic](block.Bytes)
		case pemTypePrivate:
			private, err = tpm2.Unmarshal[tpm2.TPM2BPrivate](block.Bytes)
		}
		if err != nil {
			return nil, fmt.Errorf("TPM key file %s is damaged: %w", path, err)
		}
	}
	if public == nil || private == nil {
		return nil, fmt.Errorf("TPM key file %s does not hold a TPM 2.0 key", path)
	}
	return newTPMKey(path, *public, *private)
}

func newTPMKey(path string, public tpm2.TPM2BPublic, private tpm2.TPM2BPrivate) (Key, error) {
	contents, err := public.Contents()
	if err != nil {
		return nil, fmt.Errorf("read the public part of the TPM key: %w", err)
	}
	parms, err := contents.Parameters.ECCDetail()
	if err != nil {
		return nil, fmt.Errorf("read the public part of the TPM key: %w", err)
	}
	point, err := contents.Unique.ECC()
	if err != nil {
		return nil, fmt.Errorf("read the public part of the TPM key: %w", err)
	}
	pub, err := tpm2.ECDSAPub(parms, point)
	if err != nil {
		return nil, fmt.Errorf("read the public key from the TPM key: %w", err)
	}
	if pub.Curve != elliptic.P256() {
		return nil, fmt.Errorf("TPM key file %s does not hold an ECDSA P-256 key", path)
	}
	return &tpmKey{path: path, public: public, private: private, pub: pub}, nil
}

func deleteTPMKey(path string) error {
	// The blob is the key: removing the file removes the identity, since the TPM keeps nothing of its own for it.
	err := os.Remove(path)
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}

func tpmKeyPath(dir string, ref state.KeyRef) string {
	name := ref.File
	if name == "" {
		name = DefaultTPMFileName
	}
	return filepath.Join(dir, name)
}
