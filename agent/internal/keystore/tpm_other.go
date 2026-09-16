//go:build !linux

package keystore

import (
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
)

// DefaultTPMFileName is the file holding the TPM key blobs for KindTPM; only Linux has one.
const DefaultTPMFileName = "agent-identity.tpmkey"

// TPMAvailable reports whether this endpoint has a usable TPM 2.0 key store. On Windows the TPM is reached through CNG (KindCNG),
// so only Linux has a TPM key store of its own.
func TPMAvailable() bool { return false }

func createTPMKey(string, platform.Access) (Key, error) { return nil, ErrUnsupported }

func openTPMKey(string) (Key, error) { return nil, ErrUnsupported }

func deleteTPMKey(string) error { return ErrUnsupported }

func tpmKeyPath(string, state.KeyRef) string { return "" }
