using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The rounds of a patch policy of engine Fleeto (0.9.0 step 4): an occurrence becomes one deployment per client with a target per
/// endpoint the policy applies to, only after the workers first saw the policy; each target gets its job without a user when its turn
/// came; an endpoint whose agent is too old or whose last scan found nothing is answered at once; a round runs once per occurrence; the
/// "Scheduled patching" check counts from when the policy first scheduled the endpoint; and the database refuses a round job of a
/// policy that is off or for Action1.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PatchRoundServiceTests
{
    private readonly WorkersFixture _fixture;

    public PatchRoundServiceTests(WorkersFixture fixture) => _fixture = fixture;

    private async Task<(Endpoint Endpoint, PatchPolicy Policy)> ScopeAsync(string agentVersion = PatchRules.MinimumInstallAgentVersion,
        PatchEngine engine = PatchEngine.Fleeto)
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (client, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "LNX-" + Guid.NewGuid().ToString("N")[..6], EndpointClass.Server);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, "linux").SetProperty(e => e.OsName, "Ubuntu 24.04.1 LTS").SetProperty(e => e.AgentVersion, agentVersion));
        db.InventorySnapshots.Add(new InventorySnapshot { EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = _fixture.Now, Hash = "h" });
        // Every day, a minute from now in UTC: the round comes once the clock passes it.
        var start = _fixture.Now.AddMinutes(1);
        var policy = new PatchPolicy
        {
            Id = Guid.NewGuid(), ClientId = client.Id, Name = "Round " + Guid.NewGuid().ToString("N")[..8], Engine = engine,
            ScheduleKind = PatchScheduleKind.Weekly, WeekDays = WeekDays.All, StartMinute = start.Hour * 60 + start.Minute,
            EndpointLocalTime = false, RetryHours = 6, RandomDelayMinutes = 0, CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
        };
        db.PatchPolicies.Add(policy);
        await db.SaveChangesAsync();
        db.ClientPatchPolicies.Add(new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = policy.Id });
        await db.SaveChangesAsync();
        return (await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id), policy);
    }

    private async Task<PatchDeployment?> RoundOfAsync(PatchPolicy policy)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.PatchDeployments.AsNoTracking().Include(d => d.Targets).SingleOrDefaultAsync(d => d.PatchPolicyId == policy.Id);
    }

    [Fact]
    public async Task An_occurrence_after_the_policy_was_first_seen_becomes_a_round_and_its_endpoint_gets_a_job_without_a_user()
    {
        var (endpoint, policy) = await ScopeAsync();
        var rounds = _fixture.PatchRounds();

        await rounds.StartRoundsAsync(CancellationToken.None);
        Assert.Null(await RoundOfAsync(policy));

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(2));
        await rounds.StartRoundsAsync(CancellationToken.None);
        await rounds.StartRoundsAsync(CancellationToken.None);
        var round = await RoundOfAsync(policy);
        Assert.NotNull(round);
        Assert.Equal((PatchEngine.Fleeto, PatchDeploymentScope.PatchPolicy, PatchRounds.AutomaticInitiator),
            (round!.Engine, round.Scope, round.RequestedByUserId));
        var target = Assert.Single(round.Targets);
        Assert.Equal((endpoint.Id, PatchDeploymentTargetState.Pending), (target.EndpointId, target.State));
        Assert.NotNull(target.DueAt);

        await rounds.StartJobsAsync(CancellationToken.None);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.PatchDeploymentTargetId == target.Id);
        Assert.Equal((JobType.PatchInstall, JobState.PendingSignature, PatchRounds.AutomaticInitiator, round.Id),
            (job.Type, job.State, job.InitiatedByUserId, job.PatchDeploymentId!.Value));
        Assert.Equal(PatchRounds.ValidUntil(policy, round.RoundAt!.Value), job.ValidUntil, TimeSpan.FromSeconds(1));
        Assert.True(await db.SigningRequests.AnyAsync(r => r.SubjectId == job.Id && r.Kind == SigningRequestKind.Job));
        Assert.Equal(job.Id, (await db.PatchDeploymentTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).JobId);
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == Core.Interfaces.AuditActions.PatchRoundStarted && a.TargetId == round.Id.ToString()));

        // A pending endpoint of a round without its job yet is not "gone" to the follower; with its job it follows the job.
        await _fixture.FleetoDeployments().FollowAsync(CancellationToken.None);
        Assert.Equal(PatchDeploymentTargetState.Pending, (await db.PatchDeploymentTargets.AsNoTracking().SingleAsync(t => t.Id == target.Id)).State);
    }

    [Fact]
    public async Task An_endpoint_whose_agent_cannot_install_or_whose_scan_found_nothing_is_answered_at_once()
    {
        var (old, oldPolicy) = await ScopeAsync(agentVersion: "0.8.0");
        var (clean, cleanPolicy) = await ScopeAsync();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = clean.Id, ClientId = clean.ClientId, Engine = PatchEngine.Fleeto, ProductLastSeenAt = _fixture.Now, UpdatedAt = _fixture.Now
            });
            await db.SaveChangesAsync();
        }

        var rounds = _fixture.PatchRounds();
        await rounds.StartRoundsAsync(CancellationToken.None);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(2));
        await rounds.StartRoundsAsync(CancellationToken.None);

        var tooOld = Assert.Single((await RoundOfAsync(oldPolicy))!.Targets);
        Assert.Equal(PatchDeploymentTargetState.Unknown, tooOld.State);
        Assert.Contains("Update the agent", tooOld.Message);
        var nothing = Assert.Single((await RoundOfAsync(cleanPolicy))!.Targets);
        Assert.Equal((PatchDeploymentTargetState.Succeeded, PatchRounds.NothingToInstallMessage), (nothing.State, nothing.Message));

        // The round of the clean endpoint ends, and "Scheduled patching" counts from the policy.
        await _fixture.FleetoDeployments().FollowAsync(CancellationToken.None);
        Assert.Equal(PatchDeploymentState.Completed, (await RoundOfAsync(cleanPolicy))!.State);
        await rounds.TrackScheduledAsync(CancellationToken.None);
        await using var check = _fixture.Db.DbFactory.CreateSystem();
        var state = await check.EndpointPatchStates.AsNoTracking().SingleAsync(s => s.EndpointId == clean.Id);
        Assert.Equal(cleanPolicy.Id, state.ScheduledPolicyId);
        Assert.NotNull(state.ScheduledSince);
    }

    [Fact]
    public async Task A_policy_for_Action1_starts_no_round_and_the_database_refuses_a_round_job_of_it()
    {
        var (endpoint, policy) = await ScopeAsync(engine: PatchEngine.Action1);
        var rounds = _fixture.PatchRounds();
        await rounds.StartRoundsAsync(CancellationToken.None);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(2));
        await rounds.StartRoundsAsync(CancellationToken.None);
        Assert.Null(await RoundOfAsync(policy));

        // A round the workers would write for it anyway: the job is refused.
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var deployment = new PatchDeployment
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, BatchId = Guid.NewGuid(), Engine = PatchEngine.Fleeto, Scope = PatchDeploymentScope.PatchPolicy,
            State = PatchDeploymentState.Running, PatchPolicyId = policy.Id, RoundAt = _fixture.Now, RequestedByUserId = PatchRounds.AutomaticInitiator,
            RequestedByName = "Fleeto", RequestedAt = _fixture.Now
        };
        var target = new PatchDeploymentTarget { Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Hostname = endpoint.Hostname, UpdatedAt = _fixture.Now };
        deployment.Targets.Add(target);
        db.PatchDeployments.Add(deployment);
        await db.SaveChangesAsync();
        db.Jobs.Add(new Job
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = deployment.BatchId, Type = JobType.PatchInstall,
            ScriptName = PatchRules.InstallJobName, CreatedAt = _fixture.Now, ValidUntil = _fixture.Now.AddHours(1),
            InitiatedByUserId = PatchRounds.AutomaticInitiator, InitiatedByName = "Fleeto", PatchDeploymentId = deployment.Id, PatchDeploymentTargetId = target.Id
        });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("42501", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
}
