using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fleetify.Infrastructure.Identity;
using Fleetify.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;

namespace Fleetify.Infrastructure.Tests;

/// <summary>
/// Guarantees of the cryptographic primitives (0.1.0): domain-separated signatures, tokens that only parse in their
/// exact shape, Argon2id hashes that verify and ask for a rehash when parameters change, and certificates issued only
/// from valid P-256 CSRs with the endpoint identity set by the signer.
/// </summary>
public class CryptographyTests
{
    [Fact]
    public void Signature_for_one_context_does_not_verify_in_another()
    {
        var (privateKey, publicKey) = Ed25519.GenerateKeyPair();
        var payload = Encoding.UTF8.GetBytes("payload");

        var signature = Ed25519.Sign(privateKey, SignatureContexts.License, payload);

        Assert.True(Ed25519.Verify(publicKey, SignatureContexts.License, payload, signature));
        Assert.False(Ed25519.Verify(publicKey, SignatureContexts.AgentConfig, payload, signature));
        Assert.False(Ed25519.Verify(publicKey, SignatureContexts.License, Encoding.UTF8.GetBytes("payloaD"), signature));
    }

    [Fact]
    public void Raw_release_signature_verifies_and_rejects_tampering()
    {
        var (privateKey, publicKey) = Ed25519.GenerateKeyPair();
        var file = RandomNumberGenerator.GetBytes(4096);

        var signature = Ed25519.SignRaw(privateKey, file);
        Assert.True(Ed25519.VerifyRaw(publicKey, file, signature));

        file[100] ^= 1;
        Assert.False(Ed25519.VerifyRaw(publicKey, file, signature));
    }

    [Fact]
    public void Opaque_token_parses_only_with_its_own_prefix_and_shape()
    {
        var (token, id, hash) = OpaqueTokens.Create(OpaqueTokens.EnrollmentPrefix);

        Assert.True(OpaqueTokens.TryParse(token, OpaqueTokens.EnrollmentPrefix, out var parsedId, out var parsedHash));
        Assert.Equal(id, parsedId);
        Assert.Equal(hash, parsedHash);

        Assert.False(OpaqueTokens.TryParse(token, OpaqueTokens.SetupPrefix, out _, out _));
        Assert.False(OpaqueTokens.TryParse(token[..^2], OpaqueTokens.EnrollmentPrefix, out _, out _));
        Assert.False(OpaqueTokens.TryParse("fet_nothex_abc", OpaqueTokens.EnrollmentPrefix, out _, out _));
        Assert.False(OpaqueTokens.TryParse(null, OpaqueTokens.EnrollmentPrefix, out _, out _));
    }

    [Fact]
    public void Token_hash_differs_for_a_different_secret_with_the_same_id()
    {
        var (token, _, hash) = OpaqueTokens.Create(OpaqueTokens.EnrollmentPrefix);
        var parts = token.Split('_');
        var forged = $"{parts[0]}_{parts[1]}_{System.Buffers.Text.Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32))}";

        Assert.True(OpaqueTokens.TryParse(forged, OpaqueTokens.EnrollmentPrefix, out _, out var forgedHash));
        Assert.False(SecureCompare.HexEquals(hash, forgedHash));
    }

    [Fact]
    public void Argon2id_hash_verifies_the_right_password_only()
    {
        var hasher = new Argon2idPasswordHasher();
        var user = new ApplicationUser();

        var hash = hasher.HashPassword(user, "correct horse battery staple");

        Assert.StartsWith("$argon2id$v=19$m=47104,t=1,p=1$", hash);
        Assert.Equal(PasswordVerificationResult.Success, hasher.VerifyHashedPassword(user, hash, "correct horse battery staple"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, hash, "wrong password"));
    }

    [Fact]
    public void Argon2id_hash_with_old_parameters_asks_for_a_rehash()
    {
        var hasher = new Argon2idPasswordHasher();
        var user = new ApplicationUser();
        var salt = RandomNumberGenerator.GetBytes(16);
        using var argon2 = new Konscious.Security.Cryptography.Argon2id(Encoding.UTF8.GetBytes("secret password"))
        {
            Salt = salt, MemorySize = 19456, Iterations = 2, DegreeOfParallelism = 1
        };
        var old = $"$argon2id$v=19$m=19456,t=2,p=1${Convert.ToBase64String(salt)}${Convert.ToBase64String(argon2.GetBytes(32))}";

        Assert.Equal(PasswordVerificationResult.SuccessRehashNeeded, hasher.VerifyHashedPassword(user, old, "secret password"));
    }

