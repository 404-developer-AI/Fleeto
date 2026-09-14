using Fleetify.Infrastructure.Identity;
using Fleetify.Infrastructure.Security;
using Fleetify.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// Two-factor secrets at rest: the authenticator key is stored encrypted and bound to its user, recovery codes are stored
/// only as hashes and each redeems once.
/// </summary>
[Collection(WebCollection.Name)]
public class TwoFactorStoreTests
{
    private readonly WebFixture _fixture;

    public TwoFactorStoreTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<ApplicationUser> CreateUserAsync(UserManager<ApplicationUser> users)
    {
        var email = $"user-{Guid.NewGuid():N}@example.com";
        var user = new ApplicationUser { Id = Guid.NewGuid(), UserName = email, Email = email, DisplayName = "User", CreatedAt = DateTime.UtcNow };
        var result = await users.CreateAsync(user, "correct horse battery staple");
        Assert.True(result.Succeeded, string.Join(" ", result.Errors.Select(e => e.Description)));
        return user;
    }

    [Fact]
    public async Task Authenticator_key_is_stored_encrypted_and_round_trips()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await CreateUserAsync(users);

        await users.ResetAuthenticatorKeyAsync(user);
        var key = await users.GetAuthenticatorKeyAsync(user);
        Assert.False(string.IsNullOrEmpty(key));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var stored = await db.UserTokens.AsNoTracking()
            .Where(t => t.UserId == user.Id && t.Name == "AuthenticatorKey")
            .Select(t => t.Value)
            .SingleAsync();
        Assert.NotNull(stored);
        Assert.DoesNotContain(key!, stored);

        // Bound to the user: the ciphertext does not decrypt for another account.
        Assert.ThrowsAny<Exception>(() => _fixture.Database.SecretProtector.Unprotect(SecretPurposes.Identity, stored!,
            FleetifyUserStore.AuthenticatorKeyAssociatedData(Guid.NewGuid())));

        // A fresh scope (new context) reads the same key back.
        await using var second = _fixture.Services.CreateAsyncScope();
        var users2 = second.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var reloaded = await users2.FindByIdAsync(user.Id.ToString());
        Assert.Equal(key, await users2.GetAuthenticatorKeyAsync(reloaded!));
    }

    [Fact]
    public async Task Recovery_codes_are_stored_hashed_and_redeem_once()
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await CreateUserAsync(users);

        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToList();
        Assert.Equal(10, codes.Count);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var stored = await db.UserTokens.AsNoTracking()
                .Where(t => t.UserId == user.Id && t.Name == "RecoveryCodes")
                .Select(t => t.Value)
                .SingleAsync();
            foreach (var code in codes)
            {
                Assert.DoesNotContain(code, stored!, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(code.Replace("-", string.Empty), stored!, StringComparison.OrdinalIgnoreCase);
            }
        }

        Assert.Equal(10, await users.CountRecoveryCodesAsync(user));
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[3])).Succeeded);
        Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[3])).Succeeded);
        Assert.Equal(9, await users.CountRecoveryCodesAsync(user));

        // Typed without the hyphen and in lower case still works, once.
        Assert.True((await users.RedeemTwoFactorRecoveryCodeAsync(user, codes[5].Replace("-", string.Empty).ToLowerInvariant())).Succeeded);
        Assert.False((await users.RedeemTwoFactorRecoveryCodeAsync(user, "not-a-code")).Succeeded);
        Assert.Equal(8, await users.CountRecoveryCodesAsync(user));
    }
}
