package remote

import (
	"crypto"
	"crypto/aes"
	"crypto/cipher"
	"crypto/ecdh"
	"crypto/ecdsa"
	"crypto/hkdf"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/asn1"
	"encoding/binary"
	"encoding/hex"
	"errors"
	"fmt"
	"math/big"
	"slices"
)

const (
	// EndpointKeyContext is the domain separation prefix of the endpoint's signature over its ephemeral key.
	EndpointKeyContext = "fleeto-remote-endpoint-key-v1"
	// KeyInfo is the HKDF info prefix; both public keys follow it.
	KeyInfo = "fleeto-remote-v1"
	// PublicKeyBytes is the length of an X25519 public key.
	PublicKeyBytes = 32
	// MaxPlaintextBytes is the largest frame either side encrypts: a file chunk of 1 MiB plus its header.
	MaxPlaintextBytes = 1024*1024 + 64
	// MaxFrameBytes is the largest encrypted frame on the relay.
	MaxFrameBytes = MaxPlaintextBytes + 16
)

// ErrFrame means a frame did not decrypt: it was changed, replayed, reordered or not meant for this session. The session ends.
var ErrFrame = errors.New("a remote session frame failed authentication")

// Keys are the two directional keys of one participant's session.
type Keys struct {
	BrowserToEndpoint [32]byte
	EndpointToBrowser [32]byte
}

// ValidPublicKey reports whether key is 32 bytes and not all zero.
func ValidPublicKey(key []byte) bool {
	if len(key) != PublicKeyBytes {
		return false
	}
	for _, b := range key {
		if b != 0 {
			return true
		}
	}
	return false
}

// DeriveKeys derives the directional keys from the X25519 shared secret: HKDF-SHA256 with the token payload hash as salt and
// "fleeto-remote-v1" || browser public key || endpoint public key as info, 64 bytes, browser to endpoint first.
func DeriveKeys(shared []byte, payloadHash [32]byte, browserPublic, endpointPublic []byte) (Keys, error) {
	info := make([]byte, 0, len(KeyInfo)+2*PublicKeyBytes)
	info = append(info, KeyInfo...)
	info = append(info, browserPublic...)
	info = append(info, endpointPublic...)
	out, err := hkdf.Key(sha256.New, shared, payloadHash[:], string(info), 64)
	if err != nil {
		return Keys{}, err
	}
	var keys Keys
	copy(keys.BrowserToEndpoint[:], out[:32])
	copy(keys.EndpointToBrowser[:], out[32:])
	return keys, nil
}

// EndpointKeyMessage is what the endpoint signs: the context, a zero byte, the token payload hash and its ephemeral public key.
func EndpointKeyMessage(payloadHash [32]byte, endpointPublic []byte) []byte {
	message := make([]byte, 0, len(EndpointKeyContext)+1+32+PublicKeyBytes)
	message = append(message, EndpointKeyContext...)
	message = append(message, 0)
	message = append(message, payloadHash[:]...)
	message = append(message, endpointPublic...)
	return message
}

// EndpointHandshake is the endpoint's side of the key exchange for one verified token.
type EndpointHandshake struct {
	PublicKey []byte
	// Signature is ECDSA P-256 over EndpointKeyMessage in IEEE P1363 form (r || s), as the browser verifies it.
	Signature []byte
	Keys      Keys
}

// NewEndpointHandshake generates the endpoint's ephemeral key, signs it with the certificate key of the service and derives the keys.
func NewEndpointHandshake(token *Token, signer crypto.Signer) (*EndpointHandshake, error) {
	private, err := ecdh.X25519().GenerateKey(rand.Reader)
	if err != nil {
		return nil, fmt.Errorf("generate the session key: %w", err)
	}
	return endpointHandshake(token, signer, private)
}

func endpointHandshake(token *Token, signer crypto.Signer, private *ecdh.PrivateKey) (*EndpointHandshake, error) {
	browser, err := ecdh.X25519().NewPublicKey(token.GetBrowserPublicKey())
	if err != nil {
		return nil, fmt.Errorf("the browser key is invalid: %w", err)
	}
	shared, err := private.ECDH(browser)
	if err != nil {
		return nil, fmt.Errorf("the key exchange failed: %w", err)
	}
	public := private.PublicKey().Bytes()
	keys, err := DeriveKeys(shared, token.PayloadHash, token.GetBrowserPublicKey(), public)
	if err != nil {
		return nil, err
	}
	digest := sha256.Sum256(EndpointKeyMessage(token.PayloadHash, public))
	der, err := signer.Sign(rand.Reader, digest[:], crypto.SHA256)
	if err != nil {
		return nil, fmt.Errorf("sign the session key: %w", err)
	}
	raw, err := p1363(der)
	if err != nil {
		return nil, err
	}
	return &EndpointHandshake{PublicKey: public, Signature: raw, Keys: keys}, nil
}

