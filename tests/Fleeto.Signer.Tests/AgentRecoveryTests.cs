using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees certificate recovery and enrolling again (0.2.0): an expired, never revoked, most recent certificate renews with
/// its own key within a year after expiry; a revoked, replaced, too old, not yet expired or other-key identity is refused; an
/// "enroll again" token keeps the endpoint, revokes its earlier certificates, clears its batch sequences and drops live sessions.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class AgentRecoveryTests
{
    private readonly SignerFixture _fixture;

    public AgentRecoveryTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    private static byte[] RecoverPayload(ECDsa key) => new RecoverRequest
    {
        CsrDer = ByteString.CopyFrom(SignerFixture.Csr(key)), Hostname = "WS-RENEW", AgentVersion = "0.2.0"
    }.ToByteArray();

    private async Task ExpireAsync(Guid endpointId, TimeSpan ago)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var expiredAt = _fixture.Now - ago;
        await db.AgentCertificates.Where(c => c.EndpointId == endpointId).ExecuteUpdateAsync(s => s.SetProperty(c => c.ExpiresAt, expiredAt));
    }

    [Fact]
    public async Task An_expired_certificate_within_a_year_is_recovered_with_the_same_key()
    {
        var agent = await _fixture.EnrollAsync("WS-DRAWER");
        await ExpireAsync(agent.EndpointId, TimeSpan.FromDays(200));

        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentRecovery, agent.ClientId, agent.EndpointId, RecoverPayload(agent.Key),
            "gateway:198.51.100.10");

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.True(_fixture.ChainsToCa(request.Result!, agent.Response.CaCertificateDer.ToByteArray()));
        using (var certificate = X509CertificateLoader.LoadCertificate(request.Result!))
        {
            Assert.Equal(agent.EndpointId, InternalCertificateAuthority.EndpointIdFromCertificate(certificate));
            Assert.True(certificate.NotAfter.ToUniversalTime() > _fixture.Now.AddDays(80));
        }

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(2, await db.AgentCertificates.CountAsync(c => c.EndpointId == agent.EndpointId && c.RevokedAt == null));
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == AuditActions.CertificateRecovered && a.TargetId == agent.EndpointId.ToString());
        Assert.Contains("expiredAt", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("198.51.100.10", audit.IpAddress);
    }

    [Fact]
    public async Task Revoked_replaced_too_old_not_expired_or_other_key_identities_are_refused()
    {
        var notExpired = await _fixture.EnrollAsync();
        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentRecovery, notExpired.ClientId, notExpired.EndpointId, RecoverPayload(notExpired.Key));
        Assert.Equal(AgentRecoveryHandler.NotExpiredReason, request.RefusalReason);

        var tooOld = await _fixture.EnrollAsync();
        await ExpireAsync(tooOld.EndpointId, TimeSpan.FromDays(366));
        request = await _fixture.ProcessAsync(SigningRequestKind.AgentRecovery, tooOld.ClientId, tooOld.EndpointId, RecoverPayload(tooOld.Key));
        Assert.Equal(AgentRecoveryHandler.NotRecoverableReason, request.RefusalReason);

        var revoked = await _fixture.EnrollAsync();
        await ExpireAsync(revoked.EndpointId, TimeSpan.FromDays(10));
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.AgentCertificates.Where(c => c.EndpointId == revoked.EndpointId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, _fixture.Now.AddDays(-20)).SetProperty(c => c.RevokedReason, "Stolen laptop"));
        }

        request = await _fixture.ProcessAsync(SigningRequestKind.AgentRecovery, revoked.ClientId, revoked.EndpointId, RecoverPayload(revoked.Key));
        Assert.Equal(AgentRecoveryHandler.NotRecoverableReason, request.RefusalReason);

        var otherKey = await _fixture.EnrollAsync();
        await ExpireAsync(otherKey.EndpointId, TimeSpan.FromDays(10));
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        request = await _fixture.ProcessAsync(SigningRequestKind.AgentRecovery, otherKey.ClientId, otherKey.EndpointId, RecoverPayload(stranger));
        Assert.Equal(AgentRecoveryHandler.NotRecoverableReason, request.RefusalReason);

        // A copy of an older identity cannot come back once a newer certificate exists, even when that one expired too.
        var replaced = await _fixture.EnrollAsync();
        await ExpireAsync(replaced.EndpointId, TimeSpan.FromDays(10));
        using var newerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var newer = _fixture.KeyRing.IssueAgentCertificate(SignerFixture.Csr(newerKey), replaced.EndpointId, _fixture.Now);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.AgentCertificates.Add(new AgentCertificate
            {
                Id = Guid.NewGuid(), ClientId = replaced.ClientId, EndpointId = replaced.EndpointId, Fingerprint = newer.Fingerprint,
                PublicKeyFingerprint = newer.PublicKeyFingerprint, SerialNumber = newer.SerialNumber, IssuedAt = _fixture.Now.AddDays(1),
                ExpiresAt = _fixture.Now.AddDays(-1)
            });
            await db.SaveChangesAsync();
        }

        request = await _fixture.ProcessAsync(SigningRequestKind.AgentRecovery, replaced.ClientId, replaced.EndpointId, RecoverPayload(replaced.Key));
        Assert.Equal(AgentRecoveryHandler.NotRecoverableReason, request.RefusalReason);
    }

    [Fact]
    public async Task Enrolling_again_keeps_the_endpoint_and_revokes_its_earlier_certificates()
    {
        var agent = await _fixture.EnrollAsync("WS-OLD");
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == agent.EndpointId).ExecuteUpdateAsync(s => s.SetProperty(e => e.Tier, EndpointTier.Managed));
            db.IngestBatches.Add(new IngestBatch { EndpointId = agent.EndpointId, ClientId = agent.ClientId, Sequence = 1, ReceivedAt = _fixture.Now });
            await db.SaveChangesAsync();
        }

        var (_, token, row) = await CreateEndpointTokenAsync(agent);
        using var newKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentEnrollment, agent.ClientId, null,
            SignerFixture.EnrollPayload(token, SignerFixture.Csr(newKey), "WS-NEW", isServer: true), "gateway:198.51.100.20");

        Assert.Equal(SigningRequestState.Completed, request.State);
        var response = EnrollResponse.Parser.ParseFrom(request.Result);
        Assert.Equal(agent.EndpointId, Guid.Parse(response.EndpointId));

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        var endpoint = await check.Endpoints.AsNoTracking().SingleAsync(e => e.Id == agent.EndpointId);
        Assert.Equal("WS-NEW", endpoint.Hostname);
        Assert.Equal(EndpointTier.Managed, endpoint.Tier);
        Assert.Equal(EndpointClass.Server, endpoint.DetectedClass);
        var certificates = await check.AgentCertificates.AsNoTracking().Where(c => c.EndpointId == agent.EndpointId).ToListAsync();
        Assert.Equal(2, certificates.Count);
        Assert.Single(certificates, c => c.RevokedAt is not null && c.RevokedByUserId == row.CreatedByUserId);
        Assert.Single(certificates, c => c.RevokedAt is null && c.Fingerprint == KeyIds.Sha256Hex(response.CertificateDer.ToByteArray()));
        Assert.False(await check.IngestBatches.AnyAsync(b => b.EndpointId == agent.EndpointId));
        Assert.Equal(1, (await check.EnrollmentTokens.AsNoTracking().SingleAsync(t => t.Id == row.Id)).UseCount);
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == AuditActions.EndpointEnrolledAgain && a.TargetId == agent.EndpointId.ToString()));
        Assert.Contains(agent.EndpointId.ToString(), _fixture.Database.Bus.PayloadsFor(NotificationChannels.Revocations));

        // Single use: the same command cannot take the endpoint over twice.
        using var thirdKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var again = await _fixture.ProcessAsync(SigningRequestKind.AgentEnrollment, agent.ClientId, null,
            SignerFixture.EnrollPayload(token, SignerFixture.Csr(thirdKey), "WS-THIRD"));
        Assert.Equal(AgentEnrollmentHandler.UsedUpTokenReason, again.RefusalReason);
    }

    private async Task<(Site Site, string Token, EnrollmentToken Row)> CreateEndpointTokenAsync(EnrolledAgent agent)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var site = await db.Sites.AsNoTracking().SingleAsync(s => s.Id == db.Endpoints.Where(e => e.Id == agent.EndpointId).Select(e => e.SiteId).Single());
        var (token, row) = await _fixture.Database.CreateEnrollmentTokenAsync(site);
        await db.EnrollmentTokens.Where(t => t.Id == row.Id).ExecuteUpdateAsync(s => s
            .SetProperty(t => t.EndpointId, agent.EndpointId)
            .SetProperty(t => t.CreatedByUserId, Guid.NewGuid()));
        row.EndpointId = agent.EndpointId;
        row.CreatedByUserId = (await db.EnrollmentTokens.AsNoTracking().SingleAsync(t => t.Id == row.Id)).CreatedByUserId;
        return (site, token, row);
    }
}
