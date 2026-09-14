//go:build windows

package keystore

import (
	"crypto"
	"crypto/ecdsa"
	"crypto/elliptic"
	"encoding/asn1"
	"encoding/binary"
	"errors"
	"fmt"
	"io"
	"math/big"
	"sync"
	"unsafe"

	"golang.org/x/sys/windows"

	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

// Provider names of the CNG key storage providers.
const (
	ProviderPlatform = "Microsoft Platform Crypto Provider"
	ProviderSoftware = "Microsoft Software Key Storage Provider"
)

const (
	ncryptMachineKeyFlag  = 0x00000020
	ncryptSilentFlag      = 0x00000040
	ncryptPersistFlag     = 0x80000000
	ncryptAllowSigning    = 0x00000002
	daclSecurityInfo      = 0x00000004
	ecdsaPublicP256Magic  = 0x31534345 // "ECS1"
	nteBadKeyset          = 0x80090016
	nteNotSupported       = 0x80090029
	algorithmECDSAP256    = "ECDSA_P256"
	blobECCPublic         = "ECCPUBLICBLOB"
	propertyExportPolicy  = "Export Policy"
	propertyKeyUsage      = "Key Usage"
	propertySecurityDescr = "Security Descr"
)

var (
	ncrypt                        = windows.NewLazySystemDLL("ncrypt.dll")
	procNCryptOpenStorageProvider = ncrypt.NewProc("NCryptOpenStorageProvider")
	procNCryptCreatePersistedKey  = ncrypt.NewProc("NCryptCreatePersistedKey")
	procNCryptOpenKey             = ncrypt.NewProc("NCryptOpenKey")
	procNCryptSetProperty         = ncrypt.NewProc("NCryptSetProperty")
	procNCryptFinalizeKey         = ncrypt.NewProc("NCryptFinalizeKey")
	procNCryptExportKey           = ncrypt.NewProc("NCryptExportKey")
	procNCryptSignHash            = ncrypt.NewProc("NCryptSignHash")
	procNCryptDeleteKey           = ncrypt.NewProc("NCryptDeleteKey")
	procNCryptFreeObject          = ncrypt.NewProc("NCryptFreeObject")
)

type cngError struct {
	op   string
	code uint32
}

func (e *cngError) Error() string {
	return fmt.Sprintf("%s: %v (0x%08X)", e.op, windows.Errno(e.code), e.code)
}

func isCode(err error, code uint32) bool {
	var ce *cngError
	return errors.As(err, &ce) && ce.code == code
}

// call invokes an NCrypt function. NCrypt returns SECURITY_STATUS: zero on success.
func call(proc *windows.LazyProc, op string, args ...uintptr) error {
	if err := proc.Find(); err != nil {
		return fmt.Errorf("%s: %w", op, err)
	}
	r, _, _ := proc.Call(args...)
	if uint32(r) != 0 {
		return &cngError{op: op, code: uint32(r)}
	}
	return nil
}

func openProvider(name string) (uintptr, error) {
	namePtr, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return 0, err
	}
	var h uintptr
	if err := call(procNCryptOpenStorageProvider, "NCryptOpenStorageProvider", uintptr(unsafe.Pointer(&h)), uintptr(unsafe.Pointer(namePtr)), 0); err != nil {
		return 0, err
	}
	return h, nil
}

func freeObject(h uintptr) {
	if h != 0 {
		_, _, _ = procNCryptFreeObject.Call(h)
	}
}

func setProperty(h uintptr, name string, value []byte, flags uint32) error {
	namePtr, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return err
	}
	return call(procNCryptSetProperty, "NCryptSetProperty("+name+")", h, uintptr(unsafe.Pointer(namePtr)),
		uintptr(unsafe.Pointer(&value[0])), uintptr(len(value)), uintptr(flags))
}

func machineFlag(machine bool) uintptr {
	if machine {
		return ncryptMachineKeyFlag
	}
	return 0
}

type cngKey struct {
	mu       sync.Mutex
	provider uintptr
	key      uintptr
	public   *ecdsa.PublicKey
	name     string
}

func (k *cngKey) Public() crypto.PublicKey { return k.public }

func (k *cngKey) Description() string {
	if k.name == ProviderPlatform {
		return "TPM (" + ProviderPlatform + ")"
	}
	return k.name
}

