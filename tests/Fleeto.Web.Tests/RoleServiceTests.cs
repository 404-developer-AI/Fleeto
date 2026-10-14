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
/// Custom roles (0.6.0): the built-in roles cannot be changed, a custom role holds exactly the permissions given to it and the
/// services enforce them, nobody but an admin can use roles to reach further than it may itself, and every change is audited.
/// </summary>
[Collection(WebCollection.Name)]
public class RoleServiceTests
{
    private readonly WebFixture _fixture;

    public RoleServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private RoleService Roles => _fixture.Services.GetRequiredService<RoleService>();
    private UserAdminService Users => _fixture.Services.GetRequiredService<UserAdminService>();

    /// <summary>The caller of a user as CurrentUser builds it: its roles and permissions from the database, its scope from its groups.</summary>
    private async Task<Caller> CallerOfAsync(Guid userId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var (roles, granted) = await UserPermissions.LoadAsync(db, userId, CancellationToken.None);
        var scope = await UserAccessResolver.ScopeOfAsync(db, userId, CancellationToken.None);
        return new Caller(userId, "Role tester", "roles@example.com", roles, scope, "127.0.0.1", Granted: granted);
    }

    private static string Name() => "Helpdesk " + Guid.NewGuid().ToString("N")[..6];

    [Fact]
    public async Task Built_in_roles_are_listed_as_default_and_cannot_be_changed_or_deleted()
    {
        var admin = WebFixtureBase.Admin();
        var roles = await Roles.ListAsync(admin);

        Assert.Equal(["Admin", "Technician", "Read-only"], roles.Take(3).Select(r => r.DisplayName));
        Assert.All(roles.Take(3), r => Assert.True(r.IsBuiltIn));
        Assert.Equal(BuiltInRoles.TechnicianPermissions.Order(), roles[1].Permissions.Order());

        var technician = roles[1];
        Assert.Equal(RoleService.BuiltInProblem, (await Roles.UpdateAsync(admin, technician.Id, new RoleInput("Technician 2", null, []))).Problem);
        Assert.Equal(RoleService.BuiltInProblem, (await Roles.DeleteAsync(admin, technician.Id)).Problem);
        Assert.Contains("built-in role", (await Roles.CreateAsync(admin, new RoleInput("read-only", null, []))).Problem);
        Assert.Contains("built-in role", (await Roles.CreateAsync(admin, new RoleInput("Admin", null, []))).Problem);
    }

