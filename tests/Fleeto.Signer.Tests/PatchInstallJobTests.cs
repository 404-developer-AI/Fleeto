using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using JobType = Fleeto.Core.Entities.JobType;

namespace Fleeto.Signer.Tests;

/// <summary>
/// The installation of updates by Fleeto (0.9.0 step 3): the signer composes the body from the deployment the job is bound to, never from
/// what web wrote; it refuses when the endpoint is no longer Fleeto's to patch, the agent is too old, the deployment is not the user's or
/// another engine's, or none of the chosen updates is missing any more; and the database refuses a job without its binding.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class PatchInstallJobTests
{
    private readonly SignerFixture _fixture;

    public PatchInstallJobTests(SignerFixture fixture) => _fixture = fixture;

    private async Task<(Endpoint Endpoint, Guid TechnicianId)> ScopeAsync(string agentVersion = PatchRules.MinimumInstallAgentVersion,
        string action1AgentId = "")
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "LNX-PI");
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, displayName: "Tess Tech");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, "linux").SetProperty(e => e.OsName, "Ubuntu 24.04.1 LTS").SetProperty(e => e.AgentVersion, agentVersion));
        db.InventorySnapshots.Add(new InventorySnapshot
        {
            EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = _fixture.Now, Hash = "h", Action1AgentId = action1AgentId
        });
        db.EndpointMissingUpdates.Add(new EndpointMissingUpdate
        {
            Id = Guid.NewGuid(), EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ExternalUpdateId = "libssl3:amd64", Name = "libssl3",
            Version = "3.0.13-0ubuntu3.5", UpdatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
        return (await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id), technician.Id);
    }

    private async Task<Job> DeployAsync(Endpoint endpoint, Guid userId, PatchDeploymentScope scope = PatchDeploymentScope.AllMissing,
        PatchEngine engine = PatchEngine.Fleeto, bool autoReboot = false, bool securityOnly = false, bool insertJob = true, params string[] chosen)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var deployment = new PatchDeployment
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, BatchId = Guid.NewGuid(), Engine = engine, Scope = scope, AutoReboot = autoReboot,
            SecurityOnly = securityOnly, State = PatchDeploymentState.Running, RequestedByUserId = userId, RequestedByName = "Tess Tech",
            RequestedAt = _fixture.Now
        };
        var target = new PatchDeploymentTarget
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Hostname = endpoint.Hostname, UpdatedAt = _fixture.Now
        };
        deployment.Targets.Add(target);
        foreach (var id in chosen)
        {
            deployment.Updates.Add(new PatchDeploymentUpdate { Id = Guid.NewGuid(), ClientId = endpoint.ClientId, ExternalUpdateId = id, Name = id, Version = "9.9" });
        }

        db.PatchDeployments.Add(deployment);
        await db.SaveChangesAsync();
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = deployment.BatchId, Type = JobType.PatchInstall,
            ScriptName = PatchRules.InstallJobName, TimeoutSeconds = PatchRules.InstallTimeoutSeconds, MaxOutputBytes = PatchRules.InstallMaxOutputBytes,
            CreatedAt = _fixture.Now, ValidUntil = _fixture.Now + PatchRules.InstallValidity, InitiatedByUserId = userId, InitiatedByName = "Tess Tech",
            PatchDeploymentId = deployment.Id, PatchDeploymentTargetId = target.Id
        };
        if (insertJob)
        {
            db.Jobs.Add(job);
            await db.SaveChangesAsync();
        }

        return job;
    }

    private async Task<Job> SignAsync(Job job)
    {
        await _fixture.ProcessAsync(SigningRequestKind.Job, job.ClientId, job.Id, [], "web:198.51.100.40");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id);
    }

    [Fact]
    public async Task The_signer_composes_the_installation_from_the_deployment()
    {
        var (endpoint, technicianId) = await ScopeAsync();
        var signed = await SignAsync(await DeployAsync(endpoint, technicianId, autoReboot: true, securityOnly: true));

        Assert.Equal(JobState.Queued, signed.State);
        Assert.True(Ed25519.Verify(_fixture.KeyRing.SigningPublicKey, SignatureContexts.Job, signed.Payload, signed.Signature!));
        var payload = JobPayload.Parser.ParseFrom(signed.Payload);
        Assert.Equal(Protocol.Agent.V1.JobType.PatchInstall, payload.Type);
        Assert.Null(payload.Script);
        Assert.Equal(PatchInstallScope.AllMissing, payload.PatchInstall.Scope);
        Assert.True(payload.PatchInstall.Filter.SecurityOnly);
        Assert.Equal(PatchRestart.IfRequired, payload.PatchInstall.Restart);
        Assert.Equal((uint)(PatchRules.RebootTimeoutMinutes * 60), payload.PatchInstall.RestartNoticeSeconds);
        Assert.Equal((uint)PatchRules.InstallTimeoutSeconds, payload.TimeoutSeconds);
        Assert.Equal(PatchRules.InstallJobName, signed.ScriptName);
    }

    [Fact]
    public async Task Chosen_updates_that_are_no_longer_missing_are_left_out_and_none_left_is_refused()
    {
        var (endpoint, technicianId) = await ScopeAsync();
        var signed = await SignAsync(await DeployAsync(endpoint, technicianId, PatchDeploymentScope.Specified, chosen: ["libssl3:amd64", "curl:amd64"]));
        var update = Assert.Single(JobPayload.Parser.ParseFrom(signed.Payload).PatchInstall.Updates);
        Assert.Equal(("libssl3:amd64", "9.9"), (update.Id, update.Version));

        var refused = await SignAsync(await DeployAsync(endpoint, technicianId, PatchDeploymentScope.Specified, chosen: ["curl:amd64"]));
        Assert.Equal((JobState.Refused, JobHandler.PatchNothingLeftReason), (refused.State, refused.RefusalReason));
    }

    [Fact]
    public async Task An_endpoint_with_an_Action1_agent_or_an_older_agent_gets_nothing()
    {
        var (action1, technicianId) = await ScopeAsync(action1AgentId: "a1-agent");
        var refused = await SignAsync(await DeployAsync(action1, technicianId));
        Assert.Equal((JobState.Refused, JobHandler.PatchEngineReason), (refused.State, refused.RefusalReason));

        var (old, oldTechnician) = await ScopeAsync(agentVersion: "0.8.0");
        var tooOld = await SignAsync(await DeployAsync(old, oldTechnician));
        Assert.Equal(JobState.Refused, tooOld.State);
        Assert.Contains("Update the agent", tooOld.RefusalReason);
        Assert.Null(tooOld.Payload);
    }

    [Fact]
    public async Task A_user_without_the_permission_to_deploy_updates_gets_nothing_signed()
    {
        var (endpoint, _) = await ScopeAsync();
        var readOnly = await _fixture.Database.CreateUserAsync(FleetoRoles.ReadOnly, displayName: "Rita Read");
        var refused = await SignAsync(await DeployAsync(endpoint, readOnly.Id));
        Assert.Equal((JobState.Refused, JobHandler.InitiatorReason), (refused.State, refused.RefusalReason));
    }

    [Fact]
    public async Task The_database_refuses_an_installation_job_without_a_deployment_by_Fleeto_of_its_own_user()
    {
        var (endpoint, technicianId) = await ScopeAsync();
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Job Unbound(Guid? deploymentId = null, Guid? targetId = null, Guid? userId = null) => new()
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = Guid.NewGuid(), Type = JobType.PatchInstall,
            ScriptName = PatchRules.InstallJobName, CreatedAt = _fixture.Now, ValidUntil = _fixture.Now.AddHours(1),
            InitiatedByUserId = userId ?? technicianId, InitiatedByName = "Tess Tech", PatchDeploymentId = deploymentId, PatchDeploymentTargetId = targetId
        };

        await AssertRefusedAsync(db, Unbound());

        // Bound to a deployment of Action1, or one another user started.
        var action1 = await DeployAsync(endpoint, technicianId, engine: PatchEngine.Action1, insertJob: false);
        await AssertRefusedAsync(db, Unbound(action1.PatchDeploymentId, action1.PatchDeploymentTargetId));
        var other = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, displayName: "Otto Other");
        var fleeto = await DeployAsync(endpoint, technicianId);
        await AssertRefusedAsync(db, Unbound(Guid.NewGuid(), fleeto.PatchDeploymentTargetId, other.Id));

        // A second job for the same endpoint of the deployment.
        db.ChangeTracker.Clear();
        db.Jobs.Add(Unbound(fleeto.PatchDeploymentId, fleeto.PatchDeploymentTargetId));
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());

        // The binding never changes.
        db.ChangeTracker.Clear();
        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            """UPDATE "Jobs" SET "PatchDeploymentId" = NULL WHERE "Id" = @id""", new NpgsqlParameter("id", fleeto.Id)));
        Assert.Equal("42501", error.SqlState);
    }

    private static async Task AssertRefusedAsync(Infrastructure.Data.FleetoDbContext db, Job job)
    {
        db.ChangeTracker.Clear();
        db.Jobs.Add(job);
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal("42501", Assert.IsType<PostgresException>(error.InnerException).SqlState);
    }
}