// Sign signs a digest inside the key storage provider and converts the IEEE P1363 r||s result to ASN.1 DER, the
// format crypto.Signer callers (crypto/tls, crypto/x509) expect for ECDSA.
func (k *cngKey) Sign(_ io.Reader, digest []byte, _ crypto.SignerOpts) ([]byte, error) {
	if len(digest) == 0 {
		return nil, errors.New("empty digest")
	}
	k.mu.Lock()
	defer k.mu.Unlock()
	if k.key == 0 {
		return nil, errors.New("the identity key is closed")
	}
	var size uint32
	if err := call(procNCryptSignHash, "NCryptSignHash", k.key, 0, uintptr(unsafe.Pointer(&digest[0])), uintptr(len(digest)),
		0, 0, uintptr(unsafe.Pointer(&size)), ncryptSilentFlag); err != nil {
		return nil, err
	}
	if size == 0 || size%2 != 0 || size > 512 {
		return nil, fmt.Errorf("NCryptSignHash returned an unexpected signature size %d", size)
	}
	sig := make([]byte, size)
	if err := call(procNCryptSignHash, "NCryptSignHash", k.key, 0, uintptr(unsafe.Pointer(&digest[0])), uintptr(len(digest)),
		uintptr(unsafe.Pointer(&sig[0])), uintptr(size), uintptr(unsafe.Pointer(&size)), ncryptSilentFlag); err != nil {
		return nil, err
	}
	sig = sig[:size]
	half := len(sig) / 2
	return asn1.Marshal(struct{ R, S *big.Int }{
		R: new(big.Int).SetBytes(sig[:half]),
		S: new(big.Int).SetBytes(sig[half:]),
	})
}

func (k *cngKey) Close() error {
	k.mu.Lock()
	defer k.mu.Unlock()
	freeObject(k.key)
	freeObject(k.provider)
	k.key, k.provider = 0, 0
	return nil
}

func exportPublic(h uintptr) (*ecdsa.PublicKey, error) {
	blobType, _ := windows.UTF16PtrFromString(blobECCPublic)
	var size uint32
	if err := call(procNCryptExportKey, "NCryptExportKey", h, 0, uintptr(unsafe.Pointer(blobType)), 0, 0, 0,
		uintptr(unsafe.Pointer(&size)), 0); err != nil {
		return nil, err
	}
	if size < 8 || size > 1024 {
		return nil, fmt.Errorf("unexpected public key blob size %d", size)
	}
	buf := make([]byte, size)
	if err := call(procNCryptExportKey, "NCryptExportKey", h, 0, uintptr(unsafe.Pointer(blobType)), 0,
		uintptr(unsafe.Pointer(&buf[0])), uintptr(size), uintptr(unsafe.Pointer(&size)), 0); err != nil {
		return nil, err
	}
	buf = buf[:size]
	// BCRYPT_ECCKEY_BLOB: magic, key length, X, Y (big-endian coordinates).
	magic := binary.LittleEndian.Uint32(buf[0:4])
	keyLen := int(binary.LittleEndian.Uint32(buf[4:8]))
	if magic != ecdsaPublicP256Magic || keyLen != 32 || len(buf) != 8+2*keyLen {
		return nil, errors.New("the key is not an ECDSA P-256 key")
	}
	uncompressed := append([]byte{0x04}, buf[8:]...)
	// ParseUncompressedPublicKey rejects points that are not on the curve.
	pub, err := ecdsa.ParseUncompressedPublicKey(elliptic.P256(), uncompressed)
	if err != nil {
		return nil, fmt.Errorf("the exported public key is invalid: %w", err)
	}
	return pub, nil
}

// createCNGKey tries the TPM first and falls back to the software provider, unless ref names a provider.
func createCNGKey(ref state.KeyRef) (Key, state.KeyRef, error) {
	providers := []string{ProviderPlatform, ProviderSoftware}
	if ref.Provider != "" {
		providers = []string{ref.Provider}
	}
	var errs []error
	for _, provider := range providers {
		key, err := createInProvider(provider, ref.Name, ref.Machine)
		if err == nil {
			ref.Provider = provider
			return key, ref, nil
		}
		errs = append(errs, fmt.Errorf("%s: %w", provider, err))
	}
	return nil, ref, fmt.Errorf("create identity key: %w", errors.Join(errs...))
}

