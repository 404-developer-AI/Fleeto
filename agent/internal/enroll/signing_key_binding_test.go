package enroll

// A gateway holds a certificate of the instance CA (so the agent's TLS check passes) but never the CA key. These tests prove
// that it cannot make an enrolling agent pin an instance signing key that fleeto-signer did not vouch for with the CA key.

import (
	"context"
	"crypto/ed25519"
	"crypto/rand"
	"crypto/x509"
	"encoding/hex"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
	"github.com/404-developer-AI/Fleeto/agent/internal/testpki"
)

// signerResponse is what fleeto-signer answers: a certificate from the CA and the signing key bound by the CA.
func signerResponse(t *testing.T, ca *testpki.CA, req *agentv1.EnrollRequest, signingKey ed25519.PublicKey, keyID string) *agentv1.EnrollResponse {
	t.Helper()
	certDER, err := ca.IssueAgent(req.GetCsrDer(), testEndpoint, testInstance, time.Now().Add(-time.Minute), 90*24*time.Hour)
	if err != nil {
		t.Fatal(err)
	}
	return &agentv1.EnrollResponse{
		EndpointId: testEndpoint, InstanceId: testInstance, CertificateDer: certDER, CaCertificateDer: ca.Cert.Raw,
		InstanceSigningPublicKey: signingKey, InstanceSigningKeyId: keyID,
		InstanceSigningKeyCaSignature: ca.SignMessage(SigningKeyMessage(testInstance, keyID, signingKey)),
	}
}

func mustSigningKey(t *testing.T) ed25519.PublicKey {
	t.Helper()
	pub, _, err := ed25519.GenerateKey(rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	return pub
}

func TestEnrollmentPinsTheSigningKeyTheCAVouchesFor(t *testing.T) {
	ca := mustCA(t)
	g := newFakeGateway(t, ca, mustServerCert(t, ca, "127.0.0.1"))
	genuine := mustSigningKey(t)
	g.respond = func(w http.ResponseWriter, req *agentv1.EnrollRequest) {
		writeResponse(w, signerResponse(t, ca, req, genuine, "genuine000000001"))
	}
	res, err := Enroll(context.Background(), newRequest(t, g.addr(), ca.Fingerprint()))
	if err != nil {
		t.Fatalf("enroll: %v", err)
	}
	if !res.SigningKey.Equal(genuine) || res.SigningKeyID != "genuine000000001" {
		t.Fatal("the agent did not pin the genuine signing key")
	}
}

// A signature made by InternalCertificateAuthority.SignInstanceSigningKey on the server (.NET), so the agent and the signer
// provably agree on the message layout and the signature encoding.
func TestSigningKeyBindingFromTheServerVerifies(t *testing.T) {
	const (
		caHex = "308201bd30820164a003020102020900fbd531da64c7a0dc300a06082a8648ce3d0403023041310f300d060355040a1306466c6565746f312e302c" +
			"06035504031325466c6565746f20496e7374616e636520434120726d6d2e766563746f722e6578616d706c65301e170d3236313130393233353530305a" +
			"170d3336313130373030303030305a3041310f300d060355040a1306466c6565746f312e302c06035504031325466c6565746f20496e7374616e636520" +
			"434120726d6d2e766563746f722e6578616d706c653059301306072a8648ce3d020106082a8648ce3d030107034200040568b311be8023914f7ecf06ea" +
			"d1dad63331a37b31306c18fcb354d3592a6c2985df87060b08f344befe9f0a255f3e77836956a9567cacce45dbd136d24ea39ba345304330120603551d" +
			"130101ff040830060101ff020100300e0603551d0f0101ff040403020106301d0603551d0e041604144e2c93445d4f0d17366946bb8b8875e3233113f8" +
			"300a06082a8648ce3d0403020347003044022074e18496f35fa9e988b5c5f1b73444182b24d1d07efc096d5345391984dad0d102204188f09300360445" +
			"259686a4a8226cb766a9b24174d16285b5b3cfdef9259f61"
		sigHex = "304402201ea5edf29e1ab29e295b9945d5d8fe5ed2e519daafeb00b5a177b78e873379a702205b4ef7219fc33161d329c02d23d344b71a5c082aca9b9" +
			"6ee4d203474346487a1"
		keyHex = "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a"
	)
	caDER, _ := hex.DecodeString(caHex)
	sig, _ := hex.DecodeString(sigHex)
	key, _ := hex.DecodeString(keyHex)
	ca, err := x509.ParseCertificate(caDER)
	if err != nil {
		t.Fatal(err)
	}
	if !VerifySigningKeyBinding(ca, testInstance, testKeyID, key, sig) {
		t.Fatal("a binding signed by fleeto-signer does not verify in the agent")
	}
	key[0] ^= 1
	if VerifySigningKeyBinding(ca, testInstance, testKeyID, key, sig) {
		t.Fatal("a binding verifies for another key")
	}
}

func TestEnrollmentRefusesASigningKeyTheCADidNotSign(t *testing.T) {
	ca := mustCA(t)
	// A key that can sign but is not the instance CA, like the key of the gateway's own TLS certificate.
	other := mustCA(t)
	attacker := mustSigningKey(t)
	const attackerKeyID = "attacker00000001"

	cases := map[string]func(resp *agentv1.EnrollResponse){
		// The gateway swaps key and key id and keeps the signer's genuine CA signature.
		"substituted key, genuine signature": func(resp *agentv1.EnrollResponse) {
			resp.InstanceSigningPublicKey, resp.InstanceSigningKeyId = attacker, attackerKeyID
		},
		"substituted key only": func(resp *agentv1.EnrollResponse) {
			resp.InstanceSigningPublicKey = attacker
		},
		"substituted key id only": func(resp *agentv1.EnrollResponse) {
			resp.InstanceSigningKeyId = attackerKeyID
		},
		"substituted key signed by another key": func(resp *agentv1.EnrollResponse) {
			resp.InstanceSigningPublicKey, resp.InstanceSigningKeyId = attacker, attackerKeyID
			resp.InstanceSigningKeyCaSignature = other.SignMessage(SigningKeyMessage(testInstance, attackerKeyID, attacker))
		},
		"no signature": func(resp *agentv1.EnrollResponse) {
			resp.InstanceSigningKeyCaSignature = nil
		},
		"signature for another instance": func(resp *agentv1.EnrollResponse) {
			resp.InstanceSigningKeyCaSignature = ca.SignMessage(SigningKeyMessage(
				"00000000-0000-4000-8000-000000000000", resp.GetInstanceSigningKeyId(), resp.GetInstanceSigningPublicKey()))
		},
		"signature without the domain separation": func(resp *agentv1.EnrollResponse) {
			resp.InstanceSigningKeyCaSignature = ca.SignMessage(resp.GetInstanceSigningPublicKey())
		},
	}
	for name, tamper := range cases {
		t.Run(name, func(t *testing.T) {
			g := newFakeGateway(t, ca, mustServerCert(t, ca, "127.0.0.1"))
			genuine := mustSigningKey(t)
			g.respond = func(w http.ResponseWriter, req *agentv1.EnrollRequest) {
				resp := signerResponse(t, ca, req, genuine, "genuine000000001")
				tamper(resp)
				writeResponse(w, resp)
			}
			res, err := Enroll(context.Background(), newRequest(t, g.addr(), ca.Fingerprint()))
			if err == nil {
				t.Fatalf("the agent pinned signing key id %q, which the instance CA did not sign", res.SigningKeyID)
			}
			if !strings.Contains(err.Error(), "not signed by the instance CA") {
				t.Fatalf("unexpected error: %v", err)
			}
		})
	}
}
