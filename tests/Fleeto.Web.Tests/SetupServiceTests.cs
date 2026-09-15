using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// The first-admin setup link works exactly once, is refused after it expires, and is refused by every link once any admin
/// exists. Runs in its own database, because it depends on no admin existing at the start.
/// </summary>
[Collection(SetupCollection.Name)]
public class SetupServiceTests
{
    private readonly SetupFixture _fixture;

    public SetupServiceTests(SetupFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<string> CreateTokenAsync(TimeSpan lifetime)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var (token, id, hash) = OpaqueTokens.Create(OpaqueTokens.SetupPrefix);
        db.SetupTokens.Add(new SetupToken { Id = id, TokenHash = hash, CreatedAt = now, ExpiresAt = now + lifetime });
        await db.SaveChangesAsync();
        return token;
    }

    private async Task<SetupResult> CompleteAsync(string token, string email)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        var setup = scope.ServiceProvider.GetRequiredService<SetupService>();
        return await setup.CompleteAsync(token, "First Admin", email, "a long enough password", "a long enough password", "127.0.0.1");
    }

    private async Task<SetupTokenState> CheckAsync(string token)
    {
        await using var scope = _fixture.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<SetupService>().CheckAsync(token);
    }

    [Fact]
    public async Task Setup_link_is_valid_once_refused_when_expired_and_refused_after_an_admin_exists()
    {
        // Expired: refused, nothing created.
        var expired = await CreateTokenAsync(TimeSpan.FromMinutes(-1));
        Assert.Equal(SetupTokenState.Expired, await CheckAsync(expired));
        var expiredResult = await CompleteAsync(expired, "expired@example.com");
        Assert.False(expiredResult.Success);
        Assert.Equal(SetupTokenState.Expired, expiredResult.State);

        // A malformed or unknown token is invalid.
        Assert.Equal(SetupTokenState.Invalid, await CheckAsync("fsu_not-a-token"));
        Assert.Equal(SetupTokenState.Invalid, await CheckAsync(OpaqueTokens.Create(OpaqueTokens.SetupPrefix).Token));

        // Validation problems do not use up the link.
        var token = await CreateTokenAsync(TimeSpan.FromHours(1));
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var weak = await scope.ServiceProvider.GetRequiredService<SetupService>()
                .CompleteAsync(token, "First Admin", "admin@example.com", "short", "short", null);
            Assert.Equal(SetupProblem.Password, weak.Problem);
        }

        Assert.Equal(SetupTokenState.Valid, await CheckAsync(token));

        // Valid: creates the admin and marks the link used.
        var result = await CompleteAsync(token, "admin@example.com");
        Assert.True(result.Success);
        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            var admin = await users.FindByEmailAsync("admin@example.com");
            Assert.NotNull(admin);
            Assert.True(await users.IsInRoleAsync(admin!, FleetoRoles.Admin));
        }

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == Core.Interfaces.AuditActions.FirstAdminCreated));
        }

        // Once: the same link does nothing a second time.
        var again = await CompleteAsync(token, "second@example.com");
        Assert.False(again.Success);

        // After an admin exists, even a fresh valid link is refused.
        var fresh = await CreateTokenAsync(TimeSpan.FromHours(1));
        Assert.Equal(SetupTokenState.AdminExists, await CheckAsync(fresh));
        var refused = await CompleteAsync(fresh, "third@example.com");
        Assert.False(refused.Success);
        Assert.Equal(SetupTokenState.AdminExists, refused.State);

        await using (var scope = _fixture.Services.CreateAsyncScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
            Assert.Null(await users.FindByEmailAsync("second@example.com"));
            Assert.Null(await users.FindByEmailAsync("third@example.com"));
        }
    }
}
