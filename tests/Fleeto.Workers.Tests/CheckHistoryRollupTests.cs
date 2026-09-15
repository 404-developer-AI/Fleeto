using Fleeto.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees the check history rollups: the evaluation counts every result exactly once into its hourly and daily bucket (by the
/// agent's collection time when plausible), keeps errors and "no response" out of minimum, maximum and average, and retention
/// removes rollups after 13 months.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class CheckHistoryRollupTests
{
    private readonly WorkersFixture _fixture;

    public CheckHistoryRollupTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task InsertAsync(Endpoint endpoint, CheckDefinition definition, double value, DateTime agentTime, string error = "", string target = "")
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.CheckResults.Add(new CheckResult
        {
            Time = _fixture.Now, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, CheckDefinitionId = definition.Id, Target = target,
            AgentTime = agentTime, Value = value, Error = error, ConfigVersion = 1
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Results_are_rolled_up_once_per_hour_and_day_without_errors_or_missing_responses_in_the_values()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-HISTORY", EndpointClass.Server);
        var ping = await _fixture.CreateCheckAsync(site, CheckType.Ping, null, null, parameters: """{"host":"10.0.0.1"}""");
        var now = _fixture.Now;
        var hour = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        var buffered = now.AddHours(-3);

        await InsertAsync(endpoint, ping, 12, now);
        await InsertAsync(endpoint, ping, 30, now);
        await InsertAsync(endpoint, ping, -1, now);
        await InsertAsync(endpoint, ping, 0, now, error: "the host name could not be resolved");
        await InsertAsync(endpoint, ping, 50, buffered);
        await InsertAsync(endpoint, ping, 99, now.AddYears(-1)); // implausible agent clock: counted at ingest time

        var evaluation = _fixture.CheckEvaluation();
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        // A second pass finds nothing new: nothing is counted twice.
        await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var hourly = await db.CheckResultsHourly.AsNoTracking().Where(r => r.EndpointId == endpoint.Id).OrderBy(r => r.Bucket).ToListAsync();
        var current = Assert.Single(hourly, r => r.Bucket == hour);
        Assert.Equal(12, current.MinValue);
        Assert.Equal(99, current.MaxValue);
        Assert.Equal(3, current.ValueCount);
        Assert.Equal(141, current.SumValue);
        Assert.Equal(1, current.NoResponseCount);
        Assert.Equal(1, current.ErrorCount);
        var earlier = Assert.Single(hourly, r => r.Bucket == new DateTime(buffered.Year, buffered.Month, buffered.Day, buffered.Hour, 0, 0, DateTimeKind.Utc));
        Assert.Equal(50, earlier.MinValue);

        var daily = await db.CheckResultsDaily.AsNoTracking().Where(r => r.EndpointId == endpoint.Id).ToListAsync();
        Assert.Equal(6, daily.Sum(d => d.ValueCount + d.ErrorCount + d.NoResponseCount));
    }

    [Fact]
    public async Task Retention_removes_rollups_older_than_thirteen_months()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-HISTORY-OLD", EndpointClass.Server);
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);
        var now = _fixture.Now;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            foreach (var bucket in new[] { now.AddDays(-401), now.AddDays(-30) })
            {
                var b = new DateTime(bucket.Year, bucket.Month, bucket.Day, 0, 0, 0, DateTimeKind.Utc);
                db.CheckResultsHourly.Add(new CheckResultHourly { EndpointId = endpoint.Id, ClientId = endpoint.ClientId, CheckDefinitionId = cpu.Id, Bucket = b, ValueCount = 1 });
                db.CheckResultsDaily.Add(new CheckResultDaily { EndpointId = endpoint.Id, ClientId = endpoint.ClientId, CheckDefinitionId = cpu.Id, Bucket = b, ValueCount = 1 });
            }

            await db.SaveChangesAsync();
        }

        await _fixture.Retention().RunAsync(CancellationToken.None);

        await using var check = _fixture.Db.DbFactory.CreateSystem();
        Assert.Equal(1, await check.CheckResultsHourly.CountAsync(r => r.EndpointId == endpoint.Id));
        Assert.Equal(1, await check.CheckResultsDaily.CountAsync(r => r.EndpointId == endpoint.Id));
    }
}
