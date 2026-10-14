using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Roles in the database (0.6.0): every start makes the built-in roles hold exactly the permissions of this build, a custom role
/// keeps exactly what was stored for it (so a permission a later release adds is off in it), and the signer's question whether a
/// user holds a permission reads both the same way.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class RolePermissionStoreTests
{
    private readonly TestDatabase _db;

    public RolePermissionStoreTests(DatabaseFixture fixture) => _db = fixture.Database;

    private async Task<HashSet<string>> StoredAsync(FleetoDbContext db, string roleName)
    {
        var normalized = roleName.ToUpperInvariant();
        return (await db.RolePermissions.Where(p => db.Roles.Any(r => r.Id == p.RoleId && r.NormalizedName == normalized))
            .Select(p => p.Permission).ToListAsync()).ToHashSet();
    }

    [Fact]
    public async Task Built_in_roles_are_made_equal_to_this_build_on_every_start_and_custom_roles_are_left_alone()
    {
        var custom = await _db.CreateRoleAsync(Permissions.ClientsView, Permissions.AlertsView);
        await using (var db = _db.DbFactory.CreateSystem())
        {
            // A permission an older build gave the technician, and one this build gives that went missing.
            var technician = await db.Roles.SingleAsync(r => r.NormalizedName == FleetoRoles.Technician.ToUpperInvariant());
            db.RolePermissions.Add(new RolePermission { RoleId = technician.Id, Permission = "retired.permission" });
            await db.RolePermissions.Where(p => p.RoleId == technician.Id && p.Permission == Permissions.RemoteControl).ExecuteDeleteAsync();
            await db.SaveChangesAsync();
        }

        await using (var db = _db.DbFactory.CreateSystem())
        {
            await InstanceInitializer.SyncBuiltInRolesAsync(db, CancellationToken.None);
            await db.SaveChangesAsync();
        }

        await using (var db = _db.DbFactory.CreateSystem())
        {
            Assert.Equal(BuiltInRoles.AdminPermissions.ToHashSet(), await StoredAsync(db, FleetoRoles.Admin));
            Assert.Equal(BuiltInRoles.TechnicianPermissions.ToHashSet(), await StoredAsync(db, FleetoRoles.Technician));
            Assert.Equal(BuiltInRoles.ReadOnlyPermissions.ToHashSet(), await StoredAsync(db, FleetoRoles.ReadOnly));
            Assert.Equal([Permissions.AlertsView, Permissions.ClientsView], (await StoredAsync(db, custom)).Order());
            Assert.True(await db.Roles.Where(r => FleetoRoles.All.Select(n => n.ToUpperInvariant()).Contains(r.NormalizedName!)).AllAsync(r => r.IsBuiltIn));
            Assert.False((await db.Roles.SingleAsync(r => r.NormalizedName == custom.ToUpperInvariant())).IsBuiltIn);
        }
    }

    [Fact]
    public async Task A_user_holds_a_permission_through_any_of_its_roles_and_only_while_it_may_sign_in()
    {
        var helpdesk = await _db.CreateRoleAsync(Permissions.ClientsView, Permissions.RemoteControl);
        var user = await _db.CreateUserAsync(helpdesk);
        var locked = await _db.CreateUserAsync(helpdesk, lockoutEnd: _db.Time.GetUtcNow().AddDays(1));
        var withoutTwoFactor = await _db.CreateUserAsync(helpdesk, twoFactor: false);
        var now = _db.Time.GetUtcNow().UtcDateTime;

        await using var db = _db.DbFactory.CreateSystem();
        Assert.True(await ScriptCheckResolver.UserHasPermissionAsync(db, user.Id, now, Permissions.RemoteControl, CancellationToken.None));
        Assert.False(await ScriptCheckResolver.UserHasPermissionAsync(db, user.Id, now, Permissions.RemoteBackground, CancellationToken.None));
        Assert.False(await ScriptCheckResolver.UserHasPermissionAsync(db, locked.Id, now, Permissions.RemoteControl, CancellationToken.None));
        Assert.False(await ScriptCheckResolver.UserHasPermissionAsync(db, withoutTwoFactor.Id, now, Permissions.RemoteControl, CancellationToken.None));

        // A second role adds what it gives.
        var scripts = await _db.CreateRoleAsync(Permissions.ScriptsView, Permissions.JobsView, Permissions.ScriptsRun);
        var scriptsRole = await db.Roles.SingleAsync(r => r.NormalizedName == scripts.ToUpperInvariant());
        db.UserRoles.Add(new Microsoft.AspNetCore.Identity.IdentityUserRole<Guid> { UserId = user.Id, RoleId = scriptsRole.Id });
        await db.SaveChangesAsync();
        Assert.True(await ScriptCheckResolver.UserHasPermissionAsync(db, user.Id, now, Permissions.ScriptsRun, CancellationToken.None));
    }
}
