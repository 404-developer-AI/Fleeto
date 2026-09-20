using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// What Fleeto shows about patch management (0.4.0 step 2): an agent-only endpoint gets nothing, a client without an
/// Action1 mapping says so instead of showing zero, missing updates come most severe first, compliance counts only
/// endpoints the product covers, and a client-restricted caller sees nothing of another client.
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
}
