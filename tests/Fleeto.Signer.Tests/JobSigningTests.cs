using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;
using JobType = Fleeto.Core.Entities.JobType;
using JobRunAs = Fleeto.Core.Entities.JobRunAs;
using ScriptLanguage = Fleeto.Core.Entities.ScriptLanguage;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees the signer's job rules (0.2.0): a valid job is signed over the body the signer read, for this instance and endpoint
/// only, and queued; a read-only or locked-out initiator, an agent-only endpoint, a passed or too long validity, a changed script,
/// another client's script, the wrong platform and an unapproved or outdated version under an approval policy are refused, and
/// the refusal is recorded on the job; a cancelled job is never signed.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class JobSigningTests
{
    private readonly SignerFixture _fixture;

    public JobSigningTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Scope(Client Client, Site Site, Endpoint Endpoint, Guid TechnicianId, Guid AdminId);

    private async Task<Scope> ScopeAsync(EndpointTier tier = EndpointTier.Managed, bool approvalRequired = false, long? maxOutputBytes = null)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, tier, "SRV-JOB", EndpointClass.Server);
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, displayName: "Tess Tech");
        var admin = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        if (approvalRequired || maxOutputBytes is not null)
        {
            await using var db = _fixture.Database.DbFactory.CreateSystem();
            var policy = new Policy
            {
                Id = Guid.NewGuid(), ClientId = client.Id, Name = "Approval " + Guid.NewGuid().ToString("N")[..6], ScriptApprovalRequired = approvalRequired,
                MaxOutputBytes = maxOutputBytes ?? ScriptRules.DefaultMaxOutputBytes,
                CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
            };
            db.Policies.Add(policy);
            db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policy.Id, CreatedAt = _fixture.Now });
            await db.SaveChangesAsync();
        }

        return new Scope(client, site, endpoint, technician.Id, admin.Id);
    }

    private async Task<Job> CreateJobAsync(Scope scope, ScriptVersion version, Script script, Guid? initiatorId = null, TimeSpan? validity = null,
        string? sha = null, JobState state = JobState.PendingSignature, JobRunAs runAs = JobRunAs.Service)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = scope.Client.Id, EndpointId = scope.Endpoint.Id, BatchId = Guid.NewGuid(), Type = JobType.Script,
            ScriptId = script.Id, ScriptVersionId = version.Id, ScriptName = script.Name, ScriptVersionNumber = version.Number, Language = script.Language,
            ScriptSha256 = sha ?? version.Sha256, TimeoutSeconds = version.TimeoutSeconds, MaxOutputBytes = ScriptRules.DefaultMaxOutputBytes,
            CreatedAt = _fixture.Now, ValidUntil = _fixture.Now + (validity ?? TimeSpan.FromHours(24)), InitiatedByUserId = initiatorId ?? scope.TechnicianId,
            InitiatedByName = "Tess Tech", State = state, RunAs = runAs
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<(SigningRequest Request, Job Job)> SignAsync(Job job)
    {
        var request = await _fixture.ProcessAsync(SigningRequestKind.Job, job.ClientId, job.Id, [], "web:198.51.100.40");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return (request, await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == job.Id));
    }

    [Fact]
    public async Task A_valid_job_is_signed_for_its_endpoint_and_queued()
    {
        var scope = await ScopeAsync();
        var (script, version) = await _fixture.Database.CreateScriptAsync(null, scope.TechnicianId, body: "Get-Service Spooler");
        var job = await CreateJobAsync(scope, version, script);

        var (request, signed) = await SignAsync(job);

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.Equal(JobState.Queued, signed.State);
        Assert.True(Ed25519.Verify(_fixture.KeyRing.SigningPublicKey, SignatureContexts.Job, signed.Payload, signed.Signature!));
        var payload = JobPayload.Parser.ParseFrom(signed.Payload);
        Assert.Equal(job.Id.ToString("D"), payload.JobId);
        Assert.Equal(scope.Endpoint.Id.ToString("D"), payload.EndpointId);
        Assert.Equal(_fixture.KeyRing.InstanceId.ToString("D"), payload.InstanceId);
        Assert.Equal("Get-Service Spooler", payload.Script.Body);
        Assert.Equal(version.Sha256, payload.Script.Sha256);
        Assert.Equal(Protocol.Agent.V1.ScriptLanguage.Powershell, payload.Script.Language);
        Assert.Equal(job.ValidUntil, payload.ValidUntil.ToDateTime(), TimeSpan.FromSeconds(1));
        Assert.Equal((ulong)ScriptRules.DefaultMaxOutputBytes, payload.MaxOutputBytes);
        Assert.Equal(Protocol.Agent.V1.JobRunAs.Service, payload.RunAs);
        Assert.Contains(scope.Endpoint.Id.ToString(), _fixture.Database.Bus.PayloadsFor(NotificationChannels.Jobs));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.JobSigned && a.TargetId == job.Id.ToString()));
    }

    [Fact]
    public async Task A_job_for_the_signed_in_user_carries_that_choice_into_the_signature()
    {
        var scope = await ScopeAsync();
        var (script, version) = await _fixture.Database.CreateScriptAsync(null, scope.TechnicianId);
        var job = await CreateJobAsync(scope, version, script, runAs: JobRunAs.LoggedOnUser);

        var (_, signed) = await SignAsync(job);

        Assert.Equal(JobState.Queued, signed.State);
        Assert.Equal(Protocol.Agent.V1.JobRunAs.LoggedOnUser, JobPayload.Parser.ParseFrom(signed.Payload).RunAs);
    }

    [Fact]
    public async Task The_signed_output_cap_comes_from_the_policy_and_never_from_the_job_row()
    {
        var scope = await ScopeAsync(maxOutputBytes: 8 * ScriptRules.Mebibyte);
        var (script, version) = await _fixture.Database.CreateScriptAsync(null, scope.TechnicianId);
        var job = await CreateJobAsync(scope, version, script);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            // Web wrote a cap of its own; only the signer's reading of the policy may end up in the signature.
            await db.Jobs.Where(j => j.Id == job.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.MaxOutputBytes, ScriptRules.MaxOutputBytes));
        }

        var (_, signed) = await SignAsync(job);

        Assert.Equal(8 * ScriptRules.Mebibyte, signed.MaxOutputBytes);
        Assert.Equal((ulong)(8 * ScriptRules.Mebibyte), JobPayload.Parser.ParseFrom(signed.Payload).MaxOutputBytes);
    }

    [Fact]
    public async Task Jobs_that_break_a_rule_are_refused_and_the_refusal_is_recorded_on_the_job()
    {
        var scope = await ScopeAsync();
        var (script, version) = await _fixture.Database.CreateScriptAsync(null, scope.TechnicianId);
        var readOnly = await _fixture.Database.CreateUserAsync(FleetoRoles.ReadOnly);
        var lockedOut = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, lockoutEnd: DateTimeOffset.UtcNow.AddYears(1));
        var noTwoFactor = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin, twoFactor: false);

        foreach (var initiator in new[] { readOnly.Id, lockedOut.Id, noTwoFactor.Id, Guid.NewGuid() })
        {
            var (_, refused) = await SignAsync(await CreateJobAsync(scope, version, script, initiatorId: initiator));
            Assert.Equal(JobState.Refused, refused.State);
            Assert.Equal(JobHandler.InitiatorReason, refused.RefusalReason);
            Assert.Null(refused.Signature);
        }

        var (_, changed) = await SignAsync(await CreateJobAsync(scope, version, script, sha: new string('a', 64)));
        Assert.Equal(JobHandler.ScriptChangedReason, changed.RefusalReason);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var past = _fixture.Now.AddHours(-1);
            var job = await CreateJobAsync(scope, version, script, validity: TimeSpan.FromMinutes(1));
            await db.Jobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s.SetProperty(j => j.ValidUntil, _fixture.Now).SetProperty(j => j.CreatedAt, past));
            var (_, expired) = await SignAsync(job);
            Assert.Equal(JobHandler.ValidityReason, expired.RefusalReason);
        }

        var (linuxScript, linuxVersion) = await _fixture.Database.CreateScriptAsync(null, scope.TechnicianId, ScriptLanguage.Bash, "echo hi");
        var (_, platform) = await SignAsync(await CreateJobAsync(scope, linuxVersion, linuxScript));
        Assert.Equal(JobHandler.PlatformReason, platform.RefusalReason);

        var agentOnly = await ScopeAsync(EndpointTier.AgentOnly);
        var (_, notManaged) = await SignAsync(await CreateJobAsync(agentOnly, version, script));
        Assert.Equal(JobHandler.NotManagedReason, notManaged.RefusalReason);

        var cancelled = await CreateJobAsync(scope, version, script, state: JobState.Cancelled);
        var (cancelRequest, stillCancelled) = await SignAsync(cancelled);
        Assert.Equal(SigningRequestState.Completed, cancelRequest.State);
        Assert.Equal(JobState.Cancelled, stillCancelled.State);
        Assert.Null(stillCancelled.Signature);
    }

    [Fact]
    public async Task Under_an_approval_policy_only_the_approved_current_version_is_signed()
    {
        var scope = await ScopeAsync(approvalRequired: true);
        var (unapprovedScript, unapproved) = await _fixture.Database.CreateScriptAsync(scope.Client.Id, scope.TechnicianId);
        var (_, refused) = await SignAsync(await CreateJobAsync(scope, unapproved, unapprovedScript));
        Assert.Equal(JobHandler.ApprovalReason, refused.RefusalReason);

        var (script, approved) = await _fixture.Database.CreateScriptAsync(scope.Client.Id, scope.TechnicianId, approverId: scope.AdminId);
        var (_, signed) = await SignAsync(await CreateJobAsync(scope, approved, script));
        Assert.Equal(JobState.Queued, signed.State);

        // A newer version becomes current: the approved older one no longer runs on this site.
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var newer = new ScriptVersion
            {
                Id = Guid.NewGuid(), ScriptId = script.Id, ClientId = script.ClientId, Number = 2, Body = "Write-Output 'v2'",
                Sha256 = Core.Domain.ScriptLanguages.Sha256("Write-Output 'v2'"), TimeoutSeconds = 600, AuthorUserId = scope.TechnicianId, AuthorName = "Author",
                CreatedAt = _fixture.Now
            };
            db.ScriptVersions.Add(newer);
            await db.SaveChangesAsync();
            await db.Scripts.Where(s => s.Id == script.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.CurrentVersionId, newer.Id));
        }

        var (_, outdated) = await SignAsync(await CreateJobAsync(scope, approved, script));
        Assert.Equal(JobHandler.ApprovalReason, outdated.RefusalReason);

        // Another client's script is refused even where no approval is required.
        var other = await ScopeAsync();
        var (foreignScript, foreignVersion) = await _fixture.Database.CreateScriptAsync(scope.Client.Id, scope.TechnicianId);
        await Assert.ThrowsAnyAsync<Exception>(() => CreateJobAsync(other, foreignVersion, foreignScript));
    }
}
