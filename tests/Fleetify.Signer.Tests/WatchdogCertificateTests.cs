using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Security;
using Fleetify.Signer.Handlers;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Signer.Tests;

/// <summary>
/// Guarantees of watchdog certificates (0.2.1): only the gateway can ask, only for an endpoint with a valid agent certificate, only for a key
/// of its own, at most three a day; a new one revokes the earlier watchdog certificates; renewal keeps the role and counts per role; and a
/// watchdog certificate never recovers.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class WatchdogCertificateTests
{
    private readonly SignerFixture _fixture;

    public WatchdogCertificateTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_watchdog_certificate_is_issued_for_its_own_key_and_replaces_the_previous_one()
    {
        var agent = await _fixture.EnrollAsync("WS-WATCHDOG");
        using var watchdogKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var first = await _fixture.ProcessAsync(SigningRequestKind.WatchdogCertificate, agent.ClientId, agent.EndpointId, SignerFixture.Csr(watchdogKey));
        Assert.Equal(SigningRequestState.Completed, first.State);
        Assert.True(_fixture.ChainsToCa(first.Result!, agent.Response.CaCertificateDer.ToByteArray()));
        using (var certificate = X509CertificateLoader.LoadCertificate(first.Result!))
        {
            Assert.Equal(agent.EndpointId, InternalCertificateAuthority.EndpointIdFromCertificate(certificate));
        }

        using var secondKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var second = await _fixture.ProcessAsync(SigningRequestKind.WatchdogCertificate, agent.ClientId, agent.EndpointId, SignerFixture.Csr(secondKey));
        Assert.Equal(SigningRequestState.Completed, second.State);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var certificates = await db.AgentCertificates.AsNoTracking().Where(c => c.EndpointId == agent.EndpointId).ToListAsync();
        var agentCertificate = Assert.Single(certificates, c => c.Role == AgentComponent.Agent);
        Assert.Null(agentCertificate.RevokedAt);
        var watchdogCertificates = certificates.Where(c => c.Role == AgentComponent.Watchdog).ToList();
        Assert.Equal(2, watchdogCertificates.Count);
        Assert.NotNull(watchdogCertificates.Single(c => c.Fingerprint == KeyIds.Sha256Hex(first.Result!)).RevokedAt);
        Assert.Null(watchdogCertificates.Single(c => c.Fingerprint == KeyIds.Sha256Hex(second.Result!)).RevokedAt);
        var issued = await db.AuditEntries.AsNoTracking()
            .Where(a => a.Action == AuditActions.CertificateIssued && a.TargetId == agent.EndpointId.ToString()).Select(a => a.DetailsJson).ToListAsync();
        Assert.Contains(issued, d => d.Contains("Watchdog", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_agent_key_another_requester_and_an_endpoint_without_an_agent_certificate_are_refused()
    {
        var agent = await _fixture.EnrollAsync("WS-WATCHDOG-REFUSED");

        var sameKey = await _fixture.ProcessAsync(SigningRequestKind.WatchdogCertificate, agent.ClientId, agent.EndpointId, SignerFixture.Csr(agent.Key));
        Assert.Equal(SigningRequestState.Refused, sameKey.State);
        Assert.Equal(WatchdogCertificateHandler.SameKeyReason, sameKey.RefusalReason);

        using var watchdogKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var fromWorkers = await _fixture.ProcessAsync(SigningRequestKind.WatchdogCertificate, agent.ClientId, agent.EndpointId, SignerFixture.Csr(watchdogKey),
            requestedBy: "workers");
        Assert.Equal(SigningRequestState.Refused, fromWorkers.State);
        Assert.Equal(WatchdogCertificateHandler.WrongRequesterReason, fromWorkers.RefusalReason);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.AgentCertificates.Where(c => c.EndpointId == agent.EndpointId)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, _fixture.Now).SetProperty(c => c.RevokedReason, "Stolen laptop"));
        }

        var revoked = await _fixture.ProcessAsync(SigningRequestKind.WatchdogCertificate, agent.ClientId, agent.EndpointId, SignerFixture.Csr(watchdogKey));
        Assert.Equal(SigningRequestState.Refused, revoked.State);
        Assert.Equal(WatchdogCertificateHandler.NoAgentCertificateReason, revoked.RefusalReason);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await check.AgentCertificates.AnyAsync(c => c.EndpointId == agent.EndpointId && c.Role == AgentComponent.Watchdog));
    }

    [Fact]
    public async Task At_most_three_watchdog_certificates_are_issued_per_endpoint_per_day()
    {
        var agent = await _fixture.EnrollAsync("WS-WATCHDOG-LIMIT");
        for (var i = 0; i < WatchdogCertificateHandler.MaxPerDay; i++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            Assert.Equal(SigningRequestState.Completed,
                (await _fixture.ProcessAsync(SigningRequestKind.WatchdogCertificate, agent.ClientId, agent.EndpointId, SignerFixture.Csr(key))).State);
        }

        using var oneMore = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var refused = await _fixture.ProcessAsync(SigningRequestKind.WatchdogCertificate, agent.ClientId, agent.EndpointId, SignerFixture.Csr(oneMore));
        Assert.Equal(SigningRequestState.Refused, refused.State);
        Assert.Equal(WatchdogCertificateHandler.TooManyReason, refused.RefusalReason);
    }

    [Fact]
    public async Task A_watchdog_renews_with_its_role_and_the_24_hour_rule_counts_per_role()
    {
        var agent = await _fixture.EnrollAsync("WS-WATCHDOG-RENEW");
        _fixture.Database.Time.Advance(TimeSpan.FromHours(25));
        using var watchdogKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Equal(SigningRequestState.Completed,
            (await _fixture.ProcessAsync(SigningRequestKind.WatchdogCertificate, agent.ClientId, agent.EndpointId, SignerFixture.Csr(watchdogKey))).State);

        // The watchdog certificate was issued a moment ago; the renewal of the agent certificate still passes its own 24-hour rule.
        var agentRenewal = await _fixture.ProcessAsync(SigningRequestKind.AgentRenewal, agent.ClientId, agent.EndpointId, SignerFixture.Csr(agent.Key));
        Assert.Equal(SigningRequestState.Completed, agentRenewal.State);

        var tooSoon = await _fixture.ProcessAsync(SigningRequestKind.AgentRenewal, agent.ClientId, agent.EndpointId, SignerFixture.Csr(watchdogKey));
        Assert.Equal(AgentRenewalHandler.TooSoonReason, tooSoon.RefusalReason);

        _fixture.Database.Time.Advance(TimeSpan.FromHours(25));
        var watchdogRenewal = await _fixture.ProcessAsync(SigningRequestKind.AgentRenewal, agent.ClientId, agent.EndpointId, SignerFixture.Csr(watchdogKey));
        Assert.Equal(SigningRequestState.Completed, watchdogRenewal.State);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var watchdogFingerprint = KeyIds.Sha256Hex(watchdogRenewal.Result!);
        var agentFingerprint = KeyIds.Sha256Hex(agentRenewal.Result!);
        Assert.Equal(AgentComponent.Watchdog, (await db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Fingerprint == watchdogFingerprint)).Role);
        Assert.Equal(AgentComponent.Agent, (await db.AgentCertificates.AsNoTracking().SingleAsync(c => c.Fingerprint == agentFingerprint)).Role);
    }
}