// BrowserHandshake is the browser's side, implemented in the web UI (remote-crypto.mjs) and here for tests: it accepts the certificate key
// the endpoint sent only when its SHA-256 is one of the public key fingerprints the instance recorded, verifies the endpoint's signature
// with it and derives the same keys.
func BrowserHandshake(browserPrivate *ecdh.PrivateKey, payloadHash [32]byte, endpointPublic, signature, certificatePublicKey []byte,
	fingerprints []string) (Keys, error) {
	if !ValidPublicKey(endpointPublic) || len(signature) != 64 {
		return Keys{}, errors.New("the endpoint key or its signature is malformed")
	}
	sum := sha256.Sum256(certificatePublicKey)
	if !slices.Contains(fingerprints, hex.EncodeToString(sum[:])) {
		return Keys{}, errors.New("the endpoint's certificate key is not a key the instance issued a certificate for")
	}
	parsed, err := x509.ParsePKIXPublicKey(certificatePublicKey)
	certificateKey, ok := parsed.(*ecdsa.PublicKey)
	if err != nil || !ok {
		return Keys{}, errors.New("the endpoint's certificate key is not an ECDSA key")
	}
	digest := sha256.Sum256(EndpointKeyMessage(payloadHash, endpointPublic))
	r := new(big.Int).SetBytes(signature[:32])
	s := new(big.Int).SetBytes(signature[32:])
	if !ecdsa.Verify(certificateKey, digest[:], r, s) {
		return Keys{}, errors.New("the endpoint key is not signed by the endpoint's certificate key")
	}
	endpoint, err := ecdh.X25519().NewPublicKey(endpointPublic)
	if err != nil {
		return Keys{}, err
	}
	shared, err := browserPrivate.ECDH(endpoint)
	if err != nil {
		return Keys{}, err
	}
	return DeriveKeys(shared, payloadHash, browserPrivate.PublicKey().Bytes(), endpointPublic)
}

// Cipher encrypts or decrypts one direction of a session. The nonce is four zero bytes and a 64-bit big-endian counter that starts at 0
// and increases by one per frame; it is never sent. A changed, replayed, dropped or reordered frame therefore fails to decrypt, and
// the session ends. A Cipher is not safe for concurrent use.
type Cipher struct {
	aead    cipher.AEAD
	counter uint64
	failed  bool
}

// NewCipher returns a cipher for one direction.
func NewCipher(key [32]byte) (*Cipher, error) {
	block, err := aes.NewCipher(key[:])
	if err != nil {
		return nil, err
	}
	aead, err := cipher.NewGCM(block)
	if err != nil {
		return nil, err
	}
	return &Cipher{aead: aead}, nil
}

func (c *Cipher) nonce() []byte {
	nonce := make([]byte, 12)
	binary.BigEndian.PutUint64(nonce[4:], c.counter)
	return nonce
}

// Seal encrypts the next frame.
func (c *Cipher) Seal(plaintext []byte) ([]byte, error) {
	if len(plaintext) > MaxPlaintextBytes {
		return nil, fmt.Errorf("a frame of %d bytes is larger than allowed", len(plaintext))
	}
	if c.counter == ^uint64(0) {
		return nil, errors.New("the session used every frame number; open a new session")
	}
	out := c.aead.Seal(nil, c.nonce(), plaintext, nil)
	c.counter++
	return out, nil
}

// Open decrypts the next frame. After one failure every later frame fails too.
func (c *Cipher) Open(frame []byte) ([]byte, error) {
	if c.failed || len(frame) < c.aead.Overhead() || len(frame) > MaxFrameBytes {
		c.failed = true
		return nil, ErrFrame
	}
	plaintext, err := c.aead.Open(nil, c.nonce(), frame, nil)
	if err != nil {
		c.failed = true
		return nil, ErrFrame
	}
	c.counter++
	return plaintext, nil
}

// p1363 converts an ASN.1 DER ECDSA P-256 signature to r || s with 32 bytes each.
func p1363(der []byte) ([]byte, error) {
	var sig struct{ R, S *big.Int }
	rest, err := asn1.Unmarshal(der, &sig)
	if err != nil || len(rest) != 0 || sig.R == nil || sig.S == nil || sig.R.Sign() <= 0 || sig.S.Sign() <= 0 ||
		sig.R.BitLen() > 256 || sig.S.BitLen() > 256 {
		return nil, errors.New("the certificate key returned a malformed signature")
	}
	out := make([]byte, 64)
	sig.R.FillBytes(out[:32])
	sig.S.FillBytes(out[32:])
	return out, nil
}
