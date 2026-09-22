using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;
using JobType = Fleeto.Core.Entities.JobType;
using ScriptLanguage = Fleeto.Core.Entities.ScriptLanguage;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees the one job whose script nobody writes (0.4.0 step 3): installing the Action1 agent. The signer composes
/// the body itself from the installer link of the client's organization, so web can name the endpoint but never what
/// runs on it. Without a link, with a link that is not an Action1 download, on Linux or on an endpoint that is not
/// managed, nothing is signed.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class Action1AgentJobTests
{
    private const string InstallerUrl = "https://app.eu.action1.com/agent/9f2c/Windows/agent(Contoso).msi";

    private readonly SignerFixture _fixture;

    public Action1AgentJobTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Client Client, Endpoint Endpoint, Guid TechnicianId)> ScopeAsync(string? installerUrl = InstallerUrl,
        EndpointTier tier = EndpointTier.Managed, string osPlatform = "windows")
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, tier, "WS-A1");
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, displayName: "Tess Tech");

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        if (osPlatform != "windows")
        {
            var row = await db.Endpoints.SingleAsync(e => e.Id == endpoint.Id);
            row.OsPlatform = osPlatform;
        }

        var integration = await db.Integrations.FirstOrDefaultAsync(i => i.Type == IntegrationType.Action1);
        if (integration is null)
        {
            integration = new Integration
            {
                Id = Guid.NewGuid(), Type = IntegrationType.Action1, Region = Action1Region.Europe, EncryptedCredentials = "x",
                CredentialName = "api-key@action1.com", CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
            };
            db.Integrations.Add(integration);
        }

        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(),
            IntegrationId = integration.Id,
            ClientId = client.Id,
            ExternalTenantId = "org-" + client.Code,
            ExternalTenantName = client.Code,
            AgentInstallerUrl = installerUrl ?? string.Empty,
            CreatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
        return (client, endpoint, technician.Id);
    }

    private async Task<Job> CreateJobAsync(Client client, Endpoint endpoint, Guid technicianId, string? installerUrl = InstallerUrl)
    {
        // Web binds the job to the body the link gives at that moment, exactly as PatchService does.
        var scriptSha256 = Action1AgentInstall.IsValidInstallerUrl(installerUrl)
            ? ScriptLanguages.Sha256(Action1AgentInstall.Body(installerUrl!))
            : string.Empty;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var job = new Job
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            EndpointId = endpoint.Id,
            BatchId = Guid.NewGuid(),
            Type = JobType.Action1Agent,
            ScriptName = Action1AgentInstall.JobName,
            Language = ScriptLanguage.PowerShell,
            ScriptSha256 = scriptSha256,
            TimeoutSeconds = Action1AgentInstall.TimeoutSeconds,
            MaxOutputBytes = Action1AgentInstall.MaxOutputBytes,
            CreatedAt = _fixture.Now,
            ValidUntil = _fixture.Now.AddHours(24),
            InitiatedByUserId = technicianId,
            InitiatedByName = "Tess Tech",
            State = JobState.PendingSignature
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
    public async Task The_signer_writes_the_installer_job_itself_from_the_link_of_the_client()
    {
        var (client, endpoint, technicianId) = await ScopeAsync();
        var job = await CreateJobAsync(client, endpoint, technicianId);

        var (request, signed) = await SignAsync(job);

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.Equal(JobState.Queued, signed.State);
        Assert.True(Ed25519.Verify(_fixture.KeyRing.SigningPublicKey, SignatureContexts.Job, signed.Payload, signed.Signature!));

        var payload = JobPayload.Parser.ParseFrom(signed.Payload);
        Assert.Equal(endpoint.Id.ToString("D"), payload.EndpointId);
        Assert.Equal(Protocol.Agent.V1.ScriptLanguage.Powershell, payload.Script.Language);
        Assert.Equal(Action1AgentInstall.JobName, payload.Script.Name);
        Assert.Contains(InstallerUrl, payload.Script.Body);
        Assert.Contains("msiexec.exe", payload.Script.Body);
        // Nothing restarts: a patch deployment decides that, not the installation of the agent.
        Assert.Contains("/norestart", payload.Script.Body);
        Assert.Equal(Protocol.Agent.V1.JobRunAs.Service, payload.RunAs);
        Assert.Equal((uint)Action1AgentInstall.TimeoutSeconds, payload.TimeoutSeconds);
        // What web asked for and what the signer wrote are the same body: the hash binds the two.
        Assert.Equal(payload.Script.Sha256, signed.ScriptSha256);
        Assert.Equal(ScriptLanguages.Sha256(payload.Script.Body), payload.Script.Sha256);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.JobSigned && a.TargetId == job.Id.ToString());
        // The link names the customer's organization, so only its host is written down.
        Assert.Contains("app.eu.action1.com", audit.DetailsJson);
        Assert.DoesNotContain("/agent/9f2c/", audit.DetailsJson);
    }

    [Fact]
    public async Task Without_an_installer_link_for_the_client_nothing_is_signed()
    {
        var (client, endpoint, technicianId) = await ScopeAsync(installerUrl: null);
        var job = await CreateJobAsync(client, endpoint, technicianId);

        var (_, refused) = await SignAsync(job);

        Assert.Equal(JobState.Refused, refused.State);
        Assert.Equal(JobHandler.AgentInstallerReason, refused.RefusalReason);
        Assert.Null(refused.Payload);
    }

    [Fact]
    public async Task A_link_that_is_not_an_Action1_download_is_refused_by_the_signer()
    {
        // An admin can paste anything in Settings; the signer is the one that decides what ends up in a job.
        var (client, endpoint, technicianId) = await ScopeAsync(installerUrl: "https://example.test/evil.msi");
        var job = await CreateJobAsync(client, endpoint, technicianId);

        var (_, refused) = await SignAsync(job);

        Assert.Equal(JobState.Refused, refused.State);
        Assert.Equal(JobHandler.AgentInstallerReason, refused.RefusalReason);
    }

    [Fact]
    public async Task A_link_that_changed_after_the_technician_asked_is_refused()
    {
        var (client, endpoint, technicianId) = await ScopeAsync();
        var job = await CreateJobAsync(client, endpoint, technicianId);

        // An admin changes the organization's installer link between the request and the signature: what would run is no
        // longer what the technician chose.
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var mapping = await db.IntegrationMappings.SingleAsync(m => m.ClientId == client.Id);
            mapping.AgentInstallerUrl = "https://app.eu.action1.com/agent/other/Windows/agent.msi";
            await db.SaveChangesAsync();
        }

        var (_, refused) = await SignAsync(job);

        Assert.Equal(JobState.Refused, refused.State);
        Assert.Equal(JobHandler.AgentInstallerChangedReason, refused.RefusalReason);
        Assert.Null(refused.Payload);
    }

    [Fact]
    public async Task A_Linux_endpoint_is_refused_until_Linux_patch_management_exists()
    {
        var (client, endpoint, technicianId) = await ScopeAsync(osPlatform: "linux");
        var job = await CreateJobAsync(client, endpoint, technicianId);

        var (_, refused) = await SignAsync(job);

        Assert.Equal(JobState.Refused, refused.State);
        Assert.Equal(JobHandler.AgentPlatformReason, refused.RefusalReason);
    }

    [Fact]
    public async Task An_agent_only_endpoint_is_refused_like_every_other_job()
    {
        var (client, endpoint, technicianId) = await ScopeAsync(tier: EndpointTier.AgentOnly);
        var job = await CreateJobAsync(client, endpoint, technicianId);

        var (_, refused) = await SignAsync(job);

        Assert.Equal(JobState.Refused, refused.State);
        Assert.Equal(JobHandler.NotManagedReason, refused.RefusalReason);
    }
}
