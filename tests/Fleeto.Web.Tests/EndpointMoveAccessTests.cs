using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Moving an endpoint to another site of its client changes who reaches it when a group gives one site and not the other: a user
/// that loses it has its remote session connections to it ended, and the open pages of the users concerned reload, as after a
/// change to a group.
/// </summary>
[Collection(WebCollection.Name)]
public class EndpointMoveAccessTests
{
    private readonly WebFixture _fixture;

    public EndpointMoveAccessTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed record Setup(Client Client, Site Old, Site New, Endpoint Endpoint, Guid TechnicianId, Guid GroupId, Guid ParticipantId);

    /// <summary>A technician whose only group includes the old site, connected to an endpoint on it.</summary>
    private async Task<Setup> ArrangeAsync(string hostname)
    {
        var client = await _fixture.Database.CreateClientAsync();
        var old = await _fixture.Database.CreateSiteAsync(client.Id, "Office");
        var target = await _fixture.Database.CreateSiteAsync(client.Id, "Warehouse");
        var endpoint = await _fixture.Database.CreateEndpointAsync(old, EndpointTier.Managed, hostname);
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        var groupId = await _fixture.Database.CreateGroupAsync(technician.Id, siteId: old.Id);
        var participantId = await ConnectedParticipants.AddAsync(_fixture.Database, endpoint, technician.Id);
        return new Setup(client, old, target, endpoint, technician.Id, groupId, participantId);
    }

    [Fact]
    public async Task Losing_the_site_through_a_group_change_ends_the_connection()
    {
        var setup = await ArrangeAsync("MOVE-GROUP");
        var groups = _fixture.Services.GetRequiredService<UserGroupService>();
        var detail = await groups.GetAsync(WebFixtureBase.Admin(), setup.GroupId);
        var changed = await groups.SaveAsync(WebFixtureBase.Admin(), setup.GroupId,
            new UserGroupInput(detail!.Name, null, false, [new AccessTargetRef(AccessTargetKind.Site, setup.New.Id)], [], [setup.TechnicianId]));
        Assert.True(changed.Success, changed.Problem);

        Assert.True(await ConnectedParticipants.GatewayClosesAsync(_fixture.Database, setup.ParticipantId));
    }

    [Fact]
    public async Task Moving_the_endpoint_away_from_the_site_of_a_user_ends_its_connection_and_reloads_its_pages()
    {
        var setup = await ArrangeAsync("MOVE-AWAY");
        var before = ConnectedParticipants.AccessNotifications(_fixture.Database, setup.TechnicianId);

        var moved = await _fixture.Services.GetRequiredService<EndpointService>()
            .MoveAsync(WebFixtureBase.Admin(), setup.Endpoint.Id, setup.New.Id);
        Assert.True(moved.Success, moved.Problem);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.False(await UserAccessResolver.MayReachAsync(db, setup.TechnicianId, setup.Client.Id, setup.New.Id, setup.Endpoint.Id,
                CancellationToken.None));
        }

        Assert.NotNull((await ConnectedParticipants.GetAsync(_fixture.Database, setup.ParticipantId)).RevokedAt);
        Assert.True(await ConnectedParticipants.GatewayClosesAsync(_fixture.Database, setup.ParticipantId));
        // Open pages keep a scope snapshot until they reload; the notification makes them.
        Assert.True(ConnectedParticipants.AccessNotifications(_fixture.Database, setup.TechnicianId) > before);
    }

    [Fact]
    public async Task Moving_the_endpoint_keeps_the_connection_of_a_user_that_still_reaches_it()
    {
        var setup = await ArrangeAsync("MOVE-KEEP");
        // A second group of the technician gives the new site too.
        await _fixture.Database.CreateGroupAsync(setup.TechnicianId, siteId: setup.New.Id);
        var before = ConnectedParticipants.AccessNotifications(_fixture.Database, setup.TechnicianId);

        var moved = await _fixture.Services.GetRequiredService<EndpointService>()
            .MoveAsync(WebFixtureBase.Admin(), setup.Endpoint.Id, setup.New.Id);
        Assert.True(moved.Success, moved.Problem);

        Assert.Null((await ConnectedParticipants.GetAsync(_fixture.Database, setup.ParticipantId)).RevokedAt);
        Assert.True(ConnectedParticipants.AccessNotifications(_fixture.Database, setup.TechnicianId) > before);
    }
}
