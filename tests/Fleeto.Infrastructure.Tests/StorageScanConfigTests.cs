using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Storage analysis in the signed configuration (0.6.0): a managed endpoint gets the scan interval of its policy, an agent-only
/// endpoint none (tier enforcement: look, do not touch), and a folder growth check never reaches the agent.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class StorageScanConfigTests
{
    private readonly TestDatabase _db;

    public StorageScanConfigTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task A_managed_endpoint_gets_the_scan_interval_of_its_policy_and_an_agent_only_endpoint_none()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var managed = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-STORAGE");
        var agentOnly = await _db.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-STORAGE");
        await _db.LoadTestLicenseAsync(100_000);

        Assert.Equal((uint)StorageRules.DefaultScanIntervalHours, (await BuildAsync(managed.Id)).StorageScanIntervalHours);
        Assert.Equal(0u, (await BuildAsync(agentOnly.Id)).StorageScanIntervalHours);

        var now = DateTime.UtcNow;
        await using (var db = _db.DbFactory.CreateSystem())
        {
            var policy = new Policy { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Weekly scans", StorageScanIntervalHours = 168, CreatedAt = now, UpdatedAt = now };
            db.Policies.Add(policy);
            db.EndpointPolicies.Add(new EndpointPolicy { EndpointId = managed.Id, ClientId = client.Id, PolicyId = policy.Id, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        Assert.Equal(168u, (await BuildAsync(managed.Id)).StorageScanIntervalHours);
    }

    [Fact]
    public async Task A_folder_growth_check_is_left_out_of_the_agent_configuration()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-GROWTH");
        await _db.LoadTestLicenseAsync(100_000);
        var now = DateTime.UtcNow;
        var growth = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = "Growth", Type = CheckType.FolderGrowth,
            IntervalSeconds = CheckCatalog.FleetoEvaluatedIntervalSeconds, WarningThreshold = 10, CriticalThreshold = 50, CreatedAt = now, UpdatedAt = now
        };
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.CheckDefinitions.Add(growth);
            await db.SaveChangesAsync();
        }

        Assert.DoesNotContain((await BuildAsync(endpoint.Id)).Checks, c => c.Id == growth.Id.ToString("D"));
    }

    [Fact]
    public async Task The_database_refuses_a_scan_interval_that_is_not_offered()
    {
        var now = DateTime.UtcNow;
        await using var db = _db.DbFactory.CreateSystem();
        db.Policies.Add(new Policy { Id = Guid.NewGuid(), Name = "Odd " + Guid.NewGuid().ToString("N")[..8], StorageScanIntervalHours = 5, CreatedAt = now, UpdatedAt = now });

        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }

    private async Task<Protocol.Agent.V1.AgentConfig> BuildAsync(Guid endpointId)
    {
        await using var context = _db.DbFactory.CreateSystem();
        return (await new AgentConfigBuilder(_db.Licenses).BuildAsync(context, endpointId, _db.InstanceId))!.Config;
    }
}
