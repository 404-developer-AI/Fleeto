package agent

import (
	"context"
	"encoding/base64"
	"errors"
	"fmt"
	"log/slog"
	"os"
	"path/filepath"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/enroll"
	"github.com/404-developer-AI/Fleeto/agent/internal/inventory"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/version"
)

// BufferFileName is the result buffer inside the state directory.
const BufferFileName = "results.db"

// EnrollParams describes an enrollment.
type EnrollParams struct {
	StateDir      string
	Access        platform.Access
	Key           state.KeyRef
	Server        string
	Token         string
	CAFingerprint string
	Logger        *slog.Logger
	Timeout       time.Duration
}

// Enroll creates a new identity key, enrolls with the gateway and saves the state. On failure nothing is left behind:
// the key is deleted and no state file is written. Any previous state, key and result buffer in the directory are
// replaced, because sequences and configuration versions belong to the previous endpoint.
func Enroll(ctx context.Context, p EnrollParams) (*state.State, error) {
	if _, err := enroll.ValidateServer(p.Server); err != nil {
		return nil, err
	}
	if err := enroll.ValidateToken(p.Token); err != nil {
		return nil, err
	}
	if _, err := enroll.ParseFingerprint(p.CAFingerprint); err != nil {
		return nil, err
	}
	if err := platform.EnsureProtectedDir(p.StateDir, p.Access); err != nil {
		return nil, err
	}
	store := state.NewStore(p.StateDir, p.Access)
	if existing, err := store.Load(); err == nil && !existing.Revoked && !certificateExpired(existing, time.Now()) {
		return nil, fmt.Errorf("the agent is already enrolled as endpoint %s; uninstall it first to enroll again", existing.EndpointID)
	}

	// A stale key from an earlier, failed or revoked enrollment is useless; remove it so creation starts clean.
	if err := keystore.Delete(p.StateDir, withDefaults(p.Key)); err != nil {
		return nil, fmt.Errorf("remove the previous identity key: %w", err)
	}
	key, ref, err := keystore.Create(p.StateDir, p.Key, p.Access)
	if err != nil {
		return nil, err
	}
	defer key.Close()
	p.Logger.Info("identity key created", "store", key.Description())

	hostname := inventory.Hostname()
	res, err := enroll.Enroll(ctx, enroll.Request{
		Server: p.Server, Token: p.Token, CAFingerprint: p.CAFingerprint, Key: key, Hostname: hostname,
		OS: inventory.OSInfo(ctx), AgentVersion: version.Version, Timeout: p.Timeout,
	})
	if err != nil {
		if delErr := keystore.Delete(p.StateDir, ref); delErr != nil {
			p.Logger.Error("could not remove the identity key after a failed enrollment", "error", delErr)
		}
		return nil, err
	}

	st := &state.State{
		Server:           p.Server,
		EndpointID:       res.EndpointID,
		InstanceID:       res.InstanceID,
		CACertificatePEM: state.EncodeCertificatePEM(res.CACertificateDER),
		SigningPublicKey: base64.StdEncoding.EncodeToString(res.SigningKey),
		SigningKeyID:     res.SigningKeyID,
		CertificatePEM:   state.EncodeCertificatePEM(res.CertificateDER),
		Key:              ref,
		EnrolledAt:       time.Now().UTC(),
	}
	// Jobs of a previous enrollment belong to that endpoint.
	if err := os.RemoveAll(filepath.Join(p.StateDir, JobsDirName)); err != nil {
		_ = keystore.Delete(p.StateDir, ref)
		return nil, fmt.Errorf("remove the jobs of the previous enrollment: %w", err)
	}
	if err := os.Remove(filepath.Join(p.StateDir, BufferFileName)); err != nil && !errors.Is(err, os.ErrNotExist) {
		_ = keystore.Delete(p.StateDir, ref)
		return nil, fmt.Errorf("remove the result buffer of the previous enrollment: %w", err)
	}
	if err := store.Save(st); err != nil {
		_ = keystore.Delete(p.StateDir, ref)
		return nil, fmt.Errorf("save enrollment state: %w", err)
	}
	p.Logger.Info("enrolled", "endpointId", st.EndpointID, "instanceId", st.InstanceID, "server", st.Server,
		"certificateExpires", res.Certificate.NotAfter.UTC().Format(time.RFC3339))
	return st, nil
}

// certificateExpired reports whether the stored certificate has passed its end date (or cannot be read). Such an agent may be
// enrolled again: with an "enroll again" token it takes over its existing endpoint.
func certificateExpired(st *state.State, now time.Time) bool {
	cert, err := st.Certificate()
	return err != nil || now.After(cert.NotAfter)
}

func withDefaults(ref state.KeyRef) state.KeyRef {
	switch ref.Kind {
	case keystore.KindFile:
		if ref.File == "" {
			ref.File = keystore.DefaultFileName
		}
	case keystore.KindCNG:
		if ref.Name == "" {
			ref.Name = keystore.DefaultCNGName
		}
	}
	return ref
}
