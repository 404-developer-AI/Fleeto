using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Fleeto.Signer.Processing;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees that enrollment re-checks the token and the CSR itself, creates an agent-only endpoint with a
/// certificate from the instance CA and a signed first configuration, audits without the token, and refuses every
/// invalid token or CSR without writing anything.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class AgentEnrollmentTests
{
    private readonly SignerFixture _fixture;

    public AgentEnrollmentTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Enrollment_creates_an_agent_only_endpoint_with_a_certificate_and_a_signed_first_configuration()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = SignerFixture.Csr(key);

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentEnrollment, site.ClientId, null,
            SignerFixture.EnrollPayload(token, csr, "SRV-ENROLL ", isServer: true), "gateway:203.0.113.7");

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.Null(request.RefusalReason);
        Assert.NotNull(request.CompletedAt);
        var response = EnrollResponse.Parser.ParseFrom(request.Result);
        var endpointId = Guid.Parse(response.EndpointId);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var endpoint = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpointId);
        Assert.Equal(EndpointTier.AgentOnly, endpoint.Tier);
        Assert.Equal(EndpointSource.Agent, endpoint.Source);
        Assert.Equal(EndpointClass.Server, endpoint.DetectedClass);
        Assert.Equal(site.Id, endpoint.SiteId);
        Assert.Equal(site.ClientId, endpoint.ClientId);
        Assert.Equal("SRV-ENROLL", endpoint.Hostname);
        Assert.Equal(1, endpoint.ConfigVersion);

        // Certificate: chains to the instance CA and names the endpoint.
        var ca = await db.CertificateAuthorities.AsNoTracking().SingleAsync(c => c.RetiredAt == null);
        Assert.Equal(ca.CertificateDer, response.CaCertificateDer.ToByteArray());
        var certificateDer = response.CertificateDer.ToByteArray();
        Assert.True(_fixture.ChainsToCa(certificateDer, ca.CertificateDer));
        using (var certificate = X509CertificateLoader.LoadCertificate(certificateDer))
        {
            Assert.Equal(endpointId, InternalCertificateAuthority.EndpointIdFromCertificate(certificate));
            var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
            Assert.Contains($"urn:fleeto:endpoint:{endpointId:D}", san.Format(false), StringComparison.Ordinal);
        }

        var stored = await db.AgentCertificates.AsNoTracking().SingleAsync(c => c.EndpointId == endpointId);
        Assert.Equal(KeyIds.Sha256Hex(certificateDer), stored.Fingerprint);
        Assert.Equal(InternalCertificateAuthority.CsrPublicKeyFingerprint(csr), stored.PublicKeyFingerprint);
        Assert.Null(stored.RevokedAt);

        var tokenAfter = await db.EnrollmentTokens.AsNoTracking().SingleAsync(t => t.Id == tokenRow.Id);
        Assert.Equal(1, tokenAfter.UseCount);

        // Signing key in the response is the instance key.
        var signingKey = await db.InstanceSigningKeys.AsNoTracking().SingleAsync(k => k.RetiredAt == null);
        Assert.Equal(signingKey.PublicKey, response.InstanceSigningPublicKey.ToByteArray());
        Assert.Equal(signingKey.Id, response.InstanceSigningKeyId);
        Assert.Equal(_fixture.Database.InstanceId.ToString("D"), response.InstanceId);

        // First configuration: version 1, agent-only, signed with the instance key over the stored bytes.
        var config = await db.EndpointConfigs.AsNoTracking().SingleAsync(c => c.EndpointId == endpointId);
        Assert.Equal(1, config.Version);
        Assert.Equal(signingKey.Id, config.KeyId);
        Assert.True(Ed25519.Verify(signingKey.PublicKey, SignatureContexts.AgentConfig, config.Payload, config.Signature));
        var agentConfig = AgentConfig.Parser.ParseFrom(config.Payload);
        Assert.Equal(Tier.AgentOnly, agentConfig.Tier);
        Assert.Empty(agentConfig.Checks);
        Assert.Equal(1UL, agentConfig.Version);
        Assert.Equal(endpointId.ToString("D"), agentConfig.EndpointId);
        Assert.Equal(_fixture.Database.InstanceId.ToString("D"), agentConfig.InstanceId);

        // Audit: both entries, by the agent, with the gateway's remote address and never the token.
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.TargetId == endpointId.ToString()).ToListAsync();
        var enrolled = Assert.Single(audit, a => a.Action == AuditActions.EndpointEnrolled);
        Assert.Single(audit, a => a.Action == AuditActions.CertificateIssued);
        Assert.All(audit, a => Assert.Equal(AuditActorType.Agent, a.ActorType));
        Assert.Equal(endpointId.ToString(), enrolled.ActorId);
        Assert.Equal("203.0.113.7", enrolled.IpAddress);
        Assert.Contains(tokenRow.Id.ToString(), enrolled.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(stored.Fingerprint, enrolled.DetailsJson, StringComparison.Ordinal);
        await AssertTokenNotInAuditAsync(token);

        Assert.Contains(endpointId.ToString(), _fixture.Database.Bus.PayloadsFor(NotificationChannels.EndpointStatus));
        Assert.Contains(endpointId.ToString(), _fixture.Database.Bus.PayloadsFor(NotificationChannels.EndpointConfig));
    }

    [Fact]
    public async Task Enrollment_with_a_wrong_token_secret_is_refused()
    {
        var (site, _, tokenRow) = await _fixture.CreateSiteWithTokenAsync();
        var otherSecret = OpaqueTokens.Create(OpaqueTokens.EnrollmentPrefix).Token.Split('_', 3)[2];
        var forged = $"fet_{tokenRow.Id:N}_{otherSecret}";

        await AssertRefusedAsync(site, tokenRow, forged, AgentEnrollmentHandler.InvalidTokenReason);
    }

    [Fact]
    public async Task Enrollment_with_an_expired_token_is_refused()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync(lifetime: TimeSpan.FromMinutes(-1));

        await AssertRefusedAsync(site, tokenRow, token, AgentEnrollmentHandler.ExpiredTokenReason);
    }

    [Fact]
    public async Task Enrollment_with_a_revoked_token_is_refused()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.EnrollmentTokens.Where(t => t.Id == tokenRow.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, _fixture.Now));
        }

        await AssertRefusedAsync(site, tokenRow, token, AgentEnrollmentHandler.RevokedTokenReason);
    }

    [Fact]
    public async Task Enrollment_with_a_single_use_token_that_was_already_used_is_refused()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync(maxUses: 1);
        using var firstKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var first = await _fixture.ProcessAsync(SigningRequestKind.AgentEnrollment, site.ClientId, null,
            SignerFixture.EnrollPayload(token, SignerFixture.Csr(firstKey), "WS-FIRST"));
        Assert.Equal(SigningRequestState.Completed, first.State);

        await AssertRefusedAsync(site, tokenRow, token, AgentEnrollmentHandler.UsedUpTokenReason, expectedUseCount: 1);
    }

    [Fact]
    public async Task Enrollment_with_a_token_of_another_client_than_the_request_is_refused()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync();
        var otherClient = await _fixture.Database.CreateClientAsync();

        await AssertRefusedAsync(site, tokenRow, token, AgentEnrollmentHandler.InvalidTokenReason, requestClientId: otherClient.Id);
    }

    [Fact]
    public async Task Enrollment_with_a_csr_that_is_not_p256_is_refused()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync();
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        using var rsa = RSA.Create(2048);

        await AssertRefusedAsync(site, tokenRow, token, AgentEnrollmentHandler.InvalidCsrReason, csr: SignerFixture.Csr(p384));
        await AssertRefusedAsync(site, tokenRow, token, AgentEnrollmentHandler.InvalidCsrReason, csr: SignerFixture.Csr(rsa));
    }

    [Fact]
    public async Task Enrollment_with_a_csr_whose_signature_does_not_verify_is_refused()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        // A CSR claiming one public key but signed by another key: no proof of possession.
        var victimPublicKey = PublicKey.CreateFromSubjectPublicKeyInfo(key.ExportSubjectPublicKeyInfo(), out _);
        var mismatched = new CertificateRequest(new X500DistinguishedName("CN=agent"), victimPublicKey, HashAlgorithmName.SHA256)
            .CreateSigningRequest(X509SignatureGenerator.CreateForECDsa(otherKey));

        // And a valid CSR with a corrupted signature byte.
        var corrupted = SignerFixture.Csr(key);
        corrupted[^1] ^= 0x55;

        await AssertRefusedAsync(site, tokenRow, token, AgentEnrollmentHandler.InvalidCsrReason, csr: mismatched);
        await AssertRefusedAsync(site, tokenRow, token, AgentEnrollmentHandler.InvalidCsrReason, csr: corrupted);
    }

    [Fact]
    public async Task Enrollment_request_older_than_five_minutes_is_refused_as_expired()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync();

        await AssertRefusedAsync(site, tokenRow, token, SigningRequestProcessor.ExpiredReason, createdAt: _fixture.Now.AddMinutes(-6));
    }

    [Fact]
    public async Task Malformed_enrollment_payload_is_refused()
    {
        var (site, _, tokenRow) = await _fixture.CreateSiteWithTokenAsync();

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentEnrollment, site.ClientId, null, [0xFF, 0xFF, 0xFF]);

        Assert.Equal(SigningRequestState.Refused, request.State);
        Assert.Equal(AgentEnrollmentHandler.MalformedReason, request.RefusalReason);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(0, await db.EnrollmentTokens.Where(t => t.Id == tokenRow.Id).Select(t => t.UseCount).SingleAsync());
    }

    private async Task AssertRefusedAsync(Site site, EnrollmentToken tokenRow, string token, string expectedReason,
        byte[]? csr = null, Guid? requestClientId = null, int expectedUseCount = 0, DateTime? createdAt = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var hostname = "WS-REFUSED-" + Guid.NewGuid().ToString("N")[..8];
        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentEnrollment, requestClientId ?? site.ClientId, null,
            SignerFixture.EnrollPayload(token, csr ?? SignerFixture.Csr(key), hostname), "gateway:192.0.2.1", createdAt);

        Assert.Equal(SigningRequestState.Refused, request.State);
        Assert.Equal(expectedReason, request.RefusalReason);
        Assert.Null(request.Result);
        Assert.NotNull(request.CompletedAt);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.Endpoints.AnyAsync(e => e.Hostname == hostname));
        Assert.Equal(expectedUseCount, await db.EnrollmentTokens.Where(t => t.Id == tokenRow.Id).Select(t => t.UseCount).SingleAsync());

        var refusal = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.SigningRefused && a.TargetId == request.Id.ToString());
        Assert.Contains("AgentEnrollment", refusal.DetailsJson, StringComparison.Ordinal);
        Assert.Equal("192.0.2.1", refusal.IpAddress);
        await AssertTokenNotInAuditAsync(token);
    }

    private async Task AssertTokenNotInAuditAsync(string token)
    {
        var secret = token.Split('_', 3)[2];
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var details = await db.AuditEntries.AsNoTracking().Select(a => a.DetailsJson).ToListAsync();
        Assert.DoesNotContain(details, d => d.Contains(secret, StringComparison.Ordinal) || d.Contains(token, StringComparison.Ordinal));
    }
}
