using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Agent updates in web (0.2.1): only admins see the release and pause, resume or release it to every ring; each change is audited and
/// notifies the gateway; the overview counts agents per version and lists failed updates of the current release; a policy stores its ring.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class AgentUpdateServiceTests
{
    private readonly WebFixture _fixture;

    public AgentUpdateServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private AgentUpdateService Updates => _fixture.Services.GetRequiredService<AgentUpdateService>();

    private async Task<string> CreateCurrentReleaseAsync()
    {
        var version = "9.8." + Random.Shared.Next(1000, 99999);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.AgentReleases.Where(r => r.IsCurrent).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsCurrent, false));
        db.AgentReleases.Add(new AgentRelease { Version = version, ManifestSha256 = new string('a', 64), InstalledAt = _fixture.Database.Time.GetUtcNow().UtcDateTime, IsCurrent = true });
        await db.SaveChangesAsync();
        return version;
    }

    [Fact]
    public async Task Admins_pause_resume_and_release_to_all_and_every_change_is_audited()
    {
        var version = await CreateCurrentReleaseAsync();
        var admin = WebFixtureBase.Admin();

        Assert.False((await Updates.PauseAsync(WebFixtureBase.Technician(), version)).Success);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Updates.GetAsync(WebFixtureBase.Technician()));

        Assert.True((await Updates.PauseAsync(admin, version)).Success);
        Assert.False((await Updates.PauseAsync(admin, version)).Success);
        Assert.False((await Updates.ReleaseToAllAsync(admin, version)).Success);
        Assert.Contains(version, _fixture.Database.Bus.PayloadsFor(NotificationChannels.AgentReleases));

        var overview = await Updates.GetAsync(admin);
        Assert.Equal(version, overview.Release!.Version);
        Assert.NotNull(overview.Release.PausedAt);
        Assert.All(overview.Rings, r => Assert.False(r.Available));

        Assert.True((await Updates.ResumeAsync(admin, version)).Success);
        Assert.True((await Updates.ReleaseToAllAsync(admin, version)).Success);
        overview = await Updates.GetAsync(admin);
        Assert.All(overview.Rings, r => Assert.True(r.Available));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var actions = await db.AuditEntries.AsNoTracking().Where(a => a.TargetType == "AgentRelease" && a.TargetId == version).Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.AgentReleasePaused, actions);
        Assert.Contains(AuditActions.AgentReleaseResumed, actions);
        Assert.Contains(AuditActions.AgentReleaseReleasedToAll, actions);

        Assert.False((await Updates.PauseAsync(admin, "0.0.1")).Success);
    }

    [Fact]
    public async Task The_overview_counts_versions_and_lists_failed_updates_of_the_current_release()
    {
        var version = await CreateCurrentReleaseAsync();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var failed = await _fixture.Database.CreateEndpointAsync(site, hostname: "WS-ROLLED-BACK");
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.EndpointComponentStates.Add(new EndpointComponentState
            {
                EndpointId = failed.Id, ClientId = failed.ClientId, Component = AgentComponent.Agent, UpdateVersion = version,
                UpdateState = ComponentUpdateState.RolledBack, UpdateDetail = "The new version did not connect", UpdateAt = _fixture.Database.Time.GetUtcNow().UtcDateTime
            });
            await db.Endpoints.Where(e => e.Id == failed.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.AgentVersion, "0.1.0"));
            await db.SaveChangesAsync();
        }

        var overview = await Updates.GetAsync(WebFixtureBase.Admin());
        var problem = Assert.Single(overview.Problems, p => p.EndpointId == failed.Id);
        Assert.Equal(ComponentUpdateState.RolledBack, problem.State);
        Assert.Equal(client.Code, problem.ClientCode);
        Assert.True(overview.AgentsOlder >= 1);
    }

    [Fact]
    public async Task The_endpoint_detail_shows_what_the_installer_waits_for_with_the_ring_of_its_site()
    {
        var version = await CreateCurrentReleaseAsync();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, hostname: "WS-WAITING");
        var policies = _fixture.Services.GetRequiredService<PolicyService>();
        var policy = await policies.CreateAsync(WebFixtureBase.Admin(), null,
            new PolicyInput("Ring " + Guid.NewGuid().ToString("N")[..6], null, 30, 3600, 10, AlertSeverity.Critical, UpdateRing: UpdateRing.Delayed));
        Assert.True(policy.Success, policy.Problem);
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policy.Value, CreatedAt = now });
            db.EndpointComponentStates.Add(new EndpointComponentState
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, Component = AgentComponent.Watchdog, WaitVersion = version,
                WaitReason = ComponentUpdateWait.UpdateRing, WaitAt = now
            });
            await db.SaveChangesAsync();
        }

        // A technician limited to the client sees the wait and the ring of the site.
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var detail = await endpoints.GetAsync(WebFixtureBase.CallerWith(new RestrictedClientScope([client.Id]), FleetoRoles.Technician), endpoint.Id);
        Assert.NotNull(detail);
        var watchdog = Assert.Single(detail.Components!, c => c.Component == AgentComponent.Watchdog);
        Assert.Equal(ComponentUpdateWait.UpdateRing, watchdog.WaitReason);
        Assert.Equal(version, detail.ReleaseVersion);
        Assert.Equal(UpdateRing.Delayed, detail.ReleaseRing!.Ring);
        Assert.False(detail.ReleaseRing.Paused);
        Assert.True(detail.ReleaseRing.AvailableAt > now.AddDays(13));
    }

    [Fact]
    public async Task A_policy_stores_its_update_ring()
    {
        var policies = _fixture.Services.GetRequiredService<PolicyService>();
        var created = await policies.CreateAsync(WebFixtureBase.Admin(), null,
            new PolicyInput("Ring " + Guid.NewGuid().ToString("N")[..6], null, 30, 3600, 10, AlertSeverity.Critical, UpdateRing: UpdateRing.Delayed));
        Assert.True(created.Success, created.Problem);

        var listed = (await policies.ListAsync(WebFixtureBase.Admin())).Single(p => p.Id == created.Value);
        Assert.Equal(UpdateRing.Delayed, listed.UpdateRing);

        Assert.False((await policies.CreateAsync(WebFixtureBase.Admin(), null,
            new PolicyInput("Ring " + Guid.NewGuid().ToString("N")[..6], null, 30, 3600, 10, AlertSeverity.Critical, UpdateRing: (UpdateRing)42))).Success);
    }
}