    [Fact]
    public void Argon2id_refuses_malformed_or_absurd_hashes()
    {
        var hasher = new Argon2idPasswordHasher();
        var user = new ApplicationUser();

        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, "AQAAAAEAACcQ", "x"));
        Assert.Equal(PasswordVerificationResult.Failed, hasher.VerifyHashedPassword(user, "$argon2id$v=19$m=99999999,t=1,p=1$AAAAAAAAAAAAAAAAAAAAAA==$AAAAAAAAAAAAAAAAAAAAAA==", "x"));
    }

    [Fact]
    public void Agent_certificate_is_issued_from_a_p256_csr_with_the_endpoint_identity()
    {
        var now = DateTime.UtcNow;
        var ca = InternalCertificateAuthority.CreateCa("rmm.test.example", now);
        using var agentKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=ignored-by-signer", agentKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        var endpointId = Guid.NewGuid();

        var issued = InternalCertificateAuthority.IssueAgentCertificate(ca.CertificateDer, ca.PrivateKeyPkcs8, csr, endpointId, Guid.NewGuid(), now);

        using var certificate = X509CertificateLoader.LoadCertificate(issued.CertificateDer);
        using var caCertificate = X509CertificateLoader.LoadCertificate(ca.CertificateDer);
        Assert.Equal(endpointId, InternalCertificateAuthority.EndpointIdFromCertificate(certificate));
        Assert.Contains(endpointId.ToString("D"), certificate.Subject);
        Assert.Equal(KeyIds.Sha256Hex(agentKey.ExportSubjectPublicKeyInfo()), issued.PublicKeyFingerprint);
        Assert.Equal(issued.PublicKeyFingerprint, InternalCertificateAuthority.CsrPublicKeyFingerprint(csr));
        Assert.True(certificate.NotAfter.ToUniversalTime() <= now.AddDays(90).AddSeconds(1));

        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(caCertificate);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        Assert.True(chain.Build(certificate));
    }

    [Fact]
    public void Csr_with_an_rsa_key_is_refused()
    {
        var now = DateTime.UtcNow;
        var ca = InternalCertificateAuthority.CreateCa("rmm.test.example", now);
        using var rsa = RSA.Create(2048);
        var csr = new CertificateRequest("CN=x", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1).CreateSigningRequest();

        Assert.Throws<InvalidOperationException>(() =>
            InternalCertificateAuthority.IssueAgentCertificate(ca.CertificateDer, ca.PrivateKeyPkcs8, csr, Guid.NewGuid(), Guid.NewGuid(), now));
    }

    [Fact]
    public void Tampered_csr_is_refused()
    {
        var now = DateTime.UtcNow;
        var ca = InternalCertificateAuthority.CreateCa("rmm.test.example", now);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=x", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        csr[^10] ^= 0xFF;

        Assert.ThrowsAny<Exception>(() =>
            InternalCertificateAuthority.IssueAgentCertificate(ca.CertificateDer, ca.PrivateKeyPkcs8, csr, Guid.NewGuid(), Guid.NewGuid(), now));
    }

    [Fact]
    public void Signer_key_box_is_bound_to_its_context()
    {
        var signerKey = new SignerKey(RandomNumberGenerator.GetBytes(32));
        var sealedKey = signerKey.Seal("secret"u8, "InstanceSigningKeys|abc");

        Assert.Equal("secret"u8.ToArray(), signerKey.Open(sealedKey, "InstanceSigningKeys|abc"));
        Assert.ThrowsAny<CryptographicException>(() => signerKey.Open(sealedKey, "InstanceSigningKeys|other"));
        Assert.ThrowsAny<CryptographicException>(() => new SignerKey(RandomNumberGenerator.GetBytes(32)).Open(sealedKey, "InstanceSigningKeys|abc"));
    }
}
