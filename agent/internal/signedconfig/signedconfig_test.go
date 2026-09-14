package signedconfig

import (
	"crypto/ed25519"
	"crypto/rand"
	"errors"
	"strings"
	"testing"

	"google.golang.org/protobuf/proto"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

const (
	instanceID = "6f1d3c1e-5a4b-4f7e-9a31-2b8f0c1d2e3f"
	endpointID = "0b6a4e2c-1d3f-4a5b-8c7d-9e0f1a2b3c4d"
	keyID      = "a1b2c3d4e5f60718"
)

func newKey(t *testing.T) (ed25519.PublicKey, ed25519.PrivateKey) {
	t.Helper()
	pub, priv, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return pub, priv
}

// sign mirrors Ed25519.Sign(privateKey, SignatureContexts.AgentConfig, payload) on the server.
func sign(t *testing.T, priv ed25519.PrivateKey, cfg *agentv1.AgentConfig) *agentv1.SignedConfig {
	t.Helper()
	payload, err := proto.MarshalOptions{Deterministic: true}.Marshal(cfg)
	if err != nil {
		t.Fatal(err)
	}
	msg := append(append([]byte(Context), 0), payload...)
	return &agentv1.SignedConfig{Payload: payload, Signature: ed25519.Sign(priv, msg), KeyId: keyID}
}

func managedConfig(version uint64) *agentv1.AgentConfig {
	return &agentv1.AgentConfig{
		InstanceId: instanceID, EndpointId: endpointID, Version: version, Tier: agentv1.Tier_TIER_MANAGED,
		HeartbeatIntervalSeconds: 30,
		Checks:                   []*agentv1.CheckSpec{{Id: "c1", Type: agentv1.CheckType_CHECK_TYPE_CPU_USAGE, IntervalSeconds: 60}},
	}
}

func trustFor(pub ed25519.PublicKey) Trust {
	return Trust{SigningKey: pub, KeyID: keyID, InstanceID: instanceID, EndpointID: endpointID}
}

func TestValidConfigIsAccepted(t *testing.T) {
	pub, priv := newKey(t)
	cfg, err := Verify(sign(t, priv, managedConfig(3)), trustFor(pub), 2, nil)
	if err != nil {
		t.Fatalf("expected acceptance, got %v", err)
	}
	if cfg.GetVersion() != 3 || len(EffectiveChecks(cfg)) != 1 {
		t.Fatalf("unexpected config %v", cfg)
	}
}

func TestConfigSignedWithAnotherKeyIsRefused(t *testing.T) {
	pub, _ := newKey(t)
	_, otherPriv := newKey(t)
	_, err := Verify(sign(t, otherPriv, managedConfig(3)), trustFor(pub), 0, nil)
	if err == nil || !strings.Contains(err.Error(), "does not verify") {
		t.Fatalf("expected signature refusal, got %v", err)
	}
}

func TestConfigWithOtherKeyIDIsRefused(t *testing.T) {
	pub, priv := newKey(t)
	sc := sign(t, priv, managedConfig(3))
	sc.KeyId = "ffffffffffffffff"
	if _, err := Verify(sc, trustFor(pub), 0, nil); err == nil {
		t.Fatal("expected refusal for a different key id")
	}
}

func TestConfigForAnotherEndpointIsRefused(t *testing.T) {
	pub, priv := newKey(t)
	cfg := managedConfig(3)
	cfg.EndpointId = "11111111-2222-3333-4444-555555555555"
	_, err := Verify(sign(t, priv, cfg), trustFor(pub), 0, nil)
	if err == nil || !strings.Contains(err.Error(), "endpoint") {
		t.Fatalf("expected endpoint refusal, got %v", err)
	}
}

func TestConfigForAnotherInstanceIsRefused(t *testing.T) {
	pub, priv := newKey(t)
	cfg := managedConfig(3)
	cfg.InstanceId = "11111111-2222-3333-4444-555555555555"
	if _, err := Verify(sign(t, priv, cfg), trustFor(pub), 0, nil); err == nil {
		t.Fatal("expected instance refusal")
	}
}

func TestOlderOrEqualVersionIsRefused(t *testing.T) {
	pub, priv := newKey(t)
	for _, applied := range []uint64{3, 4} {
		if _, err := Verify(sign(t, priv, managedConfig(3)), trustFor(pub), applied, []byte("other")); err == nil {
			t.Fatalf("expected refusal of version 3 with applied version %d", applied)
		}
	}
}

func TestIdenticalResendIsReportedAsAlreadyApplied(t *testing.T) {
	pub, priv := newKey(t)
	sc := sign(t, priv, managedConfig(3))
	cfg, err := Verify(sc, trustFor(pub), 3, sc.GetPayload())
	if !errors.Is(err, ErrAlreadyApplied) || cfg == nil {
		t.Fatalf("expected ErrAlreadyApplied, got %v", err)
	}
}

func TestTamperedPayloadIsRefused(t *testing.T) {
	pub, priv := newKey(t)
	sc := sign(t, priv, managedConfig(3))
	for i := range sc.Payload {
		tampered := proto.Clone(sc).(*agentv1.SignedConfig)
		tampered.Payload[i] ^= 0x01
		if _, err := Verify(tampered, trustFor(pub), 0, nil); err == nil {
			t.Fatalf("expected refusal after flipping a bit in byte %d", i)
		}
	}
	truncated := proto.Clone(sc).(*agentv1.SignedConfig)
	truncated.Payload = truncated.Payload[:len(truncated.Payload)-1]
	if _, err := Verify(truncated, trustFor(pub), 0, nil); err == nil {
		t.Fatal("expected refusal of a truncated payload")
	}
	badSig := proto.Clone(sc).(*agentv1.SignedConfig)
	badSig.Signature[0] ^= 0x80
	if _, err := Verify(badSig, trustFor(pub), 0, nil); err == nil {
		t.Fatal("expected refusal of a modified signature")
	}
}

func TestSignatureWithoutContextIsRefused(t *testing.T) {
	pub, priv := newKey(t)
	payload, _ := proto.Marshal(managedConfig(3))
	sc := &agentv1.SignedConfig{Payload: payload, Signature: ed25519.Sign(priv, payload), KeyId: keyID}
	if _, err := Verify(sc, trustFor(pub), 0, nil); err == nil {
		t.Fatal("a signature over the bare payload must not verify")
	}
}

func TestMissingPinnedKeyRefusesEverything(t *testing.T) {
	_, priv := newKey(t)
	if _, err := Verify(sign(t, priv, managedConfig(3)), Trust{InstanceID: instanceID, EndpointID: endpointID, KeyID: keyID}, 0, nil); err == nil {
		t.Fatal("expected refusal without a pinned key")
	}
}

func TestAgentOnlyConfigWithChecksRunsNoChecks(t *testing.T) {
	pub, priv := newKey(t)
	for _, tier := range []agentv1.Tier{agentv1.Tier_TIER_AGENT_ONLY, agentv1.Tier_TIER_UNSPECIFIED} {
		cfg := managedConfig(5)
		cfg.Tier = tier
		verified, err := Verify(sign(t, priv, cfg), trustFor(pub), 0, nil)
		if err != nil {
			t.Fatal(err)
		}
		if checks := EffectiveChecks(verified); len(checks) != 0 {
			t.Fatalf("tier %v must run no checks, got %d", tier, len(checks))
		}
	}
}
