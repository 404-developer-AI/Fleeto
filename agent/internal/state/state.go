// Package state persists the enrollment state of the agent as JSON in the state directory.
//
// The state holds no secrets: the private key lives in the key store, and the enrollment token is never stored.
// Everything in it is public material (certificates, public keys, identifiers) plus the last verified configuration.
package state

import (
	"crypto/ed25519"
	"crypto/x509"
	"encoding/base64"
	"encoding/json"
	"encoding/pem"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"sync"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
)

// FileName is the name of the state file inside the state directory.
const FileName = "state.json"

// ErrNotEnrolled is returned when no state file exists.
var ErrNotEnrolled = errors.New("the agent is not enrolled")

// KeyRef says where the identity key lives. It never contains key material.
type KeyRef struct {
	// Kind is "file" or "cng".
	Kind string `json:"kind"`
	// Provider is the CNG key storage provider, e.g. "Microsoft Platform Crypto Provider".
	Provider string `json:"provider,omitempty"`
	// Name is the CNG key name.
	Name string `json:"name,omitempty"`
	// Machine is true for a CNG machine key.
	Machine bool `json:"machine,omitempty"`
	// File is the key file name relative to the state directory.
	File string `json:"file,omitempty"`
}

// State is the persisted enrollment state.
type State struct {
	Server               string    `json:"server"`
	EndpointID           string    `json:"endpointId"`
	InstanceID           string    `json:"instanceId"`
	CACertificatePEM     string    `json:"caCertificatePem"`
	SigningPublicKey     string    `json:"instanceSigningPublicKey"`
	SigningKeyID         string    `json:"instanceSigningKeyId"`
	CertificatePEM       string    `json:"certificatePem"`
	Key                  KeyRef    `json:"key"`
	EnrolledAt           time.Time `json:"enrolledAt"`
	AppliedConfig        []byte    `json:"appliedConfig,omitempty"`
	AppliedConfigVersion uint64    `json:"appliedConfigVersion"`
	InventoryHash        string    `json:"inventoryHash,omitempty"`
	Revoked              bool      `json:"revoked,omitempty"`
	RevokedReason        string    `json:"revokedReason,omitempty"`
	RevokedAt            time.Time `json:"revokedAt,omitzero"`
	CertificateRenewedAt time.Time `json:"certificateRenewedAt,omitzero"`
}

// Certificate parses the agent certificate.
func (s *State) Certificate() (*x509.Certificate, error) {
	return parsePEMCertificate(s.CertificatePEM, "agent certificate")
}

// CACertificate parses the pinned instance CA certificate.
func (s *State) CACertificate() (*x509.Certificate, error) {
	return parsePEMCertificate(s.CACertificatePEM, "instance CA certificate")
}

// SigningKey decodes the pinned instance signing public key.
func (s *State) SigningKey() (ed25519.PublicKey, error) {
	raw, err := base64.StdEncoding.DecodeString(s.SigningPublicKey)
	if err != nil || len(raw) != ed25519.PublicKeySize {
		return nil, errors.New("the stored instance signing public key is invalid")
	}
	return ed25519.PublicKey(raw), nil
}

// EncodeCertificatePEM returns DER as a PEM certificate block.
func EncodeCertificatePEM(der []byte) string {
	return string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: der}))
}

func parsePEMCertificate(value, what string) (*x509.Certificate, error) {
	block, _ := pem.Decode([]byte(value))
	if block == nil || block.Type != "CERTIFICATE" {
		return nil, fmt.Errorf("the stored %s is missing or not PEM", what)
	}
	cert, err := x509.ParseCertificate(block.Bytes)
	if err != nil {
		return nil, fmt.Errorf("the stored %s cannot be parsed: %w", what, err)
	}
	return cert, nil
}

// Store reads and writes the state file. Updates are serialized.
type Store struct {
	dir    string
	access platform.Access
	mu     sync.Mutex
}

// NewStore returns a store for dir. The directory must exist.
func NewStore(dir string, access platform.Access) *Store {
	return &Store{dir: dir, access: access}
}

// Dir returns the state directory.
func (s *Store) Dir() string { return s.dir }

// Access returns who may read the state directory.
func (s *Store) Access() platform.Access { return s.access }

// Path returns the path of the state file.
func (s *Store) Path() string { return filepath.Join(s.dir, FileName) }

// Load reads the state. It returns ErrNotEnrolled when there is no state file.
func (s *Store) Load() (*State, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.load()
}

func (s *Store) load() (*State, error) {
	data, err := os.ReadFile(s.Path())
	if errors.Is(err, os.ErrNotExist) {
		return nil, ErrNotEnrolled
	}
	if err != nil {
		return nil, fmt.Errorf("read state file %s: %w", s.Path(), err)
	}
	var st State
	if err := json.Unmarshal(data, &st); err != nil {
		return nil, fmt.Errorf("state file %s is damaged: %w", s.Path(), err)
	}
	if st.EndpointID == "" || st.Server == "" {
		return nil, fmt.Errorf("state file %s is incomplete", s.Path())
	}
	return &st, nil
}

// Save writes the state atomically.
func (s *Store) Save(st *State) error {
	s.mu.Lock()
	defer s.mu.Unlock()
	return s.save(st)
}

func (s *Store) save(st *State) error {
	data, err := json.MarshalIndent(st, "", "  ")
	if err != nil {
		return fmt.Errorf("encode state: %w", err)
	}
	return platform.WriteFileAtomic(s.Path(), data, s.access)
}

// Update loads the state, applies fn and saves the result in one serialized step.
func (s *Store) Update(fn func(*State) error) (*State, error) {
	s.mu.Lock()
	defer s.mu.Unlock()
	st, err := s.load()
	if err != nil {
		return nil, err
	}
	if err := fn(st); err != nil {
		return nil, err
	}
	if err := s.save(st); err != nil {
		return nil, err
	}
	return st, nil
}

// Delete removes the state file.
func (s *Store) Delete() error {
	s.mu.Lock()
	defer s.mu.Unlock()
	err := os.Remove(s.Path())
	if errors.Is(err, os.ErrNotExist) {
		return nil
	}
	return err
}
