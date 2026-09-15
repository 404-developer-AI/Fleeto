using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>The last admin can never be deleted or demoted, and only admins can manage users.</summary>
[Collection(WebCollection.Name)]
public class UserAdminTests
{
    private readonly WebFixture _fixture;

    public UserAdminTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Last_admin_cannot_be_deleted_or_demoted()
    {
        var service = _fixture.Services.GetRequiredService<UserAdminService>();
        var caller = WebFixtureBase.Admin();

        var created = await service.CreateAsync(caller, $"admin-{Guid.NewGuid():N}@example.com", "Only Admin", "a long temporary password",
            [FleetoRoles.Admin]);
        Assert.True(created.Success, created.Problem);
        var adminId = created.Value;

        // Make this user the last admin: demote every other admin (allowed while more than one exists).
        foreach (var other in (await service.ListAsync(caller)).Where(u => u.Id != adminId && u.Roles.Contains(FleetoRoles.Admin)))
        {
            var demoted = await service.SetRolesAsync(caller, other.Id, [FleetoRoles.Technician]);
            Assert.True(demoted.Success, demoted.Problem);
        }

        var demote = await service.SetRolesAsync(caller, adminId, [FleetoRoles.Technician]);
        Assert.False(demote.Success);
        Assert.Contains("last admin", demote.Problem);

        var delete = await service.DeleteAsync(caller, adminId);
        Assert.False(delete.Success);
        Assert.Contains("last admin", delete.Problem);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await users.FindByIdAsync(adminId.ToString());
            Assert.NotNull(admin);
            Assert.True(await users.IsInRoleAsync(admin!, FleetoRoles.Admin));
        }

        // With a second admin, the first can be demoted.
        var second = await service.CreateAsync(caller, $"admin-{Guid.NewGuid():N}@example.com", "Second Admin", "a long temporary password",
            [FleetoRoles.Admin]);
        Assert.True(second.Success, second.Problem);
        Assert.True((await service.SetRolesAsync(caller, adminId, [FleetoRoles.ReadOnly])).Success);
    }

    [Fact]
    public async Task Technicians_cannot_manage_users_and_passwords_need_twelve_characters()
    {
        var service = _fixture.Services.GetRequiredService<UserAdminService>();
        var technician = WebFixtureBase.Technician();

        var refused = await service.CreateAsync(technician, "tech@example.com", "Tech", "a long temporary password", [FleetoRoles.Admin]);
        Assert.Equal(ServiceResult.ForbiddenProblem, refused.Problem);
        await Assert.ThrowsAsync<AccessDeniedException>(() => service.ListAsync(technician));

        var weak = await service.CreateAsync(WebFixtureBase.Admin(), $"weak-{Guid.NewGuid():N}@example.com", "Weak", "short", [FleetoRoles.ReadOnly]);
        Assert.False(weak.Success);
    }

    [Fact]
    public async Task Reset_two_factor_turns_it_off_and_removes_the_key()
    {
        var service = _fixture.Services.GetRequiredService<UserAdminService>();
        var caller = WebFixtureBase.Admin();
        var created = await service.CreateAsync(caller, $"reset-{Guid.NewGuid():N}@example.com", "Reset", "a long temporary password", [FleetoRoles.Technician]);
        Assert.True(created.Success, created.Problem);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var user = (await users.FindByIdAsync(created.Value.ToString()))!;
            await users.ResetAuthenticatorKeyAsync(user);
            await users.SetTwoFactorEnabledAsync(user, true);
        }

        Assert.True((await service.ResetTwoFactorAsync(caller, created.Value)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.Users.Where(u => u.Id == created.Value).Select(u => u.TwoFactorEnabled).SingleAsync());
        Assert.False(await db.UserTokens.AnyAsync(t => t.UserId == created.Value && t.Name == "AuthenticatorKey"));
    }
}
