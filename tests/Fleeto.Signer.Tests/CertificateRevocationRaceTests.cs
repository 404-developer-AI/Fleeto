using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Infrastructure.Services;
using Fleeto.Signer.Handlers;
using Fleeto.Signer.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Fleeto.Signer.Tests;

/// <summary>
/// A revocation or deletion and a certificate the signer issues at the same moment (security review of 0.9.0). fleeto-signer locks the
/// endpoint row (FOR NO KEY UPDATE) to renew, recover or issue a watchdog certificate; EndpointRevocationService takes the same lock
/// before it reads the certificates, so it waits for an issuance in flight and revokes or deletes the new certificate with the others,
/// and an issuance that comes after the revocation is refused. The interleaving is deterministic: the real renewal handler runs in a
/// transaction the test owns (as SigningRequestProcessor does: begin, handle, commit), the real revocation service runs as fleeto_web
/// with the production grants before that transaction commits.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class CertificateRevocationRaceTests : IAsyncLifetime
{
    private static readonly TimeSpan Blocked = TimeSpan.FromSeconds(1);

    private readonly SignerFixture _fixture;
    private NpgsqlDataSource? _webSource;
    private IFleetoDbContextFactory _web = null!;

    public CertificateRevocationRaceTests(SignerFixture fixture) => _fixture = fixture;

    public Task InitializeAsync()
    {
        var web = new NpgsqlConnectionStringBuilder(_fixture.Database.ConnectionString) { Options = "-c role=" + DatabaseRoles.Web };
        _webSource = new NpgsqlDataSourceBuilder(web.ConnectionString).Build();
        _web = new FleetoDbContextFactory(FleetoDbContextFactory.BuildOptions(_webSource));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        if (_webSource is not null)
        {
            await _webSource.DisposeAsync();
        }
    }

    private async Task<Actor> AdminAsync()
    {
        var user = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        return new Actor(user.Id, "Race admin", SystemClientScope.Instance, "198.51.100.20");
    }

    private EndpointRevocationService Revocations() => new(_web, _fixture.Database.Bus, _fixture.Database.Time);

    private static SigningRequest RenewalRequest(EnrolledAgent agent, DateTime now) => new()
    {
        Id = Guid.NewGuid(), Kind = SigningRequestKind.AgentRenewal, ClientId = agent.ClientId, SubjectId = agent.EndpointId,
        Payload = SignerFixture.Csr(agent.Key), RequestedBy = "gateway", CreatedAt = now
    };

    /// <summary>The gateway allow list query (CertificateAllowList.cs), fingerprints only.</summary>
    private async Task<List<string>> AllowListAsync(Guid endpointId)
    {
        await using var command = _fixture.Database.DataSource.CreateCommand("""
            SELECT "Fingerprint" FROM "AgentCertificates" WHERE "RevokedAt" IS NULL AND "ExpiresAt" > $1 AND "EndpointId" = $2
            """);
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = _fixture.Now });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        var list = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            list.Add(reader.GetString(0));
        }

        return list;
    }

    /// <summary>Opens the signer's transaction and renews the agent in it, uncommitted: the endpoint row stays locked.</summary>
    private async Task<(FleetoDbContext Db, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction Transaction)> RenewUncommittedAsync(
        EnrolledAgent agent, DateTime now)
    {
        var signerDb = _fixture.SignerDbFactory.CreateSystem();
        var transaction = await signerDb.Database.BeginTransactionAsync();
        var handler = new AgentRenewalHandler(_fixture.KeyRing, NullLogger<AgentRenewalHandler>.Instance);
        var outcome = await handler.HandleAsync(new SigningContext(signerDb, RenewalRequest(agent, now), now), CancellationToken.None);
        Assert.Equal(SigningRequestState.Completed, outcome.State);
        return (signerDb, transaction);
    }

    [Fact]
    public async Task A_revocation_waits_for_a_renewal_in_flight_and_revokes_the_new_certificate()
    {
        var agent = await _fixture.EnrollAsync("WS-RACE-REVOKE");
        var actor = await AdminAsync();
        _fixture.Database.Time.Advance(TimeSpan.FromHours(25));
        var now = _fixture.Now;

        var (signerDb, t1) = await RenewUncommittedAsync(agent, now);
        await using (signerDb)
        await using (t1)
        {
            var revoke = Revocations().RevokeAsync(agent.EndpointId, "Stolen laptop", actor);
            // The revocation waits for the signer's lock instead of revoking only the certificates it saw.
            Assert.NotSame(revoke, await Task.WhenAny(revoke, Task.Delay(Blocked)));
            await t1.CommitAsync();
            Assert.True(await revoke.WaitAsync(TimeSpan.FromSeconds(30)));
        }

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var certificates = await db.AgentCertificates.AsNoTracking().Where(c => c.EndpointId == agent.EndpointId).ToListAsync();
        Assert.Equal(2, certificates.Count);
        Assert.All(certificates, c => Assert.NotNull(c.RevokedAt));
        var entry = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.CertificateRevoked && a.TargetId == agent.EndpointId.ToString());
        using (var details = System.Text.Json.JsonDocument.Parse(entry.DetailsJson))
        {
            Assert.Equal(2, details.RootElement.GetProperty("certificates").GetInt32());
        }

        Assert.Empty(await AllowListAsync(agent.EndpointId));
    }

    [Fact]
    public async Task A_deletion_waits_for_a_renewal_in_flight_and_sees_the_new_certificate()
    {
        var agent = await _fixture.EnrollAsync("WS-RACE-DELETE");
        var actor = await AdminAsync();
        _fixture.Database.Time.Advance(TimeSpan.FromHours(25));
        var now = _fixture.Now;

        var seen = new List<string>();
        var (signerDb, t1) = await RenewUncommittedAsync(agent, now);
        string renewed;
        await using (signerDb)
        await using (t1)
        {
            renewed = (await signerDb.AgentCertificates.AsNoTracking().Where(c => c.EndpointId == agent.EndpointId)
                .OrderByDescending(c => c.IssuedAt).FirstAsync()).Fingerprint;
            // What an uninstall written with the deletion records (AgentUninstallRequests): the certificates that can still connect.
            var delete = Revocations().DeleteAsync(agent.EndpointId, actor, CancellationToken.None, async (db, endpoint, _, token) =>
                seen.AddRange(await db.AgentCertificates.AsNoTracking().Where(c => c.EndpointId == endpoint.Id && c.RevokedAt == null)
                    .Select(c => c.Fingerprint).ToListAsync(token)));
            Assert.NotSame(delete, await Task.WhenAny(delete, Task.Delay(Blocked)));
            await t1.CommitAsync();
            Assert.True(await delete.WaitAsync(TimeSpan.FromSeconds(30)));
        }

        Assert.Contains(renewed, seen);
        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await check.AgentCertificates.AnyAsync(c => c.EndpointId == agent.EndpointId));
        Assert.Empty(await AllowListAsync(agent.EndpointId));
    }

    [Fact]
    public async Task A_renewal_after_the_revocation_is_refused()
    {
        var agent = await _fixture.EnrollAsync("WS-RACE-AFTER");
        var actor = await AdminAsync();
        _fixture.Database.Time.Advance(TimeSpan.FromHours(25));
        var now = _fixture.Now;

        Assert.True(await Revocations().RevokeAsync(agent.EndpointId, "Stolen laptop", actor));

        await using var signerDb = _fixture.SignerDbFactory.CreateSystem();
        await using var t1 = await signerDb.Database.BeginTransactionAsync();
        var handler = new AgentRenewalHandler(_fixture.KeyRing, NullLogger<AgentRenewalHandler>.Instance);
        var outcome = await handler.HandleAsync(new SigningContext(signerDb, RenewalRequest(agent, now), now), CancellationToken.None);
        await t1.CommitAsync();

        Assert.Equal(SigningRequestState.Refused, outcome.State);
        Assert.Equal(AgentRenewalHandler.NoCurrentCertificateReason, outcome.RefusalReason);
        Assert.Empty(await AllowListAsync(agent.EndpointId));
    }
}
