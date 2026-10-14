using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The catalog of permissions (0.6.0): one list, stable keys, requirements that exist and hold for the built-in roles, and the
/// built-in roles as they were before custom roles came, so the update changes nothing for an existing user.
/// </summary>
public class PermissionCatalogTests
{
    [Fact]
    public void Keys_are_unique_and_short_enough_to_store_and_every_requirement_exists()
    {
        var keys = Permissions.All.Select(p => p.Key).ToList();
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(keys, k => Assert.InRange(k.Length, 1, RoleRules.MaxPermissionLength));
        Assert.All(keys, k => Assert.Matches("^[a-z0-9]+\\.[a-z0-9]+$", k));
        Assert.All(Permissions.All.SelectMany(p => p.Requires), r => Assert.True(Permissions.IsKnown(r), r));
        Assert.All(Permissions.All, p => Assert.DoesNotContain(p.Key, p.Requires));
        Assert.All(Permissions.All, p => Assert.False(string.IsNullOrWhiteSpace(p.Label) || string.IsNullOrWhiteSpace(p.Description), p.Key));
    }

    [Fact]
    public void Every_built_in_role_meets_the_requirements_of_its_permissions()
    {
        Assert.Null(Permissions.Problem(BuiltInRoles.AdminPermissions));
        Assert.Null(Permissions.Problem(BuiltInRoles.TechnicianPermissions));
        Assert.Null(Permissions.Problem(BuiltInRoles.ReadOnlyPermissions));
    }

    [Fact]
    public void Built_in_roles_keep_what_they_could_do_before_custom_roles()
    {
        Assert.Equal(Permissions.All.Select(p => p.Key).Order(), BuiltInRoles.AdminPermissions.Order());
        Assert.Subset(BuiltInRoles.TechnicianPermissions.ToHashSet(), BuiltInRoles.ReadOnlyPermissions.ToHashSet());

        // Read-only views, never changes, and no administration.
        Assert.All(BuiltInRoles.ReadOnlyPermissions, p => Assert.EndsWith(".view", p));
        Assert.DoesNotContain(Permissions.AccessView, BuiltInRoles.ReadOnlyPermissions);

        // A technician did everything outside Settings except what was for admins only.
        string[] adminOnly = [Permissions.AccessView, Permissions.NotesDelete, Permissions.ScriptsApprove];
        Assert.All(adminOnly, p => Assert.DoesNotContain(p, BuiltInRoles.TechnicianPermissions));
        Assert.All(Permissions.All.Where(p => p.InstanceWide), p => Assert.DoesNotContain(p.Key, BuiltInRoles.TechnicianPermissions));
        Assert.All(Permissions.All.Where(p => !p.InstanceWide && !adminOnly.Contains(p.Key)),
            p => Assert.Contains(p.Key, BuiltInRoles.TechnicianPermissions));
    }

    [Fact]
    public void A_set_of_permissions_is_refused_with_an_unknown_key_or_without_what_one_needs()
    {
        Assert.Contains("does not exist", Permissions.Problem(["alerts.view", "clients.view", "nonsense.view"]));
        Assert.Contains("\"View clients and endpoints\"", Permissions.Problem([Permissions.AlertsView]));
        Assert.Contains("\"View jobs\"", Permissions.Problem([Permissions.ScriptsView, Permissions.ScriptsRun, Permissions.ClientsView]));
        Assert.Null(Permissions.Problem([Permissions.ClientsView, Permissions.AlertsView, Permissions.AlertsAcknowledge]));
        Assert.Null(Permissions.Problem([]));
    }

    [Fact]
    public void Only_the_administration_of_the_instance_is_instance_wide()
    {
        Assert.All(Permissions.All, p => Assert.Equal(p.Group == Permissions.AdministrationGroup, p.InstanceWide));
        Assert.Contains(Permissions.All, p => p.Key == Permissions.UsersManage && p.InstanceWide);
        Assert.Contains(Permissions.All, p => p.Key == Permissions.RemoteControl && !p.InstanceWide);
    }

    [Fact]
    public void The_built_in_roles_are_named_and_described_and_a_custom_role_is_not_built_in()
    {
        Assert.All(FleetoRoles.All, r => Assert.True(BuiltInRoles.IsBuiltIn(r)));
        Assert.All(FleetoRoles.All, r => Assert.NotNull(BuiltInRoles.PermissionsOf(r)));
        Assert.All(FleetoRoles.All, r => Assert.NotEmpty(BuiltInRoles.Description(r)));
        Assert.Equal(["Admin", "Technician", "Read-only"], FleetoRoles.All.Select(BuiltInRoles.DisplayName));
        Assert.False(BuiltInRoles.IsBuiltIn("Helpdesk"));
        Assert.Null(BuiltInRoles.PermissionsOf("Helpdesk"));
    }
}
