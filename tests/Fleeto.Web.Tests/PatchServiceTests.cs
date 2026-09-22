using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// What Fleeto shows about patch management (0.4.0 step 2): an agent-only endpoint gets nothing, a client without an
/// Action1 mapping says so instead of showing zero, missing updates come most severe first, compliance counts only
/// endpoints the product covers, and a client-restricted caller sees nothing of another client. Deployments (step 3):
/// only a technician or admin starts one, an endpoint that cannot take part is skipped with the reason, a run per client,
/// and chosen updates are checked against what the endpoint is really missing.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class PatchServiceTests
{
    private readonly WebFixture _fixture;

    public PatchServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private PatchService Patches => _fixture.Services.GetRequiredService<PatchService>();

    private async Task<(Client Client, Endpoint Endpoint)> SeedAsync(string code, EndpointTier tier = EndpointTier.Managed,
        bool mapped = true)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync(code);
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Site " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, tier, "PATCH-" + code);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        if (mapped)
        {
            var integration = await db.Integrations.FirstOrDefaultAsync(i => i.Type == IntegrationType.Action1);
            if (integration is null)
            {
                integration = new Integration
                {
                    Id = Guid.NewGuid(),
                    Type = IntegrationType.Action1,
                    Region = Action1Region.Europe,
                    EncryptedCredentials = "x",
                    CredentialName = "api-key@action1.com",
                    CreatedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime,
                    UpdatedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime
                };
                db.Integrations.Add(integration);
            }

            db.IntegrationMappings.Add(new IntegrationMapping
            {
                Id = Guid.NewGuid(),
                IntegrationId = integration.Id,
                ClientId = client.Id,
                ExternalTenantId = "org-" + code,
                ExternalTenantName = code,
                CreatedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime
            });
        }

        await db.SaveChangesAsync();
        return (client, endpoint);
    }

    private async Task StoreStateAsync(Endpoint endpoint, int critical, int other, PatchCoverage coverage = PatchCoverage.Active,
        params (string Name, PatchSeverity Severity)[] missing)
    {
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.EndpointPatchStates.Add(new EndpointPatchState
        {
            EndpointId = endpoint.Id,
            ClientId = endpoint.ClientId,
            ExternalEndpointId = Guid.NewGuid().ToString(),
            ExternalTenantId = "org",
            Coverage = coverage,
            MissingCritical = critical,
            MissingOther = other,
            ProductLastSeenAt = now,
            ProductAgentVersion = "2.0.33",
            UpdatedAt = now
        });
        foreach (var (name, severity) in missing)
        {
            db.EndpointMissingUpdates.Add(new EndpointMissingUpdate
            {
                Id = Guid.NewGuid(),
                EndpointId = endpoint.Id,
                ClientId = endpoint.ClientId,
                ExternalUpdateId = name,
                Name = name,
                Vendor = "Vendor",
                Version = "1.0",
                Severity = severity,
                UpdatedAt = now
            });
        }

        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task An_agent_only_endpoint_has_no_patch_state()
    {
        var (_, endpoint) = await SeedAsync("PA1" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(), EndpointTier.AgentOnly);

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);

        Assert.NotNull(view);
        Assert.False(view!.Managed);
        Assert.Null(view.State);
    }

    [Fact]
    public async Task A_client_without_an_Action1_mapping_says_so_instead_of_showing_nothing_missing()
    {
        var (_, endpoint) = await SeedAsync("PA2" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(), mapped: false);

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);

        Assert.True(view!.Managed);
        Assert.False(view.Configured);
        Assert.Null(view.State);
    }

    [Fact]
    public async Task The_missing_updates_of_an_endpoint_come_most_severe_first()
    {
        var (_, endpoint) = await SeedAsync("PA3" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        await StoreStateAsync(endpoint, critical: 1, other: 2,
            missing: [("Chrome", PatchSeverity.Important), ("Windows", PatchSeverity.Critical), ("Reader", PatchSeverity.Low)]);

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);

        Assert.NotNull(view!.State);
        Assert.False(view.State!.IsCompliant);
        Assert.Equal(["Windows", "Chrome", "Reader"], view.Missing.Select(m => m.Name));
        Assert.NotNull(view.DetailUpdatedAt);
    }

    [Fact]
    public async Task Compliance_counts_only_the_endpoints_patch_management_covers()
    {
        var code = "PA4" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var (client, first) = await SeedAsync(code);
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Second " + Guid.NewGuid().ToString("N")[..6]);
        var second = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "PATCH-2");
        var third = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "PATCH-3");
        await StoreStateAsync(first, critical: 0, other: 0);
        await StoreStateAsync(second, critical: 2, other: 0);
        // The third endpoint has no state at all: it is not counted, in neither direction.

        var compliance = await Patches.GetComplianceAsync(WebFixture.Technician(), client.Id);

        Assert.Equal(2, compliance.Covered);
        Assert.Equal(1, compliance.Compliant);
        Assert.Equal(1, compliance.MissingCritical);
        Assert.Equal(50, compliance.Percentage);

        var perSite = await Patches.GetComplianceAsync(WebFixture.Technician(), null, site.Id);
        Assert.Equal(1, perSite.Covered);
        Assert.Equal(0, perSite.Compliant);
        Assert.DoesNotContain(third.Id, new[] { first.Id, second.Id });
    }

    [Fact]
    public async Task A_client_restricted_caller_sees_no_patch_state_of_another_client()
    {
        var (mine, _) = await SeedAsync("PA5" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var (_, theirs) = await SeedAsync("PA6" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        await StoreStateAsync(theirs, critical: 3, other: 1);
        var caller = WebFixtureBase.CallerWith(new RestrictedClientScope([mine.Id]), FleetoRoles.Technician);

        Assert.Null(await Patches.GetAsync(caller, theirs.Id));
        Assert.Equal(0, (await Patches.GetComplianceAsync(caller)).Covered);
    }

    // Deployments (0.4.0 step 3). Web only writes what was asked for; the workers hand it to the product.

    [Fact]
    public async Task A_read_only_user_cannot_start_a_deployment()
    {
        var (_, endpoint) = await SeedAsync("PD1" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        await StoreStateAsync(endpoint, critical: 1, other: 0);
        var caller = WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);

        var result = await Patches.StartDeploymentAsync(caller, [endpoint.Id]);

        Assert.False(result.Success);
        Assert.Equal(ServiceResult.ForbiddenProblem, result.Problem);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Empty(await db.PatchDeploymentTargets.AsNoTracking().Where(t => t.EndpointId == endpoint.Id).ToListAsync());
    }

    [Fact]
    public async Task An_endpoint_that_cannot_take_a_deployment_is_skipped_with_the_reason()
    {
        var code = "PD2" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var (client, agentOnly) = await SeedAsync(code, EndpointTier.AgentOnly);
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Second " + Guid.NewGuid().ToString("N")[..6]);
        var unknownToProduct = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "NO-STATE");
        var compliant = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "COMPLIANT");
        var missing = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "MISSING");
        await StoreStateAsync(compliant, critical: 0, other: 0);
        await StoreStateAsync(missing, critical: 1, other: 0, missing: [("Windows", PatchSeverity.Critical)]);

        var result = await Patches.StartDeploymentAsync(WebFixture.Technician(),
            [agentOnly.Id, unknownToProduct.Id, compliant.Id, missing.Id]);

        Assert.True(result.Success);
        var run = result.Value!;
        Assert.Equal(missing.Id, Assert.Single(Assert.Single(run.Deployments).Targets).EndpointId);
        Assert.Equal(3, run.Skipped.Count);
        Assert.Contains(run.Skipped, s => s.EndpointId == agentOnly.Id && s.Problem.Contains("Not managed"));
        Assert.Contains(run.Skipped, s => s.EndpointId == unknownToProduct.Id && s.Problem.Contains("Action1 does not report"));
        Assert.Contains(run.Skipped, s => s.EndpointId == compliant.Id && s.Problem.Contains("no missing updates"));
    }

    [Fact]
    public async Task A_run_over_two_clients_becomes_one_deployment_per_client_in_one_batch_and_is_audited()
    {
        var (first, firstEndpoint) = await SeedAsync("PD3" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var (second, secondEndpoint) = await SeedAsync("PD4" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        await StoreStateAsync(firstEndpoint, critical: 2, other: 0);
        await StoreStateAsync(secondEndpoint, critical: 0, other: 1);

        var result = await Patches.StartDeploymentAsync(WebFixture.Technician(), [firstEndpoint.Id, secondEndpoint.Id],
            autoReboot: true);

        var run = result.Value!;
        Assert.Equal(2, run.Deployments.Count);
        Assert.All(run.Deployments, d => Assert.Equal(run.BatchId, d.BatchId));
        Assert.All(run.Deployments, d => Assert.Equal(PatchDeploymentState.Requested, d.State));
        Assert.Contains(run.Deployments, d => d.ClientId == first.Id);
        Assert.Contains(run.Deployments, d => d.ClientId == second.Id);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var stored = await db.PatchDeployments.AsNoTracking().Where(d => d.BatchId == run.BatchId).ToListAsync();
        Assert.All(stored, d => Assert.True(d.AutoReboot));
        // Every deployment carries the organization of its own client, so the workers can never run it in another tenant.
        Assert.All(stored, d => Assert.StartsWith("org-", d.ExternalTenantId));
        var ids = stored.Select(d => d.Id.ToString()).ToList();
        Assert.Equal(2, await db.AuditEntries.AsNoTracking()
            .CountAsync(a => a.Action == AuditActions.PatchDeploymentStarted && a.TargetType == "PatchDeployment" && ids.Contains(a.TargetId)));
    }

    [Fact]
    public async Task Chosen_updates_are_stored_with_their_version_and_only_for_one_endpoint()
    {
        var code = "PD5" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var (client, endpoint) = await SeedAsync(code);
        await StoreStateAsync(endpoint, critical: 1, other: 1,
            missing: [("Windows", PatchSeverity.Critical), ("Chrome", PatchSeverity.Important)]);
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Second " + Guid.NewGuid().ToString("N")[..6]);
        var other = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "OTHER");
        await StoreStateAsync(other, critical: 1, other: 0, missing: [("Windows", PatchSeverity.Critical)]);

        var chosen = await Patches.StartDeploymentAsync(WebFixture.Technician(), [endpoint.Id], ["Chrome"]);

        var deployment = Assert.Single(chosen.Value!.Deployments);
        Assert.Equal(PatchDeploymentScope.Specified, deployment.Scope);
        Assert.Equal(["Chrome 1.0"], deployment.UpdateNames);

        // The same choice across several endpoints is refused: the technician saw the list of one endpoint only.
        var several = await Patches.StartDeploymentAsync(WebFixture.Technician(), [endpoint.Id, other.Id], ["Windows"]);
        Assert.False(several.Success);
        Assert.Contains("one endpoint", several.Problem);

        // An update that is not missing on this endpoint is refused rather than sent on.
        var unknown = await Patches.StartDeploymentAsync(WebFixture.Technician(), [endpoint.Id], ["Reader"]);
        Assert.False(unknown.Success);
        Assert.Contains("no longer missing", unknown.Problem);
    }

    [Fact]
    public async Task Installing_the_Action1_agent_is_offered_only_with_a_link_and_starts_one_signed_job()
    {
        var code = "PD8" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
        var (client, endpoint) = await SeedAsync(code);

        // Without an installer link the endpoint says it is not covered, and nothing can be started for it.
        var before = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);
        Assert.False(before!.CanInstallAgent);
        var refused = await Patches.InstallAgentAsync(WebFixture.Technician(), endpoint.Id);
        Assert.False(refused.Success);
        Assert.Contains("Settings, Integrations", refused.Problem);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var mapping = await db.IntegrationMappings.SingleAsync(m => m.ClientId == client.Id);
            mapping.AgentInstallerUrl = "https://app.eu.action1.com/agent/9f2c/Windows/agent.msi";
            await db.SaveChangesAsync();
        }

        var view = await Patches.GetAsync(WebFixture.Technician(), endpoint.Id);
        Assert.True(view!.CanInstallAgent);

        Assert.True((await Patches.InstallAgentAsync(WebFixture.Technician(), endpoint.Id)).Success);

        await using var read = _fixture.Database.DbFactory.CreateSystem();
        var job = await read.Jobs.AsNoTracking().SingleAsync(j => j.EndpointId == endpoint.Id);
        Assert.Equal(JobType.Action1Agent, job.Type);
        Assert.Equal(JobState.PendingSignature, job.State);
        Assert.Equal(ScriptLanguage.PowerShell, job.Language);
        // Web names no script: it binds the job to the body of that link, and the signer writes it.
        Assert.Null(job.ScriptId);
        Assert.Equal(64, job.ScriptSha256.Length);
        Assert.True(await read.SigningRequests.AsNoTracking().AnyAsync(r => r.SubjectId == job.Id && r.Kind == SigningRequestKind.Job));

        // A second attempt while the first is on its way says so instead of installing twice.
        var again = await Patches.InstallAgentAsync(WebFixture.Technician(), endpoint.Id);
        Assert.False(again.Success);
        Assert.Contains("already on its way", again.Problem);
    }

    [Fact]
    public async Task A_read_only_user_cannot_install_the_Action1_agent()
    {
        var (_, endpoint) = await SeedAsync("PD9" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var caller = WebFixtureBase.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);

        var result = await Patches.InstallAgentAsync(caller, endpoint.Id);

        Assert.False(result.Success);
        Assert.Equal(ServiceResult.ForbiddenProblem, result.Problem);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Empty(await db.Jobs.AsNoTracking().Where(j => j.EndpointId == endpoint.Id).ToListAsync());
    }

    [Fact]
    public async Task A_client_restricted_caller_cannot_deploy_on_an_endpoint_of_another_client()
    {
        var (mine, _) = await SeedAsync("PD6" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var (_, theirs) = await SeedAsync("PD7" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        await StoreStateAsync(theirs, critical: 1, other: 0);
        var caller = WebFixtureBase.CallerWith(new RestrictedClientScope([mine.Id]), FleetoRoles.Technician);

        var result = await Patches.StartDeploymentAsync(caller, [theirs.Id]);

        Assert.False(result.Success);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Empty(await db.PatchDeployments.AsNoTracking().Where(d => d.ClientId == theirs.ClientId).ToListAsync());
        Assert.Empty(await Patches.ListDeploymentsAsync(caller, theirs.Id));
    }
}
