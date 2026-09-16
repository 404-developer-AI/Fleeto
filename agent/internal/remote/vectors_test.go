package remote

import (
	"bytes"
	"crypto/ecdh"
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/sha256"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	"github.com/404-developer-AI/Fleeto/agent/internal/protocol/agentv1"
)

// vectorsFile is shared with the browser implementation: tests/browser/remote-crypto.test.mjs derives the same keys and
// frames from it, so the Go endpoint and the browser can never drift apart unnoticed.
var vectorsFile = filepath.Join("testdata", "vectors.json")

type vectorFrame struct {
	Direction  string `json:"direction"`
	Plaintext  string `json:"plaintext"`
	Ciphertext string `json:"ciphertext"`
}

type vectors struct {
	TokenPayload            string `json:"tokenPayload"`
	BrowserPrivateKey       string `json:"browserPrivateKey"`
	BrowserPublicKey        string `json:"browserPublicKey"`
	EndpointPrivateKey      string `json:"endpointPrivateKey"`
	EndpointPublicKey       string `json:"endpointPublicKey"`
	EndpointCertificateSpki string `json:"endpointCertificateSpki"`
	// EndpointCertificateFingerprint is the lowercase hex SHA-256 of the SPKI, as the instance records it (AgentCertificates.PublicKeyFingerprint).
	EndpointCertificateFingerprint string        `json:"endpointCertificateFingerprint"`
	EndpointSignature              string        `json:"endpointSignature"`
	BrowserToEndpointKey           string        `json:"browserToEndpointKey"`
	EndpointToBrowserKey           string        `json:"endpointToBrowserKey"`
	Frames                         []vectorFrame `json:"frames"`
}

var b64 = base64.StdEncoding

func TestSharedVectorsMatchTheGoImplementation(t *testing.T) {
	if os.Getenv("FLEETO_WRITE_VECTORS") == "1" {
		writeVectors(t)
	}
	data, err := os.ReadFile(vectorsFile)
	if err != nil {
		t.Fatalf("read %s (run with FLEETO_WRITE_VECTORS=1 to create it): %v", vectorsFile, err)
	}
	var v vectors
	if err := json.Unmarshal(data, &v); err != nil {
		t.Fatal(err)
	}
	decode := func(s string) []byte {
		out, err := b64.DecodeString(s)
		if err != nil {
			t.Fatal(err)
		}
		return out
	}
	payload := decode(v.TokenPayload)
	hash := sha256.Sum256(payload)
	browser, err := ecdh.X25519().NewPrivateKey(decode(v.BrowserPrivateKey))
	if err != nil {
		t.Fatal(err)
	}
	keys, err := BrowserHandshake(browser, hash, decode(v.EndpointPublicKey), decode(v.EndpointSignature), decode(v.EndpointCertificateSpki),
		[]string{v.EndpointCertificateFingerprint})
	if err != nil {
		t.Fatalf("the stored endpoint signature does not verify: %v", err)
	}
	if !bytes.Equal(keys.BrowserToEndpoint[:], decode(v.BrowserToEndpointKey)) || !bytes.Equal(keys.EndpointToBrowser[:], decode(v.EndpointToBrowserKey)) {
		t.Fatal("the derived keys differ from the stored vectors")
	}
	send := map[string]*Cipher{}
	send["browserToEndpoint"], _ = NewCipher(keys.BrowserToEndpoint)
	send["endpointToBrowser"], _ = NewCipher(keys.EndpointToBrowser)
	for i, frame := range v.Frames {
		sealed, err := send[frame.Direction].Seal(decode(frame.Plaintext))
		if err != nil || !bytes.Equal(sealed, decode(frame.Ciphertext)) {
			t.Fatalf("frame %d differs from the stored vectors", i)
		}
	}
}

// writeVectors creates the shared vectors from fresh keys. Only run when the key exchange or the frame format changes on purpose.
func writeVectors(t *testing.T) {
	t.Helper()
	f := newFixture(t)
	signed := f.sign(t, f.token(nil))
	token, err := VerifyToken(signed, f.trust, agentv1.Component_COMPONENT_WATCHDOG, f.now)
	if err != nil {
		t.Fatal(err)
	}
	endpointPrivate, _ := ecdh.X25519().GenerateKey(rand.Reader)
	certificateKey, _ := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	handshake, err := endpointHandshake(token, certificateKey, endpointPrivate)
	if err != nil {
		t.Fatal(err)
	}
	spki, _ := x509.MarshalPKIXPublicKey(&certificateKey.PublicKey)
	spkiHash := sha256.Sum256(spki)
	v := vectors{
		EndpointCertificateFingerprint: hex.EncodeToString(spkiHash[:]),
		TokenPayload:                   b64.EncodeToString(signed.Payload),
		BrowserPrivateKey:              b64.EncodeToString(f.browser.Bytes()),
		BrowserPublicKey:               b64.EncodeToString(f.browser.PublicKey().Bytes()),
		EndpointPrivateKey:             b64.EncodeToString(endpointPrivate.Bytes()),
		EndpointPublicKey:              b64.EncodeToString(handshake.PublicKey),
		EndpointCertificateSpki:        b64.EncodeToString(spki),
		EndpointSignature:              b64.EncodeToString(handshake.Signature),
		BrowserToEndpointKey:           b64.EncodeToString(handshake.Keys.BrowserToEndpoint[:]),
		EndpointToBrowserKey:           b64.EncodeToString(handshake.Keys.EndpointToBrowser[:]),
	}
	b2e, _ := NewCipher(handshake.Keys.BrowserToEndpoint)
	e2b, _ := NewCipher(handshake.Keys.EndpointToBrowser)
	add := func(direction string, c *Cipher, plaintext []byte) {
		sealed, _ := c.Seal(plaintext)
		v.Frames = append(v.Frames, vectorFrame{Direction: direction, Plaintext: b64.EncodeToString(plaintext), Ciphertext: b64.EncodeToString(sealed)})
	}
	add("endpointToBrowser", e2b, append([]byte{FrameHello}, `{"hostname":"SRV-01"}`...))
	add("browserToEndpoint", b2e, append([]byte{FrameOpen}, `{"channel":1,"service":"terminal","shell":"powershell","cols":120,"rows":30}`...))
	add("browserToEndpoint", b2e, []byte{FrameData, 0, 1, 'd', 'i', 'r', '\r'})
	add("endpointToBrowser", e2b, []byte{FrameData, 0, 1, 0xE2, 0x82, 0xAC})
	data, _ := json.MarshalIndent(v, "", "  ")
	if err := os.MkdirAll(filepath.Dir(vectorsFile), 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(vectorsFile, append(data, '\n'), 0o644); err != nil {
		t.Fatal(err)
	}
}