    [Fact]
    public async Task A_custom_role_is_created_changed_and_deleted_with_an_audit_entry_for_each()
    {
        var admin = WebFixtureBase.Admin();
        var name = Name();

        Assert.Contains("needs", (await Roles.CreateAsync(admin, new RoleInput(name, null, [Permissions.AlertsAcknowledge]))).Problem);
        Assert.Contains("does not exist", (await Roles.CreateAsync(admin, new RoleInput(name, null, ["made.up"]))).Problem);

        var created = await Roles.CreateAsync(admin, new RoleInput(name, "Answers the phone", [Permissions.ClientsView, Permissions.RemoteControl]));
        Assert.True(created.Success, created.Problem);
        Assert.Contains("already exists", (await Roles.CreateAsync(admin, new RoleInput(name.ToUpperInvariant(), null, []))).Problem);

        var updated = await Roles.UpdateAsync(admin, created.Value,
            new RoleInput(name, "Answers the phone", [Permissions.ClientsView, Permissions.NotesView, Permissions.NotesCreate]));
        Assert.True(updated.Success, updated.Problem);
        var role = Assert.Single(await Roles.ListAsync(admin), r => r.Id == created.Value);
        Assert.False(role.IsBuiltIn);
        Assert.Equal([Permissions.ClientsView, Permissions.NotesCreate, Permissions.NotesView], role.Permissions.Order());

        // A role in use is not deleted from under its users.
        var user = await _fixture.Database.CreateUserAsync(name);
        Assert.Contains("still has this role", (await Roles.DeleteAsync(admin, created.Value)).Problem);
        var demoted = await Users.SetRolesAsync(admin, user.Id, [FleetoRoles.ReadOnly]);
        Assert.True(demoted.Success, demoted.Problem);
        Assert.True((await Roles.DeleteAsync(admin, created.Value)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var actions = await db.AuditEntries.Where(a => a.TargetId == created.Value.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Equal([AuditActions.RoleCreated, AuditActions.RoleDeleted, AuditActions.RoleUpdated], actions.Order());
        Assert.False(await db.RolePermissions.AnyAsync(p => p.RoleId == created.Value));
    }

    [Fact]
    public async Task The_services_enforce_exactly_the_permissions_of_a_custom_role()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "ROLE-EP");
        var role = await _fixture.Database.CreateRoleAsync(Permissions.ClientsView, Permissions.NotesView, Permissions.NotesCreate);
        var user = await _fixture.Database.CreateUserAsync(role);
        var caller = await CallerOfAsync(user.Id);

        var notes = _fixture.Services.GetRequiredService<NoteService>();
        var note = await notes.CreateAsync(caller, endpoint.Id, "Called the user");
        Assert.True(note.Success, note.Problem);
        Assert.NotNull(await _fixture.Services.GetRequiredService<EndpointService>().GetAsync(caller, endpoint.Id));

        // Not given: refused by the service, whatever the UI shows.
        Assert.Equal(ServiceResult.ForbiddenProblem,
            (await _fixture.Services.GetRequiredService<EndpointService>().SetClassOverrideAsync(caller, endpoint.Id, EndpointClass.Server)).Problem);
        Assert.Equal(ServiceResult.ForbiddenProblem,
            (await _fixture.Services.GetRequiredService<ClientService>().CreateAsync(caller, "R" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant(), "Role client", null)).Problem);
        await Assert.ThrowsAsync<AccessDeniedException>(() => _fixture.Services.GetRequiredService<JobService>().ListForEndpointAsync(caller, endpoint.Id));
        await Assert.ThrowsAsync<AccessDeniedException>(() =>
            _fixture.Services.GetRequiredService<AlertService>().ListAsync(caller, new AlertQuery(AlertStateFilter.Open, null, null)));
        Assert.Null(await _fixture.Services.GetRequiredService<RemoteSessionService>().GetControlTargetAsync(caller, endpoint.Id));

        // Without the Software tab, the inventory comes without the installed software.
        var withoutSoftware = await _fixture.Services.GetRequiredService<EndpointService>().GetInventoryAsync(caller, endpoint.Id);
        Assert.True(withoutSoftware is null || withoutSoftware.Software.Count == 0);
    }

    [Fact]
    public async Task A_change_to_a_role_counts_at_once_for_its_users()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "ROLE-NOW");
        var admin = WebFixtureBase.Admin();
        var created = await Roles.CreateAsync(admin, new RoleInput(Name(), null, [Permissions.ClientsView]));
        var roleName = (await Roles.ListAsync(admin)).Single(r => r.Id == created.Value).Name;
        var user = await _fixture.Database.CreateUserAsync(roleName);
        var notes = _fixture.Services.GetRequiredService<NoteService>();