func createInProvider(provider, name string, machine bool) (Key, error) {
	hProv, err := openProvider(provider)
	if err != nil {
		return nil, err
	}
	algPtr, _ := windows.UTF16PtrFromString(algorithmECDSAP256)
	namePtr, err := windows.UTF16PtrFromString(name)
	if err != nil {
		freeObject(hProv)
		return nil, err
	}
	var hKey uintptr
	if err := call(procNCryptCreatePersistedKey, "NCryptCreatePersistedKey", hProv, uintptr(unsafe.Pointer(&hKey)),
		uintptr(unsafe.Pointer(algPtr)), uintptr(unsafe.Pointer(namePtr)), 0, machineFlag(machine)); err != nil {
		freeObject(hProv)
		return nil, err
	}
	fail := func(err error) (Key, error) {
		// The key is not finalized, so nothing was persisted; freeing the handle discards it.
		freeObject(hKey)
		freeObject(hProv)
		return nil, err
	}

	// Non-exportable, signing only. TPM keys cannot be exported at all and the provider may reject the properties.
	exportPolicy := make([]byte, 4)
	if err := setProperty(hKey, propertyExportPolicy, exportPolicy, ncryptPersistFlag); err != nil && provider != ProviderPlatform {
		return fail(err)
	}
	usage := make([]byte, 4)
	binary.LittleEndian.PutUint32(usage, ncryptAllowSigning)
	if err := setProperty(hKey, propertyKeyUsage, usage, ncryptPersistFlag); err != nil && provider != ProviderPlatform {
		return fail(err)
	}
	if machine {
		// SYSTEM and Administrators only: the service (SYSTEM) can use a key created by the elevated installer,
		// ordinary users cannot.
		sd, err := windows.SecurityDescriptorFromString("D:P(A;;GA;;;SY)(A;;GA;;;BA)")
		if err != nil {
			return fail(err)
		}
		raw := unsafe.Slice((*byte)(unsafe.Pointer(sd)), sd.Length())
		if err := setProperty(hKey, propertySecurityDescr, raw, daclSecurityInfo); err != nil && !isCode(err, nteNotSupported) {
			return fail(err)
		}
	}
	if err := call(procNCryptFinalizeKey, "NCryptFinalizeKey", hKey, ncryptSilentFlag); err != nil {
		return fail(err)
	}
	pub, err := exportPublic(hKey)
	if err != nil {
		_ = call(procNCryptDeleteKey, "NCryptDeleteKey", hKey, 0)
		freeObject(hProv)
		return nil, err
	}
	return &cngKey{provider: hProv, key: hKey, public: pub, name: provider}, nil
}

func openCNGKey(ref state.KeyRef) (Key, error) {
	hProv, err := openProvider(ref.Provider)
	if err != nil {
		return nil, err
	}
	namePtr, err := windows.UTF16PtrFromString(ref.Name)
	if err != nil {
		freeObject(hProv)
		return nil, err
	}
	var hKey uintptr
	if err := call(procNCryptOpenKey, "NCryptOpenKey", hProv, uintptr(unsafe.Pointer(&hKey)), uintptr(unsafe.Pointer(namePtr)), 0,
		machineFlag(ref.Machine)|ncryptSilentFlag); err != nil {
		freeObject(hProv)
		if isCode(err, nteBadKeyset) {
			return nil, fmt.Errorf("the identity key %q does not exist in %s: enroll the agent again", ref.Name, ref.Provider)
		}
		return nil, err
	}
	pub, err := exportPublic(hKey)
	if err != nil {
		freeObject(hKey)
		freeObject(hProv)
		return nil, err
	}
	return &cngKey{provider: hProv, key: hKey, public: pub, name: ref.Provider}, nil
}

func deleteCNGKey(ref state.KeyRef) error {
	providers := []string{ProviderPlatform, ProviderSoftware}
	if ref.Provider != "" {
		providers = []string{ref.Provider}
	}
	var errs []error
	for _, provider := range providers {
		if err := deleteInProvider(provider, ref.Name, ref.Machine); err != nil {
			errs = append(errs, fmt.Errorf("%s: %w", provider, err))
		}
	}
	return errors.Join(errs...)
}

func deleteInProvider(provider, name string, machine bool) error {
	hProv, err := openProvider(provider)
	if err != nil {
		// A provider that is not available (no TPM) holds no key.
		return nil
	}
	defer freeObject(hProv)
	namePtr, err := windows.UTF16PtrFromString(name)
	if err != nil {
		return err
	}
	var hKey uintptr
	if err := call(procNCryptOpenKey, "NCryptOpenKey", hProv, uintptr(unsafe.Pointer(&hKey)), uintptr(unsafe.Pointer(namePtr)), 0,
		machineFlag(machine)|ncryptSilentFlag); err != nil {
		if isCode(err, nteBadKeyset) {
			return nil
		}
		return err
	}
	// NCryptDeleteKey frees the handle on success.
	if err := call(procNCryptDeleteKey, "NCryptDeleteKey", hKey, 0); err != nil {
		freeObject(hKey)
		return err
	}
	return nil
}
