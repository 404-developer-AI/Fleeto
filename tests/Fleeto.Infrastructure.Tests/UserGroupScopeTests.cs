using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Groups of users against the database (0.6.0): a user sees what its groups give, also part of a client; what is added later
/// follows without anything kept in step; reading part of a client never shows the rest of it; and writing needs the endpoint,
/// the whole site or the whole client the row belongs to.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class UserGroupScopeTests
{
    private readonly TestDatabase _db;

    public UserGroupScopeTests(DatabaseFixture fixture) => _db = fixture.Database;

    private async Task<Guid> GroupAsync(Guid userId, bool allClients, (UserGroupRuleKind Kind, Guid? Client, Guid? Site, Guid? Endpoint)[] rules)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var now = DateTime.UtcNow;
        var group = new UserGroup
        {
            Id = Guid.NewGuid(), Name = "G " + Guid.NewGuid().ToString("N")[..8], AllClients = allClients, CreatedAt = now, UpdatedAt = now
        };
        group.NormalizedName = group.Name.ToUpperInvariant();
        db.UserGroups.Add(group);
        db.UserGroupMembers.Add(new UserGroupMember { GroupId = group.Id, UserId = userId, AddedAt = now });
        foreach (var rule in rules)
        {
            db.UserGroupRules.Add(new UserGroupRule
            {
                Id = Guid.NewGuid(), GroupId = group.Id, Kind = rule.Kind, ClientId = rule.Client, SiteId = rule.Site, EndpointId = rule.Endpoint
            });
        }

        await db.SaveChangesAsync();
        return group.Id;
    }

    [Fact]
    public async Task A_user_in_no_group_sees_nothing_and_an_admin_everything()
    {
        await _db.CreateClientAsync();
        var technician = await _db.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        var admin = await _db.CreateUserAsync(FleetoRoles.Admin);

        await using var system = _db.DbFactory.CreateSystem();
        var none = await UserAccessResolver.ScopeOfAsync(system, technician.Id, CancellationToken.None);
        Assert.False(none.AllClients);
        Assert.Empty(none.ClientIds);
        await using (var db = _db.DbFactory.Create(none))
        {
            Assert.False(await db.Clients.AnyAsync());
            Assert.False(await db.Endpoints.AnyAsync());
        }

        Assert.True((await UserAccessResolver.ScopeOfAsync(system, admin.Id, CancellationToken.None)).AllClients);
    }

    [Fact]
    public async Task Part_of_a_client_shows_only_that_part_and_grows_with_new_endpoints_of_an_included_site()
    {
        var client = await _db.CreateClientAsync();
        var included = await _db.CreateSiteAsync(client.Id, "Included");
        var other = await _db.CreateSiteAsync(client.Id, "Other");
        var visible = await _db.CreateEndpointAsync(included, hostname: "PART-IN");
        var hidden = await _db.CreateEndpointAsync(other, hostname: "PART-OUT");
        var technician = await _db.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        await GroupAsync(technician.Id, false, [(UserGroupRuleKind.Include, null, included.Id, null)]);
        await using (var system = _db.DbFactory.CreateSystem())
        {
            var now = DateTime.UtcNow;
            foreach (var endpoint in new[] { visible, hidden })
            {
                system.Alerts.Add(new Alert
                {
                    Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Kind = AlertKind.Offline, Severity = AlertSeverity.Warning,
                    Title = "Offline", OpenedAt = now, UpdatedAt = now
                });
                system.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord("endpoint.test", "Endpoint", endpoint.Id.ToString(), client.Id,
                    AuditActorType.System, "test", "test", null, null), now));
            }

            system.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord("client.test", "Client", client.Id.ToString(), client.Id,
                AuditActorType.System, "test", "test", null, null), now));
            await system.SaveChangesAsync();
        }

        IClientScope scope;
        await using (var system = _db.DbFactory.CreateSystem())
        {
            scope = await UserAccessResolver.ScopeOfAsync(system, technician.Id, CancellationToken.None);
        }

        await using (var db = _db.DbFactory.Create(scope))
        {
            Assert.True(await db.Clients.AnyAsync(c => c.Id == client.Id));
            Assert.Equal([included.Id], await db.Sites.Where(s => s.ClientId == client.Id).Select(s => s.Id).ToListAsync());
            Assert.Equal([visible.Id], await db.Endpoints.Where(e => e.ClientId == client.Id).Select(e => e.Id).ToListAsync());
            Assert.Equal([visible.Id], await db.Alerts.Where(a => a.ClientId == client.Id).Select(a => a.EndpointId).ToListAsync());
            // Of part of a client only the audit entries about its endpoints; nothing about the client as a whole.
            var audit = await db.AuditEntries.Where(a => a.ClientId == client.Id).Select(a => a.TargetId).ToListAsync();
            Assert.Equal([visible.Id.ToString()], audit);
        }

        // A new endpoint in the included site is visible at the next resolve, without anything kept in step.
        var later = await _db.CreateEndpointAsync(included, hostname: "PART-LATER");
        await using (var system = _db.DbFactory.CreateSystem())
        {
            scope = await UserAccessResolver.ScopeOfAsync(system, technician.Id, CancellationToken.None);
            Assert.True(await UserAccessResolver.MayReachAsync(system, technician.Id, client.Id, included.Id, later.Id, CancellationToken.None));
            Assert.False(await UserAccessResolver.MayReachAsync(system, technician.Id, client.Id, other.Id, hidden.Id, CancellationToken.None));
        }

        Assert.True(scope.SeesEndpoint(client.Id, later.Id));
        Assert.False(scope.SeesEndpoint(client.Id, hidden.Id));
    }

    [Fact]
    public async Task Writing_needs_the_endpoint_the_whole_site_or_the_whole_client()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id, "Mixed");
        var mine = await _db.CreateEndpointAsync(site, hostname: "WRITE-MINE");
        var excluded = await _db.CreateEndpointAsync(site, hostname: "WRITE-EXCLUDED");
        var technician = await _db.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        await GroupAsync(technician.Id, true, [(UserGroupRuleKind.Exclude, null, null, excluded.Id)]);
        IClientScope scope;
        await using (var system = _db.DbFactory.CreateSystem())
        {
            scope = await UserAccessResolver.ScopeOfAsync(system, technician.Id, CancellationToken.None);
        }

        Assert.True(scope.IncludesNewClients);
        var now = DateTime.UtcNow;

        // A row of a visible endpoint can be written.
        await using (var db = _db.DbFactory.Create(scope))
        {
            db.Notes.Add(new Note
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = mine.Id, Body = "ok", AuthorUserId = technician.Id, AuthorName = "t",
                CreatedAt = now, UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        // The excluded endpoint, the site it is in and the client are not the caller's to change.
        await using (var db = _db.DbFactory.Create(scope))
        {
            db.Notes.Add(new Note
            {
                Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = excluded.Id, Body = "no", AuthorUserId = technician.Id, AuthorName = "t",
                CreatedAt = now, UpdatedAt = now
            });
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
        }

        await using (var db = _db.DbFactory.Create(scope))
        {
            var visibleSite = await db.Sites.SingleAsync(s => s.Id == site.Id);
            visibleSite.Name = "Renamed by part of it";
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
        }

        await using (var db = _db.DbFactory.Create(scope))
        {
            var visibleClient = await db.Clients.SingleAsync(c => c.Id == client.Id);
            visibleClient.Name = "Renamed by part of it";
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => db.SaveChangesAsync());
        }

        Assert.False(await _db.DbFactory.Create(scope).Endpoints.AnyAsync(e => e.Id == excluded.Id));
    }

    [Fact]
    public async Task A_rule_goes_with_its_target_and_the_default_group_holds_the_users_of_before()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site);
        var technician = await _db.CreateUserAsync(FleetoRoles.Technician, allClients: false);
        var groupId = await GroupAsync(technician.Id, false, [(UserGroupRuleKind.Include, null, null, endpoint.Id)]);

        await using (var system = _db.DbFactory.CreateSystem())
        {
            system.Endpoints.Remove(await system.Endpoints.SingleAsync(e => e.Id == endpoint.Id));
            await system.SaveChangesAsync();
            Assert.False(await system.UserGroupRules.AnyAsync(r => r.GroupId == groupId));

            // The migration made the default group for every user of before 0.6.0.
            var fallback = await system.UserGroups.SingleAsync(g => g.IsDefault);
            Assert.True(fallback.AllClients);
        }
    }
}
