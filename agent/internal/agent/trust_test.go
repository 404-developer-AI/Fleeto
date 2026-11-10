package agent

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"encoding/base64"
	"encoding/pem"
	"math/big"
	"strings"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/checks"
	"github.com/404-developer-AI/Fleeto/agent/internal/keystore"
	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
	"github.com/404-developer-AI/Fleeto/agent/internal/state"
	"github.com/404-developer-AI/Fleeto/agent/internal/testpki"
)

type recordingCollector struct{ specs chan *agentv1.CheckSpec }

func (c recordingCollector) Collect(_ context.Context, spec *agentv1.CheckSpec) []checks.Measurement {
	select {
	case c.specs <- spec:
	default:
	}
	return []checks.Measurement{{Value: 0}}
}

// Whoever can write a state file and a key file, but has no certificate of the instance, must not choose the pinned signing key: the
// agent would otherwise apply a configuration signed with that key from the same file at start, and run its script check as the service
// account without ever reaching a gateway. The agent does not start from a state whose certificate the pinned CA did not issue.
func TestAgentDoesNotStartFromAStateWithAForgedCertificate(t *testing.T) {
	const endpointID = "00000000-0000-4000-8000-000000000001"
	const instanceID = "00000000-0000-4000-8000-000000000002"
	const keyID = "forged-key"
	realCA, err := testpki.NewCA("the real instance CA")
	if err != nil {
		t.Fatal(err)
	}
	dir := t.TempDir()

	// An identity key and a self-signed certificate for it.
	key, ref, err := keystore.Create(dir, state.KeyRef{Kind: keystore.KindFile}, platform.AccessCurrentUser)
	if err != nil {
		t.Fatal(err)
	}
	template := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: endpointID},
		NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour)}
	certDER, err := x509.CreateCertificate(rand.Reader, template, template, key.Public(), key)
	_ = key.Close()
	if err != nil {
		t.Fatal(err)
	}

	// A signing key of its own and a managed configuration with a script check, signed with it.
	pub, priv, _ := ed25519.GenerateKey(rand.Reader)
	cfg := &agentv1.AgentConfig{
		InstanceId: instanceID, EndpointId: endpointID, Version: 7, Tier: agentv1.Tier_TIER_MANAGED, HeartbeatIntervalSeconds: 30,
		Checks: []*agentv1.CheckSpec{{
			Id: "forged", Type: agentv1.CheckType_CHECK_TYPE_SCRIPT, IntervalSeconds: 1,
			Parameters: map[string]string{"script": "forged", "timeout_seconds": "5"},
			Script:     &agentv1.ScriptJob{Language: agentv1.ScriptLanguage_SCRIPT_LANGUAGE_POWERSHELL, Name: "x", Version: 1, Body: "exit 0"},
		}},
	}
	payload, _ := proto.MarshalOptions{Deterministic: true}.Marshal(cfg)
	msg := append(append([]byte(signedconfig.Context), 0), payload...)
	signed, _ := proto.Marshal(&agentv1.SignedConfig{Payload: payload, Signature: ed25519.Sign(priv, msg), KeyId: keyID})

	store := state.NewStore(dir, platform.AccessCurrentUser)
	if err := store.Save(&state.State{
		// A closed local port: the agent never reaches a gateway in this test.
		Server: "127.0.0.1:1", EndpointID: endpointID, InstanceID: instanceID,
		CACertificatePEM: string(pem.EncodeToMemory(&pem.Block{Type: "CERTIFICATE", Bytes: realCA.Cert.Raw})),
		SigningPublicKey: base64.StdEncoding.EncodeToString(pub), SigningKeyID: keyID,
		CertificatePEM: state.EncodeCertificatePEM(certDER), Key: ref,
		AppliedConfig: signed, AppliedConfigVersion: 7,
	}); err != nil {
		t.Fatal(err)
	}

	collector := recordingCollector{specs: make(chan *agentv1.CheckSpec, 1)}
	opts := testOptions(store)
	opts.Collector = collector
	a, err := New(opts)
	if err == nil {
		_ = a.Close()
		t.Fatal("the agent started from a state whose certificate the pinned instance CA did not issue")
	}
	if !strings.Contains(err.Error(), "not issued by the pinned instance CA") {
		t.Fatalf("unexpected error: %v", err)
	}
	select {
	case spec := <-collector.specs:
		t.Fatalf("the check %q of the forged configuration was run", spec.GetId())
	default:
	}
}
