using System.Security.Cryptography;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees of following clients and sites in Action1 on the web side (0.6.0): what Action1 has to do for a new or
/// deleted client or site is recorded in the same unit of work as the change, only while the integration follows clients,
/// also for a technician limited to clients; a mapping made by hand is in step with its client; and only admins dismiss.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class IntegrationFollowServiceTests
{
    private readonly WebFixture _fixture;

    public IntegrationFollowServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private IntegrationService Integrations => _fixture.Services.GetRequiredService<IntegrationService>();
    private ClientService Clients => _fixture.Services.GetRequiredService<ClientService>();
    private SiteService Sites => _fixture.Services.GetRequiredService<SiteService>();

    private static string Code() => "F" + Convert.ToHexString(RandomNumberGenerator.GetBytes(3));

    private async Task<Guid> SetUpAsync(bool follow)
    {
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
            await db.SaveChangesAsync();
        }

        var saved = await Integrations.SaveAction1Async(WebFixture.Admin(),
            new Action1Input("api-key-follow@action1.com", "follow-secret-1", Action1Region.Europe, Enabled: true, FollowClients: follow));
        Assert.True(saved.Success);
        return (await Integrations.GetAction1Async(WebFixture.Admin()))!.Id;
    }

    private async Task<List<IntegrationOperation>> OperationsAsync(Guid integrationId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.IntegrationOperations.AsNoTracking().Where(o => o.IntegrationId == integrationId).ToListAsync();
    }

    private async Task MapAsync(Guid integrationId, Guid clientId, string tenantId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(), IntegrationId = integrationId, ClientId = clientId, ExternalTenantId = tenantId,
            ExternalTenantName = "Org " + tenantId, CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_new_client_asks_for_an_organization_only_while_Action1_follows_clients()
    {
        var off = await SetUpAsync(follow: false);
        Assert.True((await Clients.CreateAsync(WebFixture.Technician(), Code(), "Not followed", null)).Success);
        Assert.Empty(await OperationsAsync(off));

        var on = await SetUpAsync(follow: true);
        var created = await Clients.CreateAsync(WebFixture.Technician(), Code(), "Followed client", null);

        Assert.True(created.Success);
        var operation = Assert.Single(await OperationsAsync(on));
        Assert.Equal(IntegrationOperationKind.CreateTenant, operation.Kind);
        Assert.Equal(created.Value, operation.TargetClientId);
        Assert.Equal("Followed client", operation.Name);
    }

    [Fact]
    public async Task Deleting_a_mapped_client_asks_to_remove_its_organization()
    {
        var integrationId = await SetUpAsync(follow: true);
        var client = await _fixture.Database.CreateClientAsync(Code());
        await MapAsync(integrationId, client.Id, "org-delete-me");

        var deleted = await Clients.DeleteAsync(WebFixture.Technician(), client.Id, client.Code);

        Assert.True(deleted.Success);
        var operation = Assert.Single(await OperationsAsync(integrationId));
        Assert.Equal(IntegrationOperationKind.DeleteTenant, operation.Kind);
        Assert.Equal("org-delete-me", operation.ExternalTenantId);
        Assert.Null(operation.TargetClientId);
    }

    [Fact]
    public async Task Deleting_a_site_with_an_endpoint_group_asks_to_remove_the_group_also_for_a_technician_limited_to_the_client()
    {
        var integrationId = await SetUpAsync(follow: true);
        var client = await _fixture.Database.CreateClientAsync(Code());
        await _fixture.Database.CreateSiteAsync(client.Id, "Stays");
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Goes");
        await MapAsync(integrationId, client.Id, "org-sites-" + client.Code);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.IntegrationSiteGroups.Add(new IntegrationSiteGroup
            {
                Id = Guid.NewGuid(), IntegrationId = integrationId, ClientId = client.Id, SiteId = site.Id,
                ExternalTenantId = "org-sites-" + client.Code, ExternalGroupId = "group-goes", SyncedName = "Goes", CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var technician = WebFixture.CallerWith(new RestrictedClientScope([client.Id]), FleetoRoles.Technician);
        Assert.True((await Sites.DeleteAsync(technician, site.Id)).Success);

        var operation = Assert.Single(await OperationsAsync(integrationId));
        Assert.Equal(IntegrationOperationKind.DeleteGroup, operation.Kind);
        Assert.Equal("group-goes", operation.ExternalGroupId);
        await using var read = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await read.IntegrationSiteGroups.AnyAsync(g => g.SiteId == site.Id));
    }

    [Fact]
    public async Task A_mapping_made_by_hand_is_in_step_with_its_client_and_the_switch_is_kept()
    {
        await SetUpAsync(follow: true);
        var client = await _fixture.Database.CreateClientAsync(Code());

        Assert.True((await Integrations.SaveMappingAsync(WebFixture.Admin(), client.Id, "org-by-hand", "Another name")).Success);

        var view = await Integrations.GetAction1Async(WebFixture.Admin());
        Assert.True(view!.FollowClients);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(client.Name, (await db.IntegrationMappings.AsNoTracking().SingleAsync(m => m.ClientId == client.Id)).SyncedName);
    }

    [Fact]
    public async Task Only_admins_dismiss_a_change_that_waits_for_Action1()
    {
        var integrationId = await SetUpAsync(follow: true);
        Assert.True((await Clients.CreateAsync(WebFixture.Technician(), Code(), "Dismissed", null)).Success);
        var operation = Assert.Single(await OperationsAsync(integrationId));

        Assert.False((await Integrations.DismissOperationAsync(WebFixture.Technician(), operation.Id)).Success);
        Assert.Single((await Integrations.GetAction1Async(WebFixture.Admin()))!.Operations!);

        Assert.True((await Integrations.DismissOperationAsync(WebFixture.Admin(), operation.Id)).Success);
        Assert.Empty(await OperationsAsync(integrationId));
    }
}