        Assert.False((await notes.CreateAsync(await CallerOfAsync(user.Id), endpoint.Id, "Not yet")).Success);
        Assert.True((await Roles.UpdateAsync(admin, created.Value,
            new RoleInput(roleName, null, [Permissions.ClientsView, Permissions.NotesView, Permissions.NotesCreate]))).Success);
        Assert.True((await notes.CreateAsync(await CallerOfAsync(user.Id), endpoint.Id, "Now")).Success);
        Assert.Contains(_fixture.Database.Bus.Published, n => n.Channel == NotificationChannels.UserAccess && n.Payload == user.Id.ToString());
    }

    [Fact]
    public async Task Administration_counts_only_for_a_user_whose_groups_give_every_client()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var role = await _fixture.Database.CreateRoleAsync(Permissions.ClientsView, Permissions.AuditView);
        var everywhere = await CallerOfAsync((await _fixture.Database.CreateUserAsync(role)).Id);
        var limitedUser = await _fixture.Database.CreateUserAsync(role, allClients: false);
        await _fixture.Database.CreateGroupAsync(limitedUser.Id, clientId: client.Id);
        var limited = await CallerOfAsync(limitedUser.Id);

        Assert.True(everywhere.Has(Permissions.AuditView));
        Assert.False(limited.Has(Permissions.AuditView));
        Assert.True(limited.Has(Permissions.ClientsView));
        Assert.DoesNotContain(Permissions.AuditView, limited.Effective);
    }

    [Fact]
    public async Task Somebody_who_manages_users_or_roles_cannot_reach_further_than_it_may_itself()
    {
        var admin = WebFixtureBase.Admin();
        var managerRole = await _fixture.Database.CreateRoleAsync(Permissions.UsersManage, Permissions.RolesManage, Permissions.ClientsView,
            Permissions.AlertsView);
        var manager = await CallerOfAsync((await _fixture.Database.CreateUserAsync(managerRole)).Id);

        // Roles: only with permissions the manager holds.
        Assert.Contains("do not have it yourself",
            (await Roles.CreateAsync(manager, new RoleInput(Name(), null, [Permissions.ClientsView, Permissions.RemoteControl]))).Problem);
        var allowed = await Roles.CreateAsync(manager, new RoleInput(Name(), null, [Permissions.ClientsView, Permissions.AlertsView]));
        Assert.True(allowed.Success, allowed.Problem);
        var technicianLike = await Roles.CreateAsync(admin, new RoleInput(Name(), null, [Permissions.ClientsView, Permissions.RemoteBackground]));
        Assert.Contains("do not have it yourself", (await Roles.DeleteAsync(manager, technicianLike.Value)).Problem);

        // Users: never the admin role, never a role with more, never a user that may do more.
        var plain = await _fixture.Database.CreateUserAsync(FleetoRoles.ReadOnly);
        Assert.Contains("Only an admin", (await Users.SetRolesAsync(manager, plain.Id, [FleetoRoles.Admin])).Problem);
        Assert.Contains("do not have it yourself", (await Users.SetRolesAsync(manager, plain.Id, [FleetoRoles.Technician])).Problem);
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        Assert.Contains("only an admin can change it", (await Users.SetPasswordAsync(manager, technician.Id, "a-new-long-password-1")).Problem);
        Assert.Contains("only an admin can change it", (await Users.DeleteAsync(manager, technician.Id)).Problem);
        var otherAdmin = await _fixture.Database.CreateUserAsync(FleetoRoles.Admin);
        Assert.Contains("Only an admin can change an admin", (await Users.ResetTwoFactorAsync(manager, otherAdmin.Id)).Problem);
        var created = await Users.CreateAsync(manager, $"new-{Guid.NewGuid():N}@example.com", "New user", "a-long-temporary-password", [FleetoRoles.Technician]);
        Assert.Contains("do not have it yourself", created.Problem);

        // Read-only holds views the manager lacks, so the manager cannot give it either; a role that gives no more it can.
        Assert.Contains("do not have it yourself", (await Users.SetRolesAsync(manager, plain.Id, [FleetoRoles.ReadOnly])).Problem);
        var mine = (await Roles.ListAsync(admin)).Single(r => r.Id == allowed.Value).Name;
        var result = await Users.SetRolesAsync(manager, (await _fixture.Database.CreateUserAsync(mine)).Id, [mine]);
        Assert.True(result.Success, result.Problem);
    }

    [Fact]
    public async Task Switching_off_script_approval_needs_the_permission_to_approve_scripts()
    {
        var admin = WebFixtureBase.Admin();
        var policies = _fixture.Services.GetRequiredService<PolicyService>();
        var created = await policies.CreateAsync(admin, null, new PolicyInput("Approval " + Guid.NewGuid().ToString("N")[..6], null, 30, 3600, 10,
            AlertSeverity.Critical, ScriptApprovalRequired: true));
        Assert.True(created.Success, created.Problem);

        var technician = WebFixtureBase.Technician();
        var input = new PolicyInput("Approval off " + Guid.NewGuid().ToString("N")[..6], null, 30, 3600, 10, AlertSeverity.Critical,
            ScriptApprovalRequired: false);
        Assert.Equal(PolicyService.ScriptApprovalProblem, (await policies.UpdateAsync(technician, created.Value, input)).Problem);
        Assert.True((await policies.UpdateAsync(admin, created.Value, input)).Success);
    }
}
