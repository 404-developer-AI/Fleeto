using System.Security.Claims;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees of the second factor with Microsoft Entra ID (0.5.0 step 2): a token that proves multi-factor authentication
/// replaces the local authenticator step and nothing else does, a linked user has no password left to sign in with, and the
/// instance always keeps one admin that signs in with a password, so a problem at Microsoft cannot lock everybody out.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class SignInTwoFactorTests
{
    private const string TenantId = "8fa1c6b2-91d2-4b4e-9a0e-2f3c4d5e6a7b";
    private const string ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d";
    private const string Password = "a long temporary password";

    private readonly WebFixture _fixture;

    public SignInTwoFactorTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private UserAdminService Users => _fixture.Services.GetRequiredService<UserAdminService>();

    private SignInSettingsService SignIn => _fixture.Services.GetRequiredService<SignInSettingsService>();

    private TwoFactorGate Gate => new(_fixture.Database.DbFactory);

    [Fact]
    public async Task A_linked_user_keeps_no_password_to_sign_in_with()
    {
        var admin = WebFixtureBase.Admin();
        await ConfigureAsync(admin);
        var userId = await CreateUserAsync(admin, FleetoRoles.Technician);

        Assert.True(await HasPasswordAsync(userId));
        Assert.True((await Users.LinkEntraAsync(admin, userId, Guid.NewGuid().ToString("D"), "tech@contoso.com")).Success);

        // The password is gone, so the password form has nothing to check even if somebody knows the old one.
        Assert.False(await HasPasswordAsync(userId));
        Assert.False(await PasswordMatchesAsync(userId, Password));

        // Setting a password while linked is refused: one way in.
        var refused = await Users.SetPasswordAsync(admin, userId, "another long password");
        Assert.False(refused.Success);
        Assert.Contains("Remove the link first", refused.Problem);

        // After unlinking an admin gives the user a password again, and short ones are refused.
        Assert.True((await Users.UnlinkEntraAsync(admin, userId)).Success);
        Assert.False((await Users.SetPasswordAsync(admin, userId, "short")).Success);
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Users.SetPasswordAsync(WebFixtureBase.Technician(), userId, "another long password")).Problem);
        Assert.True((await Users.SetPasswordAsync(admin, userId, "another long password")).Success);
        Assert.True(await PasswordMatchesAsync(userId, "another long password"));
        Assert.False(await PasswordMatchesAsync(userId, Password));
    }

    [Fact]
    public async Task A_token_that_proves_multi_factor_replaces_the_authenticator_step()
    {
        var admin = WebFixtureBase.Admin();
        await ConfigureAsync(admin);
        var userId = await CreateUserAsync(admin, FleetoRoles.Technician);
        Assert.True((await Users.LinkEntraAsync(admin, userId, Guid.NewGuid().ToString("D"), null)).Success);

        // Without the claim the user has no second factor yet and stays on the setup page.
        Assert.True(await Gate.RequiresSetupAsync(userId));
        Assert.True(await Gate.RequiresSetupAsync(Principal(userId, entraSecondFactor: false)));

        // Microsoft proved the second factor for this session: no local authenticator needed.
        Assert.False(await Gate.RequiresSetupAsync(Principal(userId, entraSecondFactor: true)));

        // Unlinking ends the exemption at once, without waiting for the session to expire.
        Assert.True((await Users.UnlinkEntraAsync(admin, userId)).Success);
        Assert.True(await Gate.RequiresSetupAsync(Principal(userId, entraSecondFactor: true)));

        // A user with a local authenticator needs no exemption at all, and a user that no longer exists fails closed.
        await EnableTwoFactorAsync(userId);
        Assert.False(await Gate.RequiresSetupAsync(Principal(userId, entraSecondFactor: false)));
        Assert.True(await Gate.RequiresSetupAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task The_last_admin_with_a_password_cannot_be_linked_demoted_or_deleted()
    {
        var admin = WebFixtureBase.Admin();
        await ConfigureAsync(admin);

        // Two admins, so the rule about the last admin of all is not what refuses this, and then every admin but one linked
        // to Entra ID: the one that is left is the only admin with a password.
        var breakGlass = await CreateUserAsync(admin, FleetoRoles.Admin);
        var linkedAdmin = await CreateUserAsync(admin, FleetoRoles.Admin);
        var others = (await Users.ListAsync(admin))
            .Where(u => u.Id != breakGlass && u.Roles.Contains(FleetoRoles.Admin) && u.EntraObjectId is null)
            .ToList();
        foreach (var other in others)
        {
            Assert.True((await Users.LinkEntraAsync(admin, other.Id, Guid.NewGuid().ToString("D"), null)).Success);
        }

        Assert.Contains(linkedAdmin, others.Select(o => o.Id));

        var link = await Users.LinkEntraAsync(admin, breakGlass, Guid.NewGuid().ToString("D"), null);
        Assert.False(link.Success);
        Assert.Contains("last admin that signs in with a password", link.Problem);

        var demote = await Users.SetRolesAsync(admin, breakGlass, [FleetoRoles.Technician]);
        Assert.False(demote.Success);
        Assert.Contains("last admin that signs in with a password", demote.Problem);

        var delete = await Users.DeleteAsync(admin, breakGlass);
        Assert.False(delete.Success);
        Assert.Contains("last admin that signs in with a password", delete.Problem);

        // With a second admin that has a password, the first may be linked after all.
        var second = await CreateUserAsync(admin, FleetoRoles.Admin);
        Assert.True((await Users.LinkEntraAsync(admin, breakGlass, Guid.NewGuid().ToString("D"), null)).Success);

        // Restore what this test changed, so the other tests keep the admins they found.
        Assert.True((await Users.UnlinkEntraAsync(admin, breakGlass)).Success);
        Assert.True((await Users.SetPasswordAsync(admin, breakGlass, Password)).Success);
        foreach (var other in others)
        {
            Assert.True((await Users.UnlinkEntraAsync(admin, other.Id)).Success);
            Assert.True((await Users.SetPasswordAsync(admin, other.Id, Password)).Success);
        }

        // The admins this test created stay, with a password: deleting them could leave the instance without a local admin,
        // which is exactly what the rule above forbids.
        Assert.True((await Users.DeleteAsync(admin, second)).Success);
    }

    [Fact]
    public async Task An_admin_without_a_password_does_not_count_and_is_never_held_back_by_the_rule()
    {
        var admin = WebFixtureBase.Admin();
        await ConfigureAsync(admin);

        // Every admin but one linked, so one admin signs in with a password. Then an admin that was linked and unlinked: it is
        // not linked, but has no password either, as after testing a link (found on v0.5.0-alpha.2).
        var breakGlass = await CreateUserAsync(admin, FleetoRoles.Admin);
        var unlinked = await CreateUserAsync(admin, FleetoRoles.Admin);
        var others = (await Users.ListAsync(admin))
            .Where(u => u.Id != breakGlass && u.Id != unlinked && u.Roles.Contains(FleetoRoles.Admin) && u.EntraObjectId is null)
            .ToList();
        foreach (var other in others)
        {
            Assert.True((await Users.LinkEntraAsync(admin, other.Id, Guid.NewGuid().ToString("D"), null)).Success);
        }

        Assert.True((await Users.LinkEntraAsync(admin, unlinked, Guid.NewGuid().ToString("D"), null)).Success);
        Assert.True((await Users.UnlinkEntraAsync(admin, unlinked)).Success);
        var listed = (await Users.ListAsync(admin)).Single(u => u.Id == unlinked);
        Assert.Null(listed.EntraObjectId);
        Assert.False(listed.HasPassword);

        // Taking it away cannot remove the last way in with a password, so nothing refuses it.
        var demote = await Users.SetRolesAsync(admin, unlinked, [FleetoRoles.Technician]);
        Assert.True(demote.Success, demote.Problem);
        Assert.True((await Users.SetRolesAsync(admin, unlinked, [FleetoRoles.Admin])).Success);
        var link = await Users.LinkEntraAsync(admin, unlinked, Guid.NewGuid().ToString("D"), null);
        Assert.True(link.Success, link.Problem);
        var delete = await Users.DeleteAsync(admin, unlinked);
        Assert.True(delete.Success, delete.Problem);

        // The one with a password is still held back.
        Assert.Contains("last admin that signs in with a password", (await Users.DeleteAsync(admin, breakGlass)).Problem);

        foreach (var other in others)
        {
            Assert.True((await Users.UnlinkEntraAsync(admin, other.Id)).Success);
            Assert.True((await Users.SetPasswordAsync(admin, other.Id, Password)).Success);
        }
    }

    [Fact]
    public void The_second_factor_of_a_session_survives_the_security_stamp_refresh()
    {
        var userId = Guid.NewGuid();
        var current = Principal(userId, entraSecondFactor: true);

        // Identity rebuilds the principal from the user store every five minutes; it knows nothing of this session.
        var refreshed = Principal(userId, entraSecondFactor: false);
        TwoFactorGate.CarryOverSecondFactor(current, refreshed);
        Assert.True(refreshed.HasClaim(TwoFactorGate.SecondFactorClaim, TwoFactorGate.EntraSecondFactor));

        // Twice does not add it twice, a session without the claim gains nothing, and nothing throws without a principal.
        TwoFactorGate.CarryOverSecondFactor(current, refreshed);
        Assert.Single(refreshed.FindAll(TwoFactorGate.SecondFactorClaim));

        var plain = Principal(userId, entraSecondFactor: false);
        TwoFactorGate.CarryOverSecondFactor(Principal(userId, entraSecondFactor: false), plain);
        Assert.Empty(plain.FindAll(TwoFactorGate.SecondFactorClaim));
        TwoFactorGate.CarryOverSecondFactor(null, plain);
        TwoFactorGate.CarryOverSecondFactor(current, null);
    }

    private static ClaimsPrincipal Principal(Guid userId, bool entraSecondFactor)
    {
        var identity = new ClaimsIdentity("test");
        identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, userId.ToString()));
        if (entraSecondFactor)
        {
            identity.AddClaim(new Claim(TwoFactorGate.SecondFactorClaim, TwoFactorGate.EntraSecondFactor));
        }

        return new ClaimsPrincipal(identity);
    }

    private async Task ConfigureAsync(Caller admin)
    {
        var view = await SignIn.GetAsync(admin);
        if (!view.IsComplete)
        {
            Assert.True((await SignIn.SaveAsync(admin, new SignInInput(true, TenantId, ClientId, "secret-value",
                _fixture.Database.Time.GetUtcNow().UtcDateTime.Date.AddDays(60)))).Success);
        }
    }

    private async Task<Guid> CreateUserAsync(Caller admin, string role)
    {
        var created = await Users.CreateAsync(admin, $"2fa-{Guid.NewGuid():N}@example.com", "A User", Password, [role]);
        Assert.True(created.Success, created.Problem);
        return created.Value;
    }

    private async Task<bool> HasPasswordAsync(Guid userId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Users.AsNoTracking().Where(u => u.Id == userId).Select(u => u.PasswordHash != null).SingleAsync();
    }

    private async Task<bool> PasswordMatchesAsync(Guid userId, string password)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        return user is not null && await users.CheckPasswordAsync(user, password);
    }

    private async Task EnableTwoFactorAsync(Guid userId)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(userId.ToString());
        Assert.NotNull(user);
        Assert.True((await users.SetTwoFactorEnabledAsync(user!, true)).Succeeded);
    }
}
