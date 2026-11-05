using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;
using JobType = Fleeto.Core.Entities.JobType;

namespace Fleeto.Signer.Tests;

/// <summary>
/// The installation of a round of a patch policy of engine Fleeto (0.9.0 step 4): the signer composes it from the policy (restart,
/// notice, timeout, filters and the updates a deferral allows), never restarts a Proxmox VE host, and refuses a round whose policy is off,
/// no longer the endpoint's, whose moment is not an occurrence of the schedule, or that has nothing to install.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class PatchRoundJobTests
{
    private readonly SignerFixture _fixture;

    public PatchRoundJobTests(SignerFixture fixture) => _fixture = fixture;

    private DateTime RoundAt => new(_fixture.Now.Year, _fixture.Now.Month, _fixture.Now.Day, _fixture.Now.Hour, _fixture.Now.Minute, 0, DateTimeKind.Utc);

    private async Task<(Endpoint Endpoint, PatchPolicy Policy)> ScopeAsync(string osName = "Ubuntu 24.04.1 LTS", Action<PatchPolicy>? configure = null,
        bool link = true)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "LNX-PR");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, "linux").SetProperty(e => e.OsName, osName)
            .SetProperty(e => e.AgentVersion, PatchRules.MinimumInstallAgentVersion));
        db.InventorySnapshots.Add(new InventorySnapshot { EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = _fixture.Now, Hash = "h" });
        db.EndpointMissingUpdates.Add(new EndpointMissingUpdate
        {
            Id = Guid.NewGuid(), EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ExternalUpdateId = "libssl3:amd64", Name = "libssl3",
            Version = "3.0.13-0ubuntu3.5", UpdateType = "Security Updates", FirstSeenAt = _fixture.Now.AddDays(-10), UpdatedAt = _fixture.Now
        });
        db.EndpointMissingUpdates.Add(new EndpointMissingUpdate
        {
            Id = Guid.NewGuid(), EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ExternalUpdateId = "curl:amd64", Name = "curl",
            Version = "8.5.0-2ubuntu10.6", UpdateType = "Regular Updates", FirstSeenAt = _fixture.Now.AddDays(-1), UpdatedAt = _fixture.Now
        });
        var policy = new PatchPolicy
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "Round " + Guid.NewGuid().ToString("N")[..8], Engine = PatchEngine.Fleeto,
            ScheduleKind = PatchScheduleKind.Weekly, WeekDays = WeekDays.All, StartMinute = RoundAt.Hour * 60 + RoundAt.Minute,
            EndpointLocalTime = false, RetryHours = 12, AutoReboot = true, RebootTimeoutMinutes = 20, InstallTimeoutMinutes = 90,
            CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
        };
        configure?.Invoke(policy);
        db.PatchPolicies.Add(policy);
        await db.SaveChangesAsync();
        if (link)
        {
            db.ClientPatchPolicies.Add(new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = policy.Id });
            await db.SaveChangesAsync();
        }

        return (await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id), policy);
    }

    private async Task<Job> RoundAsync(Endpoint endpoint, PatchPolicy policy, DateTime? roundAt = null)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var at = roundAt ?? RoundAt;
        var deployment = new PatchDeployment
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, BatchId = Guid.NewGuid(), Engine = PatchEngine.Fleeto, Scope = PatchDeploymentScope.PatchPolicy,
            AutoReboot = policy.AutoReboot, State = PatchDeploymentState.Running, PatchPolicyId = policy.Id, RoundAt = at,
            RequestedByUserId = PatchRounds.AutomaticInitiator, RequestedByName = PatchRounds.AutomaticInitiatorName(policy.Name), RequestedAt = _fixture.Now
        };
        var target = new PatchDeploymentTarget
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Hostname = endpoint.Hostname, UpdatedAt = _fixture.Now, DueAt = at
        };
        deployment.Targets.Add(target);
        db.PatchDeployments.Add(deployment);
        await db.SaveChangesAsync();
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = deployment.BatchId, Type = JobType.PatchInstall,
            ScriptName = PatchRules.InstallJobName, CreatedAt = _fixture.Now, ValidUntil = PatchRounds.ValidUntil(policy, at),
            InitiatedByUserId = PatchRounds.AutomaticInitiator, InitiatedByName = deployment.RequestedByName, PatchDeploymentId = deployment.Id,
            PatchDeploymentTargetId = target.Id
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<Job> SignAsync(Job job)
    {
        await _fixture.ProcessAsync(SigningRequestKind.Job, job.ClientId, job.Id, [], "workers:patch-rounds");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
    }

    [Fact]
    public async Task The_signer_composes_a_round_from_its_patch_policy()
    {
        var (endpoint, policy) = await ScopeAsync(configure: p => p.LinuxSecurityOnly = true);
        var signed = await SignAsync(await RoundAsync(endpoint, policy));

        Assert.Equal(JobState.Queued, signed.State);
        var payload = JobPayload.Parser.ParseFrom(signed.Payload);
        Assert.Equal(Protocol.Agent.V1.JobType.PatchInstall, payload.Type);
        var install = payload.PatchInstall;
        Assert.Equal(PatchInstallScope.AllMissing, install.Scope);
        Assert.True(install.Filter.SecurityOnly);
        Assert.False(install.Filter.RestrictToIds);
        Assert.Equal(PatchRestart.IfRequired, install.Restart);
        Assert.Equal(20u * 60, install.RestartNoticeSeconds);
        Assert.Equal(90u * 60, payload.TimeoutSeconds);
        Assert.Equal(90 * 60, signed.TimeoutSeconds);
    }

    [Fact]
    public async Task A_wait_after_release_lists_only_the_updates_old_enough_and_nothing_old_enough_is_refused()
    {
        var (endpoint, policy) = await ScopeAsync(configure: p => p.InstallDelayDays = 7);
        var install = JobPayload.Parser.ParseFrom((await SignAsync(await RoundAsync(endpoint, policy))).Payload).PatchInstall;
        Assert.True(install.Filter.RestrictToIds);
        Assert.Equal(["libssl3:amd64"], install.Filter.AllowedIds);

        var (young, youngPolicy) = await ScopeAsync(configure: p => p.InstallDelayDays = 30);
        var refused = await SignAsync(await RoundAsync(young, youngPolicy));
        Assert.Equal((JobState.Refused, JobHandler.RoundNothingReason), (refused.State, refused.RefusalReason));
    }

    [Fact]
    public async Task A_round_never_restarts_a_Proxmox_VE_host()
    {
        var (host, policy) = await ScopeAsync("Proxmox VE 8.4.1");
        var install = JobPayload.Parser.ParseFrom((await SignAsync(await RoundAsync(host, policy))).Payload).PatchInstall;
        Assert.Equal(PatchRestart.Never, install.Restart);
    }

    [Fact]
    public async Task A_round_of_a_policy_that_is_off_no_longer_linked_or_at_another_moment_is_refused()
    {
        var (off, offPolicy) = await ScopeAsync();
        var offJob = await RoundAsync(off, offPolicy);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.PatchPolicies.Where(p => p.Id == offPolicy.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.Enabled, false));
        }

        Assert.Equal(JobHandler.RoundPolicyReason, (await SignAsync(offJob)).RefusalReason);

        var (unlinked, unlinkedPolicy) = await ScopeAsync();
        var unlinkedJob = await RoundAsync(unlinked, unlinkedPolicy);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.ClientPatchPolicies.Where(l => l.PatchPolicyId == unlinkedPolicy.Id).ExecuteDeleteAsync();
        }

        Assert.Equal(JobHandler.RoundTargetReason, (await SignAsync(unlinkedJob)).RefusalReason);

        var (moved, movedPolicy) = await ScopeAsync();
        var movedJob = await RoundAsync(moved, movedPolicy, RoundAt.AddMinutes(-7));
        Assert.Equal(JobHandler.RoundScheduleReason, (await SignAsync(movedJob)).RefusalReason);
    }
}
