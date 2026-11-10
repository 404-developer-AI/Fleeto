package state

import (
	"crypto/ecdsa"
	"crypto/elliptic"
	"crypto/rand"
	"crypto/x509"
	"crypto/x509/pkix"
	"errors"
	"math/big"
	"testing"
	"time"

	"github.com/404-developer-AI/Fleeto/agent/internal/platform"
	"github.com/404-developer-AI/Fleeto/agent/internal/testpki"
)

// Only a certificate the pinned instance CA issued for this endpoint and instance loads: whoever can write a state file without the CA key
// must not be able to choose the trust of the agent. An expired certificate still loads, so the agent can recover it.
func TestIssuedCertificateNeedsThePinnedCAEndpointAndInstance(t *testing.T) {
	const endpointID = "0f5d8c7e-8c1a-4b7e-9d59-5f6a1d2c3b4a"
	const instanceID = "6a1d2c3b-4a0f-4d8c-8e1a-7b9d595f6a1d"
	ca, err := testpki.NewCA("instance")
	if err != nil {
		t.Fatal(err)
	}
	other, err := testpki.NewCA("other instance")
	if err != nil {
		t.Fatal(err)
	}
	key, err := ecdsa.GenerateKey(elliptic.P256(), rand.Reader)
	if err != nil {
		t.Fatal(err)
	}
	csr, err := x509.CreateCertificateRequest(rand.Reader, &x509.CertificateRequest{Subject: pkix.Name{CommonName: "host"}}, key)
	if err != nil {
		t.Fatal(err)
	}
	issue := func(by *testpki.CA, endpoint, instance string, notBefore time.Time) string {
		der, err := by.IssueAgent(csr, endpoint, instance, notBefore, time.Hour)
		if err != nil {
			t.Fatal(err)
		}
		return EncodeCertificatePEM(der)
	}
	selfTemplate := &x509.Certificate{SerialNumber: big.NewInt(1), Subject: pkix.Name{CommonName: endpointID},
		NotBefore: time.Now().Add(-time.Hour), NotAfter: time.Now().Add(time.Hour)}
	selfSigned, err := x509.CreateCertificate(rand.Reader, selfTemplate, selfTemplate, &key.PublicKey, key)
	if err != nil {
		t.Fatal(err)
	}
	withCertificate := func(certPEM string) *State {
		return &State{EndpointID: endpointID, InstanceID: instanceID, CertificatePEM: certPEM, CACertificatePEM: EncodeCertificatePEM(ca.Cert.Raw)}
	}

	for name, certPEM := range map[string]string{
		"valid":   issue(ca, endpointID, instanceID, time.Now().Add(-time.Minute)),
		"expired": issue(ca, endpointID, instanceID, time.Now().Add(-48*time.Hour)),
	} {
		if _, err := withCertificate(certPEM).IssuedCertificate(); err != nil {
			t.Errorf("%s: a certificate of the pinned CA for this endpoint must load: %v", name, err)
		}
	}
	for name, certPEM := range map[string]string{
		"self-signed":      EncodeCertificatePEM(selfSigned),
		"another CA":       issue(other, endpointID, instanceID, time.Now().Add(-time.Minute)),
		"another endpoint": issue(ca, "11111111-2222-4333-8444-555555555555", instanceID, time.Now().Add(-time.Minute)),
		"another instance": issue(ca, endpointID, "11111111-2222-4333-8444-555555555555", time.Now().Add(-time.Minute)),
	} {
		if _, err := withCertificate(certPEM).IssuedCertificate(); err == nil {
			t.Errorf("%s: the certificate must be refused", name)
		}
	}
}

func TestStoreRoundTripAndUpdate(t *testing.T) {
	store := NewStore(t.TempDir(), platform.AccessCurrentUser)
	if _, err := store.Load(); !errors.Is(err, ErrNotEnrolled) {
		t.Fatalf("expected ErrNotEnrolled, got %v", err)
	}
	st := &State{Server: "localhost:7200", EndpointID: "e1", InstanceID: "i1", AppliedConfig: []byte{1, 2, 3}}
	if err := store.Save(st); err != nil {
		t.Fatal(err)
	}
	if _, err := store.Update(func(s *State) error { s.AppliedConfigVersion = 7; return nil }); err != nil {
		t.Fatal(err)
	}
	got, err := store.Load()
	if err != nil {
		t.Fatal(err)
	}
	if got.AppliedConfigVersion != 7 || string(got.AppliedConfig) != "\x01\x02\x03" || got.InstanceID != "i1" {
		t.Fatalf("unexpected state %+v", got)
	}
}
