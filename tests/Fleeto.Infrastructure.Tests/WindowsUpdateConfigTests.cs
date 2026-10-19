using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Automatic Windows Update off in the signed configuration (0.7.0): only for a managed Windows endpoint whose enabled patch policy
/// asks for it and that Action1 reports, so an endpoint is never left without any updates and an agent-only endpoint is never
/// touched.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WindowsUpdateConfigTests
{
    private readonly TestDatabase _db;

    public WindowsUpdateConfigTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task Only_a_managed_Windows_endpoint_that_Action1_reports_turns_Windows_Update_off()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var covered = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-WU-COVERED");
        var notReported = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-WU-NOTREPORTED");
        var agentOnly = await _db.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-WU-AGENTONLY");
        var linux = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-WU-LINUX");
        await _db.LoadTestLicenseAsync(100_000);
        var now = DateTime.UtcNow;
        var policy = new PatchPolicy
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "No Windows Update", TurnOffWindowsUpdate = true, CreatedAt = now, UpdatedAt = now
        };
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.PatchPolicies.Add(policy);
            db.SitePatchPolicies.Add(new SitePatchPolicy { SiteId = site.Id, ClientId = client.Id, PatchPolicyId = policy.Id, CreatedAt = now });
            foreach (var (endpoint, status) in new[]
                     {
                         (covered, Action1AgentStatus.Installed), (notReported, Action1AgentStatus.NotReported),
                         (agentOnly, Action1AgentStatus.Installed), (linux, Action1AgentStatus.Installed)
                     })
            {
                db.Action1AgentInstallStates.Add(new Action1AgentInstallState
                {
                    EndpointId = endpoint.Id, ClientId = client.Id, Status = status, UpdatedAt = now
                });
            }

            await db.SaveChangesAsync();
            var stored = await db.Endpoints.FindAsync(linux.Id);
            stored!.OsPlatform = "linux";
            await db.SaveChangesAsync();
        }

        Assert.True((await BuildAsync(covered.Id)).TurnOffWindowsUpdate);
        Assert.False((await BuildAsync(notReported.Id)).TurnOffWindowsUpdate);
        Assert.False((await BuildAsync(agentOnly.Id)).TurnOffWindowsUpdate);
        Assert.False((await BuildAsync(linux.Id)).TurnOffWindowsUpdate);

        // A patch policy that is switched off runs no rounds in Action1, so Windows Update comes back on.
        await using (var db = _db.DbFactory.CreateSystem())
        {
            var stored = await db.PatchPolicies.FindAsync(policy.Id);
            stored!.Enabled = false;
            await db.SaveChangesAsync();
        }

        Assert.False((await BuildAsync(covered.Id)).TurnOffWindowsUpdate);
    }

    [Fact]
    public async Task Without_the_switch_or_a_patch_policy_Windows_Update_stays_as_it_is()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-WU-NOPOLICY");
        await _db.LoadTestLicenseAsync(100_000);
        var now = DateTime.UtcNow;
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.Action1AgentInstallStates.Add(new Action1AgentInstallState
            {
                EndpointId = endpoint.Id, ClientId = client.Id, Status = Action1AgentStatus.Installed, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        Assert.False((await BuildAsync(endpoint.Id)).TurnOffWindowsUpdate);

        await using (var db = _db.DbFactory.CreateSystem())
        {
            var policy = new PatchPolicy { Id = Guid.NewGuid(), ClientId = client.Id, Name = "Weekly", CreatedAt = now, UpdatedAt = now };
            db.PatchPolicies.Add(policy);
            db.EndpointPatchPolicies.Add(new EndpointPatchPolicy { EndpointId = endpoint.Id, ClientId = client.Id, PatchPolicyId = policy.Id, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        Assert.False((await BuildAsync(endpoint.Id)).TurnOffWindowsUpdate);
    }

    private async Task<Protocol.Agent.V1.AgentConfig> BuildAsync(Guid endpointId)
    {
        await using var context = _db.DbFactory.CreateSystem();
        return (await new AgentConfigBuilder(_db.Licenses).BuildAsync(context, endpointId, _db.InstanceId))!.Config;
    }
}
