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

    [Fact]
    public async Task A_Windows_endpoint_without_Action1_is_patched_by_Fleeto_and_says_where_it_searched()
    {
        var (_, endpoint) = await LinuxAsync();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
                .SetProperty(e => e.OsPlatform, "windows").SetProperty(e => e.OsName, "Windows 11 Pro"));
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, Engine = PatchEngine.Fleeto, ScanTool = "windows_update", ScanSource = "wsus",
                ScanSourceServer = "wsus01:8530", ProductLastSeenAt = Now, MissingCritical = 1, UpdatedAt = Now
            });
            db.EndpointMissingUpdates.Add(new EndpointMissingUpdate
            {
                Id = Guid.NewGuid(), EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ExternalUpdateId = "a:201", Name = "2026-10 Cumulative Update",
                KbNumber = "KB5031354", Severity = PatchSeverity.Critical, UpdateType = "Security Updates", SizeBytes = 812_000_000, Downloaded = true,
                UpdatedAt = Now, FirstSeenAt = Now
            });
            await db.SaveChangesAsync();
        }

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);

        Assert.True(view!.ByFleeto);
        Assert.Null(view.ScanNote);
        Assert.Equal("WSUS (wsus01:8530)", PatchScanRules.DescribeSource(view.State!.ScanSource, view.State.ScanSourceServer));
        var update = Assert.Single(view.Missing);
        Assert.Equal(("KB5031354", 812_000_000L, true), (update.KbNumber, update.SizeBytes!.Value, update.Downloaded));
        Assert.True((await Patches.RequestScanAsync(WebFixture.Technician(), endpoint.Id)).Success);
    }

    [Fact]
    public async Task A_deployment_by_Fleeto_writes_one_bound_installation_job_per_endpoint_and_asks_for_its_signature()
    {
        var (_, endpoint) = await LinuxAsync();
        await StateAsync(endpoint, Now, "curl", "tzdata");

        var started = await Patches.StartDeploymentAsync(WebFixture.Technician(), [endpoint.Id], ["curl:amd64"], autoReboot: true);

        Assert.True(started.Success, started.Problem);
        var view = Assert.Single(started.Value!.Deployments);
        Assert.Equal((PatchEngine.Fleeto, PatchDeploymentScope.Specified, PatchDeploymentState.Running), (view.Engine, view.Scope, view.State));
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var target = await db.PatchDeploymentTargets.AsNoTracking().SingleAsync(t => t.DeploymentId == view.Id);
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == target.JobId);
        Assert.Equal((JobType.PatchInstall, JobState.PendingSignature, view.Id, target.Id),
            (job.Type, job.State, job.PatchDeploymentId!.Value, job.PatchDeploymentTargetId!.Value));
        Assert.True(await db.SigningRequests.AnyAsync(r => r.SubjectId == job.Id && r.Kind == SigningRequestKind.Job));
        Assert.Equal("curl:amd64", (await db.PatchDeploymentUpdates.AsNoTracking().SingleAsync(u => u.DeploymentId == view.Id)).ExternalUpdateId);

        // A second installation while the first is on its way is skipped with the reason.
        var again = await Patches.StartDeploymentAsync(WebFixture.Technician(), [endpoint.Id]);
        Assert.False(again.Success);
        Assert.Contains("already on its way", again.Problem);
    }

    [Fact]
    public async Task A_deployment_by_Fleeto_skips_an_endpoint_whose_agent_cannot_install_and_one_whose_last_scan_found_nothing()
    {
        var (_, old) = await LinuxAsync(agentVersion: "0.8.0");
        var (_, clean) = await LinuxAsync();
        await StateAsync(clean, Now);

        var started = await Patches.StartDeploymentAsync(WebFixture.Technician(), [old.Id, clean.Id]);

        Assert.False(started.Success);
        Assert.Contains("Nothing was deployed", started.Problem);
    }

    [Fact]
    public async Task A_patch_policy_of_engine_Fleeto_is_saved_with_its_options_and_the_tab_shows_its_next_round()
    {
        var (client, endpoint) = await LinuxAsync();
        var policies = _fixture.Services.GetRequiredService<PatchPolicyService>();
        var input = new PatchPolicyInput("Fleeto " + client.Code, null, true, PatchScheduleKind.Weekly, WeekDays.All, 1, 2, DayOfWeek.Tuesday, 20 * 60,
            false, PatchUpdateScope.All, [], [], [], [], [], false, 3, true, true, null, 30, 12, Engine: PatchEngine.Fleeto, LinuxSecurityOnly: true,
            TurnOffDistributionUpdates: true, RandomDelayMinutes: 0);

        // What only Action1 has is refused for Fleeto.
        var refused = await policies.CreateAsync(WebFixture.Admin(), client.Id, input with { RequireApproval = true });
        Assert.False(refused.Success);

        var created = await policies.CreateAsync(WebFixture.Admin(), client.Id, input);
        Assert.True(created.Success, created.Problem);
        var saved = await policies.GetAsync(WebFixture.Admin(), created.Value);
        Assert.Equal((PatchEngine.Fleeto, true, true, 0), (saved!.Engine, saved.LinuxSecurityOnly, saved.TurnOffDistributionUpdates, saved.RandomDelayMinutes));

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.ClientPatchPolicies.Add(new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = created.Value });
            await db.SaveChangesAsync();
        }

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);
        var rounds = view!.Rounds;
        Assert.NotNull(rounds);
        Assert.Equal(("UTC", true), (rounds!.Zone, rounds.Enabled));
        Assert.NotNull(rounds.NextRound);
        Assert.Null(rounds.LastRoundAt);

        // A change of a switch that reaches the agent signs the configurations again.
        var configsBefore = await CountConfigChangesAsync();
        Assert.True((await policies.UpdateAsync(WebFixture.Admin(), created.Value, input with { TurnOffDistributionUpdates = false })).Success);
        Assert.True(await CountConfigChangesAsync() > configsBefore);
        var list = await policies.ListAsync(WebFixture.Admin());
        Assert.Equal(PatchEngine.Fleeto, list.Single(p => p.Id == created.Value).Engine);
    }

    private async Task<int> CountConfigChangesAsync()
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.ConfigChangeEvents.CountAsync();
    }
}
