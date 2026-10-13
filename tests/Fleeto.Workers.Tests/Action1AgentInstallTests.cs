using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The automatic installation of the Action1 agent (0.6.0): a patch policy with the switch on gets the agent onto its
/// managed Windows endpoints through a signed job without a user; a failure says why and is tried again after 1, 6 and
/// 24 hours, then Fleeto stops; the agent counts as installed only once Action1 reports the endpoint; an agent removed
/// through Fleeto stays removed until a technician installs it. The "Action1 agent" check says the same.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class Action1AgentInstallTests
{
    private const string InstallerUrl = "https://app.eu.action1.com/agent/6e1c/Windows/agent(_000__Auto).msi";
    private readonly WorkersFixture _fixture;

    public Action1AgentInstallTests(WorkersFixture fixture) => _fixture = fixture;

    private DateTime Now => _fixture.Now;

    private async Task<(Client Client, Site Site, Endpoint Endpoint)> SeedAsync(bool autoInstall = true, bool online = true,
        string installerUrl = InstallerUrl)
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var client = await _fixture.Db.CreateClientAsync();
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Auto " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "AUTO-" + Guid.NewGuid().ToString("N")[..6]);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
        await db.SaveChangesAsync();

        var integration = new Integration
        {
            Id = Guid.NewGuid(), Type = IntegrationType.Action1, Region = Action1Region.Europe, CredentialName = "api-key-auto@action1.com",
            Enabled = true, CreatedAt = Now, UpdatedAt = Now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Db.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "auto-secret"));
        db.Integrations.Add(integration);
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = client.Id, ExternalTenantId = "org-auto", ExternalTenantName = "Auto org",
            AgentInstallerUrl = installerUrl, CreatedAt = Now
        });
        var policy = new PatchPolicy
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "Monday " + client.Code, AutoInstallAgent = autoInstall, CreatedAt = Now, UpdatedAt = Now
        };
        db.PatchPolicies.Add(policy);
        db.ClientPatchPolicies.Add(new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = policy.Id, CreatedAt = Now });
        var row = await db.Endpoints.SingleAsync(e => e.Id == endpoint.Id);
        row.IsOnline = online;
        await db.SaveChangesAsync();
        return (client, site, endpoint);
    }

    private Action1AgentInstallService Service() =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus, new AlertNotificationService(_fixture.Db.Time), _fixture.Db.Licenses,
            _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<Action1AgentInstallService>.Instance);

    private async Task<Action1AgentInstallState> StateAsync(Endpoint endpoint)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Action1AgentInstallStates.AsNoTracking().SingleAsync(a => a.EndpointId == endpoint.Id);
    }

    private async Task<List<Job>> JobsAsync(Endpoint endpoint)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().Where(j => j.EndpointId == endpoint.Id).OrderBy(j => j.CreatedAt).ToListAsync();
    }

    /// <summary>Ends the newest job of the endpoint the way the gateway would record it.</summary>
    private async Task EndJobAsync(Endpoint endpoint, JobState state, int? exitCode = null)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var job = await db.Jobs.Where(j => j.EndpointId == endpoint.Id).OrderByDescending(j => j.CreatedAt).FirstAsync();
        job.State = state;
        job.Signature ??= [1];
        job.Result = exitCode is null ? null : JobResult.Exited;
        job.ExitCode = exitCode;
        job.CompletedAt = Now;
        await db.SaveChangesAsync();
    }

    private async Task MakeNextAttemptDueAsync(Endpoint endpoint)
    {
        // Each attempt is a newer job than the one before.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(1));
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var row = await db.Action1AgentInstallStates.SingleAsync(a => a.EndpointId == endpoint.Id);
        row.NextAttemptAt = Now.AddMinutes(-1);
        await db.SaveChangesAsync();
    }

    private async Task<CheckResult> LatestResultAsync(Endpoint endpoint, CheckDefinition check)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.CheckResults.AsNoTracking()
            .Where(r => r.EndpointId == endpoint.Id && r.CheckDefinitionId == check.Id)
            .OrderByDescending(r => r.Id)
            .FirstAsync();
    }

    [Fact]
    public async Task An_online_endpoint_without_the_agent_gets_one_signed_installation_without_a_user()
    {
        var (client, _, endpoint) = await SeedAsync();

        await Service().RunAsync(CancellationToken.None);
        await Service().RunAsync(CancellationToken.None);

        var job = Assert.Single(await JobsAsync(endpoint));
        Assert.Equal(JobType.Action1Agent, job.Type);
        Assert.Equal(Action1AgentInstall.AutomaticInitiator, job.InitiatedByUserId);
        Assert.StartsWith("Fleeto (patch policy Monday ", job.InitiatedByName);
        // Bound to the body of the client's link, as the signer composes it again.
        Assert.Equal(ScriptLanguages.Sha256(Action1AgentInstall.Body(InstallerUrl)), job.ScriptSha256);
        var state = await StateAsync(endpoint);
        Assert.Equal((Action1AgentStatus.Installing, job.Id), (state.Status, state.JobId));

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.True(await db.SigningRequests.AnyAsync(r => r.SubjectId == job.Id && r.Kind == SigningRequestKind.Job));
        Assert.True(await db.AuditEntries.AnyAsync(a => a.TargetId == job.Id.ToString() && a.ClientId == client.Id));
    }

    [Fact]
    public async Task Without_the_switch_nothing_is_installed_and_the_check_says_the_agent_is_missing()
    {
        var (_, site, endpoint) = await SeedAsync(autoInstall: false);
        var check = await _fixture.CreateCheckAsync(site, CheckType.Action1Agent, null, null);

        await Service().RunAsync(CancellationToken.None);

        Assert.Empty(await JobsAsync(endpoint));
        Assert.Equal(Action1AgentStatus.Missing, (await StateAsync(endpoint)).Status);
        var result = await LatestResultAsync(endpoint, check);
        Assert.Equal(0, result.Value);
        Assert.Contains("No Action1 agent is installed", result.Detail);
    }

    [Fact]
    public async Task An_offline_endpoint_waits_and_the_check_says_so()
    {
        var (_, site, endpoint) = await SeedAsync(online: false);
        var check = await _fixture.CreateCheckAsync(site, CheckType.Action1Agent, null, null);

        await Service().RunAsync(CancellationToken.None);

        Assert.Empty(await JobsAsync(endpoint));
        Assert.Equal(Action1AgentStatus.Missing, (await StateAsync(endpoint)).Status);
        var result = await LatestResultAsync(endpoint, check);
        Assert.Equal(0, result.Value);
        Assert.Equal(Action1AgentInstallService.OfflineReason, result.Detail);
    }

    [Fact]
    public async Task A_failed_installation_says_why_is_tried_again_after_1_6_and_24_hours_and_then_stops()
    {
        var (_, site, endpoint) = await SeedAsync();
        var check = await _fixture.CreateCheckAsync(site, CheckType.Action1Agent, null, null);
        await Service().RunAsync(CancellationToken.None);

        await EndJobAsync(endpoint, JobState.Failed, 1618);
        await Service().RunAsync(CancellationToken.None);

        var failed = await StateAsync(endpoint);
        Assert.Equal((Action1AgentStatus.Failed, 1), (failed.Status, failed.Attempts));
        Assert.Contains("msiexec 1618", failed.Failure);
        Assert.Equal(Now + TimeSpan.FromHours(1), failed.NextAttemptAt!.Value, TimeSpan.FromSeconds(1));
        var result = await LatestResultAsync(endpoint, check);
        Assert.Equal(0, result.Value);
        Assert.Contains("msiexec 1618", result.Detail);
        Assert.Contains("tries again", result.Detail);

        // Not before the next attempt is due.
        await Service().RunAsync(CancellationToken.None);
        Assert.Single(await JobsAsync(endpoint));

        TimeSpan[] waits = [TimeSpan.FromHours(6), TimeSpan.FromHours(24)];
        foreach (var wait in waits)
        {
            await MakeNextAttemptDueAsync(endpoint);
            await Service().RunAsync(CancellationToken.None);
            await EndJobAsync(endpoint, JobState.Failed, Action1AgentInstall.DownloadFailedExitCode);
            await Service().RunAsync(CancellationToken.None);
            var state = await StateAsync(endpoint);
            Assert.Equal(Action1AgentStatus.Failed, state.Status);
            Assert.Equal(Now + wait, state.NextAttemptAt!.Value, TimeSpan.FromSeconds(1));
            Assert.Contains("could not download", state.Failure);
        }

        await MakeNextAttemptDueAsync(endpoint);
        await Service().RunAsync(CancellationToken.None);
        await EndJobAsync(endpoint, JobState.Expired);
        await Service().RunAsync(CancellationToken.None);

        var stopped = await StateAsync(endpoint);
        Assert.Equal((Action1AgentStatus.Stopped, 4, (DateTime?)null), (stopped.Status, stopped.Attempts, stopped.NextAttemptAt));
        Assert.Contains("not online within 24 hours", stopped.Failure);
        Assert.Contains("stopped after 4 attempts", (await LatestResultAsync(endpoint, check)).Detail);

        await Service().RunAsync(CancellationToken.None);
        Assert.Equal(4, (await JobsAsync(endpoint)).Count);
    }

    [Fact]
    public async Task The_agent_counts_as_installed_only_once_Action1_reports_the_endpoint()
    {
        var (client, site, endpoint) = await SeedAsync();
        var check = await _fixture.CreateCheckAsync(site, CheckType.Action1Agent, null, null);
        await Service().RunAsync(CancellationToken.None);
        await EndJobAsync(endpoint, JobState.Succeeded, 0);

        await Service().RunAsync(CancellationToken.None);
        Assert.Equal(Action1AgentStatus.Installing, (await StateAsync(endpoint)).Status);

        // The Fleeto agent reports the new agent: Fleeto asks Action1 for the endpoint at once.
        var action1Id = Guid.NewGuid().ToString();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.InventorySnapshots.Add(new InventorySnapshot
            {
                EndpointId = endpoint.Id, ClientId = client.Id, ReceivedAt = Now, Hash = Guid.NewGuid().ToString("N"), Action1AgentId = action1Id
            });
            await db.SaveChangesAsync();
        }

        await Service().RunAsync(CancellationToken.None);
        Assert.Equal(Action1AgentStatus.Installing, (await StateAsync(endpoint)).Status);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var sync = await db.PatchRequests.AsNoTracking().SingleAsync(r => r.EndpointId == endpoint.Id);
            Assert.Equal(PatchRequestKind.Sync, sync.Kind);
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = client.Id, ExternalEndpointId = action1Id, ExternalTenantId = "org-auto", UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        await Service().RunAsync(CancellationToken.None);

        var installed = await StateAsync(endpoint);
        Assert.Equal((Action1AgentStatus.Installed, 0, (string?)null), (installed.Status, installed.Attempts, installed.Failure));
        var result = await LatestResultAsync(endpoint, check);
        Assert.Equal(1, result.Value);
    }

    [Fact]
    public async Task An_agent_removed_through_Fleeto_is_not_installed_again_until_a_technician_installs_it()
    {
        var (client, _, endpoint) = await SeedAsync(online: false);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.PatchRequests.Add(new PatchRequest
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Kind = PatchRequestKind.RemoveAgent,
                RequestedByUserId = Guid.NewGuid(), RequestedByName = "Tech", RequestedAt = Now.AddMinutes(-10), CompletedAt = Now.AddMinutes(-9)
            });
            var row = await db.Endpoints.SingleAsync(e => e.Id == endpoint.Id);
            row.IsOnline = true;
            await db.SaveChangesAsync();
        }

        await Service().RunAsync(CancellationToken.None);
        await Service().RunAsync(CancellationToken.None);

        Assert.Empty(await JobsAsync(endpoint));
        Assert.Equal(Action1AgentStatus.Removed, (await StateAsync(endpoint)).Status);

        // A technician installs it by hand: Fleeto follows that installation and looks after the agent again.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.Jobs.Add(new Job
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, BatchId = Guid.NewGuid(), Type = JobType.Action1Agent,
                ScriptName = Action1AgentInstall.JobName, Language = ScriptLanguage.PowerShell, ScriptSha256 = "a", TimeoutSeconds = 60,
                MaxOutputBytes = 1024, CreatedAt = Now, ValidUntil = Now.AddDays(1), InitiatedByUserId = Guid.NewGuid(),
                InitiatedByName = "Tess Tech", State = JobState.Queued, Signature = [1]
            });
            await db.SaveChangesAsync();
        }

        await Service().RunAsync(CancellationToken.None);

        var state = await StateAsync(endpoint);
        Assert.Equal((Action1AgentStatus.Installing, 0), (state.Status, state.Attempts));
        Assert.Single(await JobsAsync(endpoint));
    }
}
