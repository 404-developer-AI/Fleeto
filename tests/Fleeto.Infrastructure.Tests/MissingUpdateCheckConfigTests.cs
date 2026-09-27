using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// A missing updates check (0.6.0) is evaluated by Fleeto from the patch state, so it never reaches the agent: the signed
/// configuration carries the other checks of the endpoint and not this one.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MissingUpdateCheckConfigTests
{
    private readonly TestDatabase _db;

    public MissingUpdateCheckConfigTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task A_missing_updates_check_is_left_out_of_the_agent_configuration()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-MISSING");
        await _db.LoadTestLicenseAsync(100_000);
        var now = DateTime.UtcNow;

        CheckDefinition Check(CheckType type, int interval) => new()
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = type.ToString(), Type = type,
            IntervalSeconds = interval, WarningThreshold = 14, CriticalThreshold = 30, CreatedAt = now, UpdatedAt = now
        };
        var missing = Check(CheckType.MissingUpdates, CheckCatalog.FleetoEvaluatedIntervalSeconds);
        var uptime = Check(CheckType.Uptime, 3600);
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.CheckDefinitions.AddRange(missing, uptime);
            await db.SaveChangesAsync();
        }

        await using var context = _db.DbFactory.CreateSystem();
        var config = (await new AgentConfigBuilder(_db.Licenses).BuildAsync(context, endpoint.Id, _db.InstanceId))!.Config;

        Assert.Contains(config.Checks, c => c.Id == uptime.Id.ToString("D"));
        Assert.DoesNotContain(config.Checks, c => c.Id == missing.Id.ToString("D"));
    }
}
