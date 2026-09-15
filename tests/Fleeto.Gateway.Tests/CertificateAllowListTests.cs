using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Core.Entities;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Guarantees that an agent certificate opens a session only when it is issued by the instance CA, recorded, not revoked,
/// not expired, its endpoint still exists and the endpoint id in its SAN is the endpoint it was issued to.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class CertificateAllowListTests
{
    private readonly GatewayFixture _fixture;

    public CertificateAllowListTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Recorded_certificate_of_an_existing_endpoint_is_accepted()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint);
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));

        using var certificate = credential.PublicCertificate();
        Assert.Equal(AllowListDecision.Accepted, harness.AllowList.Authorize(certificate, out var identity));
        Assert.Equal(endpoint.Id, identity!.EndpointId);
        Assert.Equal(credential.Issued.PublicKeyFingerprint, identity.PublicKeyFingerprint);
    }

    [Fact]
    public async Task Revoked_expired_and_deleted_endpoint_certificates_are_not_on_the_allow_list()
    {
        using var harness = _fixture.CreateHarness();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;

        var revokedEndpoint = await _fixture.CreateEndpointAsync();
        var revoked = await _fixture.IssueAsync(revokedEndpoint, revokedAt: now.AddMinutes(-1));

        var expiredEndpoint = await _fixture.CreateEndpointAsync();
        var expired = await _fixture.IssueAsync(expiredEndpoint, expiresAt: now.AddMinutes(-1));

        var deletedEndpoint = await _fixture.CreateEndpointAsync();
        var deleted = await _fixture.IssueAsync(deletedEndpoint);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == deletedEndpoint.Id).ExecuteDeleteAsync();
        }

        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));

        foreach (var credential in new[] { revoked, expired, deleted })
        {
            using var certificate = credential.PublicCertificate();
            Assert.Equal(AllowListDecision.Refused, harness.AllowList.Authorize(certificate, out var identity));
            Assert.Null(identity);
        }
    }

    [Fact]
    public async Task Certificate_with_a_mismatching_endpoint_id_in_its_SAN_is_refused()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        // Recorded for this endpoint, but the certificate itself names another endpoint.
        var credential = await _fixture.IssueAsync(endpoint, sanEndpointId: Guid.NewGuid());
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));

        using var certificate = credential.PublicCertificate();
        Assert.Equal(AllowListDecision.Refused, harness.AllowList.Authorize(certificate, out _));
    }

    [Fact]
    public async Task Certificate_from_another_CA_is_refused_even_when_its_fingerprint_is_recorded()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var foreignCa = InternalCertificateAuthority.CreateCa("attacker.example", now);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        var issued = InternalCertificateAuthority.IssueAgentCertificate(foreignCa.CertificateDer, foreignCa.PrivateKeyPkcs8, csr, endpoint.Id,
            _fixture.Database.InstanceId, now);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.AgentCertificates.Add(new AgentCertificate
            {
                Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Fingerprint = issued.Fingerprint,
                PublicKeyFingerprint = issued.PublicKeyFingerprint, SerialNumber = issued.SerialNumber, IssuedAt = now, ExpiresAt = issued.NotAfter
            });
            await db.SaveChangesAsync();
        }

        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
        using var certificate = X509CertificateLoader.LoadCertificate(issued.CertificateDer);
        Assert.Equal(AllowListDecision.Refused, harness.AllowList.Authorize(certificate, out _));
    }

    [Fact]
    public async Task Allow_list_that_was_never_loaded_refuses_every_certificate()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint);

        using var certificate = credential.PublicCertificate();
        Assert.Equal(AllowListDecision.NotLoaded, harness.AllowList.Authorize(certificate, out _));
        Assert.Equal(AllowListDecision.NotLoaded, harness.AllowList.Authorize(null, out _));
    }
}
