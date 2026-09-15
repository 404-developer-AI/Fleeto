using Fleeto.Core.Entities;
using Fleeto.Workers.Checks;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees that retention deletes only data that is out of retention, including every check result of a deleted
/// endpoint and its evaluation cursor, and keeps everything that is still within its period.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class RetentionTests
{
    private readonly WorkersFixture _fixture;

    public RetentionTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Retention_deletes_only_what_is_out_of_retention()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (client, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-KEEP");
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var now = _fixture.Now;

        Guid oldResultEndpoint = endpoint.Id;
        var ids = new Dictionary<string, object>();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            CheckResult Result(DateTime time) => new()
            {
                Time = time, ClientId = client.Id, EndpointId = endpoint.Id, CheckDefinitionId = cpu.Id, AgentTime = time, Value = 1, ConfigVersion = 1
            };

            db.CheckResults.AddRange(Result(now.AddDays(-40)), Result(now.AddDays(-1)));
            db.IngestBatches.AddRange(
                new IngestBatch { EndpointId = endpoint.Id, ClientId = client.Id, Sequence = 1, ReceivedAt = now.AddDays(-8) },
                new IngestBatch { EndpointId = endpoint.Id, ClientId = client.Id, Sequence = 2, ReceivedAt = now.AddDays(-1) });

            SigningRequest Request(SigningRequestState state, DateTime created) => new()
            {
                Id = Guid.NewGuid(), ClientId = client.Id, Kind = SigningRequestKind.AgentConfig, SubjectId = endpoint.Id, RequestedBy = "workers",
                State = state, CreatedAt = created
            };

            var oldCompleted = Request(SigningRequestState.Completed, now.AddDays(-8));
            var oldPending = Request(SigningRequestState.Pending, now.AddDays(-8));
            var recentCompleted = Request(SigningRequestState.Completed, now.AddDays(-1));
            db.SigningRequests.AddRange(oldCompleted, oldPending, recentCompleted);
            ids["oldCompleted"] = oldCompleted.Id;
            ids["oldPending"] = oldPending.Id;
            ids["recentCompleted"] = recentCompleted.Id;

            db.EndpointEvents.AddRange(
                new EndpointEvent { ClientId = client.Id, EndpointId = endpoint.Id, Kind = EndpointEventKind.Connected, Detail = "old processed", Time = now.AddDays(-31), ProcessedAt = now.AddDays(-31) },
                new EndpointEvent { ClientId = client.Id, EndpointId = endpoint.Id, Kind = EndpointEventKind.Connected, Detail = "old unprocessed", Time = now.AddDays(-31) },
                new EndpointEvent { ClientId = client.Id, EndpointId = endpoint.Id, Kind = EndpointEventKind.Connected, Detail = "recent processed", Time = now.AddDays(-1), ProcessedAt = now });

            OutboxEmail Email(string tag, DateTime created, DateTime? sent, int attempts) => new()
            {
                Id = Guid.NewGuid(), ToAddress = $"{tag}-{endpoint.Id:N}@test.example", Subject = tag, Category = "test", CreatedAt = created,
                NextAttemptAt = created, SentAt = sent, Attempts = attempts
            };

            db.OutboxEmails.AddRange(
                Email("sent-old", now.AddDays(-31), now.AddDays(-31), 1),
                Email("sent-recent", now.AddDays(-1), now.AddDays(-1), 1),
                Email("failed-old", now.AddDays(-91), null, 10),
                Email("failed-recent", now.AddDays(-31), null, 10),
                Email("pending-old", now.AddDays(-91), null, 3));

            AgentCertificate Certificate(string tag, DateTime expires) => new()
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Fingerprint = (tag + Guid.NewGuid().ToString("N")).PadRight(64, '0')[..64],
                PublicKeyFingerprint = new string('a', 64), SerialNumber = tag, IssuedAt = expires.AddDays(-90), ExpiresAt = expires
            };

            db.AgentCertificates.AddRange(Certificate("expired-old", now.AddDays(-31)), Certificate("expired-recent", now.AddDays(-1)));

            EnrollmentToken Token(string tag, DateTime expires, int? maxUses, int useCount, DateTime created) => new()
            {
                Id = Guid.NewGuid(), ClientId = client.Id, SiteId = site.Id, Name = tag, TokenHash = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")[..32],
                ExpiresAt = expires, MaxUses = maxUses, UseCount = useCount, CreatedAt = created
            };

            db.EnrollmentTokens.AddRange(
                Token("expired-old", now.AddDays(-31), 1, 0, now.AddDays(-40)),
                Token("used-old", now.AddDays(10), 1, 1, now.AddDays(-40)),
                Token("valid", now.AddDays(10), 1, 0, now.AddDays(-40)),
                Token("used-recent", now.AddDays(10), 1, 1, now.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        // A deleted endpoint with evaluated results of all ages.
        var gone = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-GONE");
        await _fixture.InsertResultAsync(gone, cpu, 50);
        await _fixture.CheckEvaluation().EvaluateEndpointAsync(gone.Id, CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.CheckResults.Add(new CheckResult
            {
                Time = now.AddDays(-10), ClientId = client.Id, EndpointId = gone.Id, CheckDefinitionId = cpu.Id, AgentTime = now, Value = 1, ConfigVersion = 1
            });
            await db.SaveChangesAsync();
            await db.Endpoints.Where(e => e.Id == gone.Id).ExecuteDeleteAsync();
        }

        await _fixture.Retention().RunAsync(CancellationToken.None);

        await using var check = _fixture.Db.DbFactory.CreateSystem();
        // With TimescaleDB its retention policy removes old chunks, so the service leaves results by age alone.
        var timescale = await check.Database.SqlQueryRaw<bool>("""SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') AS "Value" """).SingleAsync();
        var results = await check.CheckResults.AsNoTracking().Where(r => r.EndpointId == oldResultEndpoint).OrderBy(r => r.Time).ToListAsync();
        Assert.Equal(timescale ? 2 : 1, results.Count);
        Assert.True(results[^1].Time > now.AddDays(-2));
        Assert.False(await check.CheckResults.AnyAsync(r => r.EndpointId == gone.Id));
        Assert.False(await check.WorkerWatermarks.AnyAsync(w => w.Name == CheckEvaluationService.WatermarkName(gone.Id)));

        Assert.Equal([2L], await check.IngestBatches.Where(b => b.EndpointId == endpoint.Id).Select(b => b.Sequence).ToListAsync());

        var requests = await check.SigningRequests.Where(r => r.SubjectId == endpoint.Id).Select(r => r.Id).ToListAsync();
        Assert.DoesNotContain((Guid)ids["oldCompleted"], requests);
        Assert.Contains((Guid)ids["oldPending"], requests);
        Assert.Contains((Guid)ids["recentCompleted"], requests);

        Assert.Equal(["old unprocessed", "recent processed"],
            (await check.EndpointEvents.Where(e => e.EndpointId == endpoint.Id).Select(e => e.Detail).ToListAsync()).Order().ToArray());

        var emails = await check.OutboxEmails.Where(e => e.ToAddress.EndsWith($"-{endpoint.Id:N}@test.example")).Select(e => e.Subject).ToListAsync();
        Assert.Equal(["failed-recent", "pending-old", "sent-recent"], emails.Order().ToArray());

        Assert.Equal(["expired-recent"], await check.AgentCertificates.Where(c => c.EndpointId == endpoint.Id).Select(c => c.SerialNumber).ToListAsync());
        Assert.Equal(["used-recent", "valid"], (await check.EnrollmentTokens.Where(t => t.SiteId == site.Id).Select(t => t.Name).ToListAsync()).Order().ToArray());
    }
}
