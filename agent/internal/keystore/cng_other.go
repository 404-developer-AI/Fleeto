//go:build !windows

package keystore

import "github.com/404-developer-AI/Fleeto/agent/internal/state"

func createCNGKey(ref state.KeyRef) (Key, state.KeyRef, error) { return nil, ref, ErrUnsupported }

func openCNGKey(state.KeyRef) (Key, error) { return nil, ErrUnsupported }

func deleteCNGKey(state.KeyRef) error { return ErrUnsupported }
