using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees that a renewal keeps the key of a current certificate of the same endpoint, is refused for another
/// key, for a revoked identity and within 24 hours of the previous certificate.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class AgentRenewalTests
{
    private readonly SignerFixture _fixture;

    public AgentRenewalTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Renewal_with_the_same_key_after_24_hours_issues_a_new_certificate()
    {
        var agent = await _fixture.EnrollAsync();
        _fixture.Database.Time.Advance(TimeSpan.FromHours(25));

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentRenewal, agent.ClientId, agent.EndpointId, SignerFixture.Csr(agent.Key));

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.NotNull(request.Result);
        Assert.True(_fixture.ChainsToCa(request.Result, agent.Response.CaCertificateDer.ToByteArray()));
        using (var certificate = X509CertificateLoader.LoadCertificate(request.Result))
        {
            Assert.Equal(agent.EndpointId, InternalCertificateAuthority.EndpointIdFromCertificate(certificate));
        }

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var certificates = await db.AgentCertificates.AsNoTracking().Where(c => c.EndpointId == agent.EndpointId).ToListAsync();
        Assert.Equal(2, certificates.Count);
        Assert.Single(certificates.Select(c => c.PublicKeyFingerprint).Distinct());
        Assert.Contains(certificates, c => c.Fingerprint == KeyIds.Sha256Hex(request.Result));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.CertificateRenewed && a.TargetId == agent.EndpointId.ToString()));
    }

    [Fact]
    public async Task Renewal_with_a_different_key_is_refused()
    {
        var agent = await _fixture.EnrollAsync();
        _fixture.Database.Time.Advance(TimeSpan.FromHours(25));
        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentRenewal, agent.ClientId, agent.EndpointId, SignerFixture.Csr(otherKey));

        await AssertRefusedAsync(request, agent, AgentRenewalHandler.NoCurrentCertificateReason);
    }

    [Fact]
    public async Task Renewal_within_24_hours_of_the_previous_certificate_is_refused()
    {
        var agent = await _fixture.EnrollAsync();
        _fixture.Database.Time.Advance(TimeSpan.FromHours(23));

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentRenewal, agent.ClientId, agent.EndpointId, SignerFixture.Csr(agent.Key));

        await AssertRefusedAsync(request, agent, AgentRenewalHandler.TooSoonReason);
    }

    [Fact]
    public async Task Renewal_after_revocation_is_refused()
    {
        var agent = await _fixture.EnrollAsync();
        _fixture.Database.Time.Advance(TimeSpan.FromHours(25));
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.AgentCertificates.Where(c => c.EndpointId == agent.EndpointId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, _fixture.Now).SetProperty(c => c.RevokedReason, "Stolen laptop"));
        }

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentRenewal, agent.ClientId, agent.EndpointId, SignerFixture.Csr(agent.Key));

        await AssertRefusedAsync(request, agent, AgentRenewalHandler.NoCurrentCertificateReason);
    }

    private async Task AssertRefusedAsync(SigningRequest request, EnrolledAgent agent, string expectedReason)
    {
        Assert.Equal(SigningRequestState.Refused, request.State);
        Assert.Equal(expectedReason, request.RefusalReason);
        Assert.Null(request.Result);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(1, await db.AgentCertificates.CountAsync(c => c.EndpointId == agent.EndpointId));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.SigningRefused && a.TargetId == request.Id.ToString()));
    }
}
