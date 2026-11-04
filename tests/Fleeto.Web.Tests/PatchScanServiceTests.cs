using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// The Patches tab of an endpoint the Fleeto agent patches (0.9.0 step 1): it shows what the scans found without Action1 being set up,
/// an endpoint that was never scanned successfully is not counted as compliant, and "Scan now" is once per 15 minutes, audited, only
/// for a user with Sync patches on a managed endpoint whose engine is Fleeto and whose agent scans.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class PatchScanServiceTests
{
    private readonly WebFixture _fixture;

    public PatchScanServiceTests(WebFixture fixture) => _fixture = fixture;

    private PatchService Patches => _fixture.Services.GetRequiredService<PatchService>();

    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    private async Task<(Client Client, Endpoint Endpoint)> LinuxAsync(EndpointTier tier = EndpointTier.Managed, string agentVersion = PatchScanRules.MinimumAgentVersion)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync("PS" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, tier, "LNX-" + client.Code);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, "linux").SetProperty(e => e.OsName, "Ubuntu 24.04.1 LTS").SetProperty(e => e.AgentVersion, agentVersion)
            .SetProperty(e => e.IsOnline, true));
        return (client, await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id));
    }

    private async Task StateAsync(Endpoint endpoint, DateTime? scannedAt, params string[] missing)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.EndpointPatchStates.Add(new EndpointPatchState
        {
            EndpointId = endpoint.Id, ClientId = endpoint.ClientId, Engine = PatchEngine.Fleeto, ScanTool = "apt", ProductLastSeenAt = scannedAt,
            MissingOther = missing.Length, UpdatedAt = Now, ScanProblem = scannedAt is null ? "Could not get the lock." : null
        });
        foreach (var name in missing)
        {
            db.EndpointMissingUpdates.Add(new EndpointMissingUpdate
            {
                Id = Guid.NewGuid(), EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ExternalUpdateId = name + ":amd64", Name = name,
                Version = "2.0", InstalledVersion = "1.0", UpdateType = PatchScanRules.RegularType, Origin = "noble-updates", UpdatedAt = Now, FirstSeenAt = Now
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_tab_shows_what_the_scan_found_without_Action1_being_set_up()
    {
        var (_, endpoint) = await LinuxAsync();
        await StateAsync(endpoint, Now, "curl", "tzdata");

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);

        Assert.NotNull(view);
        Assert.True(view.ByFleeto);
        Assert.False(view.Configured);
        Assert.Null(view.ScanNote);
        Assert.Equal(["curl", "tzdata"], view.Missing.Select(m => m.Name));
        Assert.Equal("noble-updates", view.Missing[0].Origin);
        Assert.Equal(2, view.State!.MissingOther);
    }

    [Fact]
    public async Task An_older_agent_is_told_to_update_and_cannot_be_asked_to_scan()
    {
        var (_, endpoint) = await LinuxAsync(agentVersion: "0.8.0");

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);
        Assert.True(view!.ByFleeto);
        Assert.Contains(PatchScanRules.MinimumAgentVersion, view.ScanNote);

        var result = await Patches.RequestScanAsync(WebFixture.Technician(), endpoint.Id);
        Assert.False(result.Success);
        Assert.Contains("Update the agent", result.Problem);
    }

    [Fact]
    public async Task An_endpoint_never_scanned_successfully_is_not_counted_as_compliant()
    {
        var (client, endpoint) = await LinuxAsync();
        await StateAsync(endpoint, null);

        var compliance = await Patches.GetComplianceAsync(WebFixture.Admin(), client.Id);
        Assert.Equal(0, compliance.Covered);

        var (other, scanned) = await LinuxAsync();
        await StateAsync(scanned, Now);
        compliance = await Patches.GetComplianceAsync(WebFixture.Admin(), other.Id);
        Assert.Equal((1, 1), (compliance.Covered, compliance.Compliant));
    }

    [Fact]
    public async Task Scan_now_is_once_per_15_minutes_audited_and_only_for_a_user_who_may_sync()
    {
        var (_, endpoint) = await LinuxAsync();

        Assert.False((await Patches.RequestScanAsync(WebFixture.CallerWith(Infrastructure.Data.SystemClientScope.Instance, FleetoRoles.ReadOnly),
            endpoint.Id)).Success);

        var first = await Patches.RequestScanAsync(WebFixture.Technician(), endpoint.Id);
        Assert.True(first.Success);
        Assert.True(first.Value);
        var again = await Patches.RequestScanAsync(WebFixture.Technician(), endpoint.Id);
        Assert.False(again.Success);
        Assert.Contains("less than 15 minutes ago", again.Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var request = await db.PatchScanRequests.AsNoTracking().SingleAsync(r => r.EndpointId == endpoint.Id);
        Assert.Equal(Now + PatchScanRules.RequestLifetime, request.ExpiresAt, TimeSpan.FromSeconds(1));
        Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.PatchScanRequested && a.TargetId == endpoint.Id.ToString()));

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);
        Assert.True(view!.LastScanRequest!.IsPending(Now));
    }

    [Fact]
    public async Task Scan_now_is_refused_on_an_agent_only_endpoint_and_one_Action1_patches()
    {
        var (_, agentOnly) = await LinuxAsync(EndpointTier.AgentOnly);
        Assert.False((await Patches.RequestScanAsync(WebFixture.Technician(), agentOnly.Id)).Success);

        var (_, action1) = await LinuxAsync();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.InventorySnapshots.Add(new InventorySnapshot
            {
                EndpointId = action1.Id, ClientId = action1.ClientId, ReceivedAt = Now, Hash = "h", Action1AgentId = "a1-agent"
            });
            await db.SaveChangesAsync();
        }

        var result = await Patches.RequestScanAsync(WebFixture.Technician(), action1.Id);
        Assert.False(result.Success);
        Assert.Contains("Action1 patches this endpoint", result.Problem);
    }
}
