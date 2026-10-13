package uninstall

import (
	"crypto/ed25519"
	"crypto/rand"
	"strings"
	"testing"
	"time"

	"google.golang.org/protobuf/proto"
	"google.golang.org/protobuf/types/known/timestamppb"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/signedconfig"
)

const (
	instanceID = "11111111-1111-1111-1111-111111111111"
	endpointID = "22222222-2222-2222-2222-222222222222"
)

func sign(t *testing.T, key ed25519.PrivateKey, keyID string, command *agentv1.UninstallCommand) *agentv1.SignedUninstall {
	t.Helper()
	payload, err := proto.Marshal(command)
	if err != nil {
		t.Fatal(err)
	}
	message := append(append([]byte(Context), 0), payload...)
	return &agentv1.SignedUninstall{Payload: payload, Signature: ed25519.Sign(key, message), KeyId: keyID}
}

func command(validUntil time.Time) *agentv1.UninstallCommand {
	return &agentv1.UninstallCommand{
		UninstallId: "33333333-3333-3333-3333-333333333333", InstanceId: instanceID, EndpointId: endpointID,
		ValidUntil: timestamppb.New(validUntil), RequestedBy: "Tess Tech",
	}
}

func TestVerifyAcceptsOnlyACommandSignedForThisEndpoint(t *testing.T) {
	public, private, _ := ed25519.GenerateKey(rand.Reader)
	_, other, _ := ed25519.GenerateKey(rand.Reader)
	trust := signedconfig.Trust{SigningKey: public, KeyID: "key-1", InstanceID: instanceID, EndpointID: endpointID}
	now := time.Now()

	got, err := Verify(sign(t, private, "key-1", command(now.Add(24*time.Hour))), trust, now)
	if err != nil || got.GetRequestedBy() != "Tess Tech" {
		t.Fatalf("a valid command was refused: %v", err)
	}

	wrongEndpoint := command(now.Add(time.Hour))
	wrongEndpoint.EndpointId = "44444444-4444-4444-4444-444444444444"
	cases := map[string]struct {
		signed *agentv1.SignedUninstall
		want   string
	}{
		"another key":      {sign(t, other, "key-1", command(now.Add(time.Hour))), "does not verify"},
		"another key id":   {sign(t, private, "key-2", command(now.Add(time.Hour))), "pinned instance signing key"},
		"another endpoint": {sign(t, private, "key-1", wrongEndpoint), "not this endpoint"},
		"expired":          {sign(t, private, "key-1", command(now.Add(-time.Hour))), "expired"},
		"too long":         {sign(t, private, "key-1", command(now.Add(60*24*time.Hour))), "longer than 31 days"},
		"empty":            {&agentv1.SignedUninstall{}, "cannot be read"},
	}
	for name, c := range cases {
		if _, err := Verify(c.signed, trust, now); err == nil || !strings.Contains(err.Error(), c.want) {
			t.Errorf("%s: got %v, want an error containing %q", name, err, c.want)
		}
	}

	// A job signature (another context) over the same payload is no uninstall command.
	payload, _ := proto.Marshal(command(now.Add(time.Hour)))
	asJob := &agentv1.SignedUninstall{Payload: payload, KeyId: "key-1",
		Signature: ed25519.Sign(private, append(append([]byte("fleeto-job-v1"), 0), payload...))}
	if _, err := Verify(asJob, trust, now); err == nil {
		t.Error("a signature in another context was accepted")
	}
}
