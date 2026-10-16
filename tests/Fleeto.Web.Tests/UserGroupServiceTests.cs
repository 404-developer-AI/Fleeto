using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Groups of users in web (0.6.0): admins manage them, a user put in another group leaves the default one, a change ends the
/// remote session connections the user lost and reloads its pages, and part of a client never changes the client as a whole.
/// </summary>
[Collection(WebCollection.Name)]
public class UserGroupServiceTests
{
    private readonly WebFixture _fixture;

    public UserGroupServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private UserGroupService Groups => _fixture.Services.GetRequiredService<UserGroupService>();

    private async Task<Caller> CallerOfAsync(Guid userId, string role)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var scope = await UserAccessResolver.ScopeOfAsync(db, userId, CancellationToken.None);
        return new Caller(userId, "Tess Tech", "tess@example.com", [role], scope, "127.0.0.1");
    }

    [Fact]
    public async Task Only_admins_manage_groups_and_a_group_is_checked_before_it_is_saved()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var admin = WebFixtureBase.Admin();
        var adminUser = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        var input = new UserGroupInput("Group " + Guid.NewGuid().ToString("N")[..6], null, false,
            [new AccessTargetRef(AccessTargetKind.Client, client.Id)], [], []);

        Assert.False((await Groups.SaveAsync(WebFixtureBase.Technician(), null, input)).Success);
        Assert.Contains("includes every client", (await Groups.SaveAsync(admin, null, input with { AllClients = true })).Problem);
        Assert.Contains("both included and excluded", (await Groups.SaveAsync(admin, null, input with { Excludes = input.Includes })).Problem);
        Assert.Contains("Admins see every client", (await Groups.SaveAsync(admin, null, input with { MemberIds = [adminUser.Id] })).Problem);
        Assert.Contains("no longer exists",
            (await Groups.SaveAsync(admin, null, input with { Includes = [new AccessTargetRef(AccessTargetKind.Site, Guid.NewGuid())] })).Problem);

        var saved = await Groups.SaveAsync(admin, null, input);
        Assert.True(saved.Success, saved.Problem);
        Assert.Contains("already exists", (await Groups.SaveAsync(admin, null, input with { Name = input.Name!.ToUpperInvariant() })).Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.UserGroupCreated && a.TargetId == saved.Value.ToString()));
    }

    [Fact]
    public async Task A_user_put_in_another_group_leaves_the_default_group_and_sees_what_the_group_gives()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Office");
        var other = await _fixture.Database.CreateSiteAsync(client.Id, "Warehouse");
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, hostname: "GRP-OFFICE");
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var admin = WebFixtureBase.Admin();

        var saved = await Groups.SaveAsync(admin, null, new UserGroupInput("Office " + Guid.NewGuid().ToString("N")[..6], "Only the office", false,
            [new AccessTargetRef(AccessTargetKind.Site, site.Id)], [], [technician.Id]));
        Assert.True(saved.Success, saved.Problem);

        var groups = await Groups.GroupsOfAsync(admin, technician.Id);
        Assert.Equal([saved.Value], groups);

        var access = await Groups.GetUserAccessAsync(admin, technician.Id);
        Assert.NotNull(access);
        Assert.False(access.Everything);
        var seen = Assert.Single(access.Clients, c => c.Id == client.Id);
        Assert.Equal(AccessLevel.Partial, seen.Level);
        var seenSite = Assert.Single(seen.Sites);
        Assert.Equal(site.Id, seenSite.Id);
        Assert.Equal(AccessLevel.Full, seenSite.Level);

        var holders = await Groups.WhoCanReachAsync(admin, AccessTargetKind.Endpoint, endpoint.Id);
        Assert.Contains(holders!, h => h.UserId == technician.Id && h.Level == AccessLevel.Full);
        var otherHolders = await Groups.WhoCanReachAsync(admin, AccessTargetKind.Site, other.Id);
        Assert.DoesNotContain(otherHolders!, h => h.UserId == technician.Id);
        Assert.Contains(_fixture.Database.Bus.PayloadsFor(NotificationChannels.UserAccess), p => p == technician.Id.ToString());
    }

    [Fact]
    public async Task Losing_an_endpoint_revokes_the_open_remote_session_connections_to_it()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "GRP-REMOTE");
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        var groupId = await _fixture.Database.CreateGroupAsync(technician.Id, siteId: site.Id);
        Guid participantId;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var now = DateTime.UtcNow;
            var session = new RemoteSession
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Kind = RemoteSessionKind.RemoteBackground,
                Component = AgentComponent.Watchdog, StartedByUserId = technician.Id, StartedByName = "Tess", CreatedAt = now
            };
            var participant = new RemoteSessionParticipant
            {
                Id = Guid.NewGuid(), SessionId = session.Id, ClientId = client.Id, EndpointId = endpoint.Id, UserId = technician.Id, UserName = "Tess",
                BrowserPublicKey = new byte[32], State = RemoteParticipantState.Connected, CreatedAt = now
            };
            participantId = participant.Id;
            db.RemoteSessions.Add(session);
            db.RemoteSessionParticipants.Add(participant);
            await db.SaveChangesAsync();
        }

        var detail = await Groups.GetAsync(WebFixtureBase.Admin(), groupId);
        var changed = await Groups.SaveAsync(WebFixtureBase.Admin(), groupId, new UserGroupInput(detail!.Name, null, false,
            [], [], [technician.Id]));
        Assert.True(changed.Success, changed.Problem);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.NotNull((await db.RemoteSessionParticipants.SingleAsync(p => p.Id == participantId)).RevokedAt);
        }
    }

    [Fact]
    public async Task Part_of_a_client_changes_only_its_endpoints_and_a_client_needs_a_group_that_grows()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Part");
        await _fixture.Database.CreateSiteAsync(client.Id, "Rest");
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        await _fixture.Database.CreateGroupAsync(technician.Id, siteId: site.Id);
        var caller = await CallerOfAsync(technician.Id, FleetoRoles.Technician);
        var clients = _fixture.Services.GetRequiredService<ClientService>();
        var sites = _fixture.Services.GetRequiredService<SiteService>();
        var maintenance = _fixture.Services.GetRequiredService<MaintenanceService>();

        Assert.Equal(ServiceResult.WholeClientProblem, (await clients.RenameAsync(caller, client.Id, "Renamed")).Problem);
        Assert.Equal(ServiceResult.WholeClientProblem, (await sites.CreateAsync(caller, client.Id, "Extra", null)).Problem);
        Assert.Equal(ServiceResult.WholeClientProblem,
            (await maintenance.StartAsync(caller, MaintenanceTarget.Client, client.Id, null, "Planned work")).Problem);
        Assert.True((await sites.UpdateAsync(caller, site.Id, "Part renamed", null)).Success);
        Assert.Equal(ServiceResult.NewClientProblem, (await clients.CreateAsync(caller, "N" + Guid.NewGuid().ToString("N")[..6], "New", null)).Problem);

        // A group for every client grows with new clients, so its members create them, and see them whole.
        var grower = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var now = DateTime.UtcNow;
            var group = new UserGroup
            {
                Id = Guid.NewGuid(), Name = "Except " + Guid.NewGuid().ToString("N")[..6], AllClients = true, CreatedAt = now, UpdatedAt = now
            };
            group.NormalizedName = group.Name.ToUpperInvariant();
            db.UserGroups.Add(group);
            db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = grower.Id, AddedAt = now });
            db.UserGroupRules.Add(new UserGroupRule { Id = Guid.NewGuid(), GroupId = group.Id, Kind = UserGroupRuleKind.Exclude, ClientId = client.Id });
            await db.SaveChangesAsync();
        }

        var growing = await CallerOfAsync(grower.Id, FleetoRoles.Technician);
        Assert.False(growing.Scope.AllClients);
        var created = await clients.CreateAsync(growing, "G" + Guid.NewGuid().ToString("N")[..6], "Grown", null);
        Assert.True(created.Success, created.Problem);
        Assert.True((await CallerOfAsync(grower.Id, FleetoRoles.Technician)).HasWholeClient(created.Value));
    }

    [Fact]
    public async Task A_shared_policy_changes_only_with_access_to_everything_it_is_used_on()
    {
        var clientA = await _fixture.Database.CreateClientAsync();
        var clientB = await _fixture.Database.CreateClientAsync();
        var policies = _fixture.Services.GetRequiredService<PolicyService>();
        var input = new PolicyInput("Shared " + Guid.NewGuid().ToString("N")[..6], null, 60, 3600, 10, AlertSeverity.Critical);
        var created = await policies.CreateAsync(WebFixtureBase.Admin(), null, input);
        Assert.True(created.Success, created.Problem);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var now = DateTime.UtcNow;
            db.ClientPolicies.Add(new ClientPolicy { ClientId = clientA.Id, PolicyId = created.Value, CreatedAt = now });
            db.ClientPolicies.Add(new ClientPolicy { ClientId = clientB.Id, PolicyId = created.Value, CreatedAt = now });
            await db.SaveChangesAsync();
        }

        var onlyA = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        await _fixture.Database.CreateGroupAsync(onlyA.Id, clientId: clientA.Id);
        var refused = await policies.UpdateAsync(await CallerOfAsync(onlyA.Id, FleetoRoles.Technician), created.Value, input with { HeartbeatIntervalSeconds = 120 });
        Assert.Contains("also used where your groups give you no access", refused.Problem);

        await _fixture.Database.CreateGroupAsync(onlyA.Id, clientId: clientB.Id);
        var allowed = await policies.UpdateAsync(await CallerOfAsync(onlyA.Id, FleetoRoles.Technician), created.Value, input with { HeartbeatIntervalSeconds = 120 });
        Assert.True(allowed.Success, allowed.Problem);

        // The default policy applies to every endpoint without a policy: only somebody who sees every client changes it.
        await using var system = _fixture.Database.DbFactory.CreateSystem();
        var defaultPolicy = await system.Policies.SingleAsync(p => p.IsDefault);
        var refusedDefault = await policies.UpdateAsync(await CallerOfAsync(onlyA.Id, FleetoRoles.Technician), defaultPolicy.Id, input with { Name = defaultPolicy.Name });
        Assert.Equal(TemplateAccess.DefaultPolicyProblem, refusedDefault.Problem);
    }
}
