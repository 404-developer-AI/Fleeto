using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees the sign-in with Microsoft Entra ID (0.5.0): only admins configure it, the client secret is write-only, the
/// sign-in page offers it only when it is complete and switched on, and a user signs in with Entra ID only after an admin has
/// linked it — one Entra account to at most one user, and never through an email address.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class SignInSettingsTests
{
    private const string Tenant = "contoso.onmicrosoft.com";
    private const string TenantId = "8fa1c6b2-91d2-4b4e-9a0e-2f3c4d5e6a7b";
    private const string ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d";

    private readonly WebFixture _fixture;

    public SignInSettingsTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private SignInSettingsService SignIn => _fixture.Services.GetRequiredService<SignInSettingsService>();

    private UserAdminService Users => _fixture.Services.GetRequiredService<UserAdminService>();

    private DateTime Today => _fixture.Database.Time.GetUtcNow().UtcDateTime.Date;

    private async Task CleanAsync()
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Settings.Where(s => s.Key == SettingKeys.EntraSignIn).ExecuteDeleteAsync();
        await db.Users.Where(u => u.EntraObjectId != null)
            .ExecuteUpdateAsync(u => u
                .SetProperty(x => x.EntraObjectId, (Guid?)null)
                .SetProperty(x => x.EntraTenantId, (string?)null)
                .SetProperty(x => x.EntraAccount, (string?)null)
                .SetProperty(x => x.EntraLinkedAt, (DateTime?)null));
    }

    [Fact]
    public async Task The_configuration_is_validated_and_the_secret_is_write_only()
    {
        await CleanAsync();
        var admin = WebFixtureBase.Admin();

        Assert.False((await SignIn.SaveAsync(admin, new SignInInput(false, "not a tenant", ClientId, "secret", Today.AddDays(90)))).Success);
        Assert.False((await SignIn.SaveAsync(admin, new SignInInput(false, Tenant, "abc", "secret", Today.AddDays(90)))).Success);
        Assert.False((await SignIn.SaveAsync(admin, new SignInInput(false, Tenant, ClientId, null, Today.AddDays(90)))).Success);
        Assert.False((await SignIn.SaveAsync(admin, new SignInInput(false, Tenant, ClientId, "secret", null))).Success);
        Assert.False((await SignIn.SaveAsync(admin, new SignInInput(false, Tenant, ClientId, "secret", Today.AddDays(-1)))).Success);
        Assert.Equal(ServiceResult.ForbiddenProblem,
            (await SignIn.SaveAsync(WebFixtureBase.Technician(), new SignInInput(false, Tenant, ClientId, "secret", Today.AddDays(90)))).Problem);

        var saved = await SignIn.SaveAsync(admin, new SignInInput(true, Tenant, ClientId, "super-secret-value", Today.AddDays(90)));
        Assert.True(saved.Success, saved.Problem);

        var view = await SignIn.GetAsync(admin);
        Assert.True(view.Enabled);
        Assert.True(view.IsComplete);
        Assert.True(view.HasClientSecret);
        Assert.Equal(Tenant, view.TenantId);
        Assert.Equal(Today.AddDays(91).AddSeconds(-1), view.ClientSecretExpiresAt);
        Assert.EndsWith("/api/account/entra/callback", view.RedirectUri);
        Assert.DoesNotContain("super-secret-value", System.Text.Json.JsonSerializer.Serialize(view));

        // A blank secret keeps the stored one; the tenant can still change.
        Assert.True((await SignIn.SaveAsync(admin, new SignInInput(true, TenantId, ClientId, null, null))).Success);
        var kept = await SignIn.GetAsync(admin);
        Assert.True(kept.HasClientSecret);
        Assert.Equal(TenantId, kept.TenantId);
        Assert.True(await SignIn.IsEntraOfferedAsync());

        // The audit log records the change without the secret.
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var entries = await db.AuditEntries.AsNoTracking()
            .Where(e => e.Action == AuditActions.SignInConfigured || e.Action == AuditActions.CredentialChanged)
            .OrderByDescending(e => e.Id).Take(5).ToListAsync();
        Assert.NotEmpty(entries);
        Assert.All(entries, e => Assert.DoesNotContain("super-secret-value", e.DetailsJson ?? string.Empty));
    }

    [Fact]
    public async Task The_sign_in_page_offers_entra_only_when_it_is_complete_and_on()
    {
        await CleanAsync();
        var admin = WebFixtureBase.Admin();

        Assert.False(await SignIn.IsEntraOfferedAsync());
        Assert.False((await SignIn.SetEnabledAsync(admin, true)).Success);

        Assert.True((await SignIn.SaveAsync(admin, new SignInInput(false, Tenant, ClientId, "secret-value", Today.AddDays(30)))).Success);
        Assert.False(await SignIn.IsEntraOfferedAsync());

        Assert.True((await SignIn.SetEnabledAsync(admin, true)).Success);
        Assert.True(await SignIn.IsEntraOfferedAsync());

        Assert.True((await SignIn.SetEnabledAsync(admin, false)).Success);
        Assert.False(await SignIn.IsEntraOfferedAsync());
        Assert.Equal(ServiceResult.ForbiddenProblem, (await SignIn.SetEnabledAsync(WebFixtureBase.Technician(), true)).Problem);
    }

    [Fact]
    public async Task A_user_is_linked_by_an_admin_and_one_account_belongs_to_one_user()
    {
        await CleanAsync();
        var admin = WebFixtureBase.Admin();
        var first = await CreateUserAsync(admin, "Linked Technician");
        var second = await CreateUserAsync(admin, "Another Technician");
        var objectId = Guid.NewGuid();

        // Without a configured sign-in there is nothing to link to.
        Assert.False((await Users.LinkEntraAsync(admin, first, objectId.ToString("D"), "tech@contoso.com")).Success);
        Assert.True((await SignIn.SaveAsync(admin, new SignInInput(true, TenantId, ClientId, "secret-value", Today.AddDays(60)))).Success);

        Assert.False((await Users.LinkEntraAsync(admin, first, "not a guid", null)).Success);
        Assert.False((await Users.LinkEntraAsync(admin, first, Guid.Empty.ToString("D"), null)).Success);
        Assert.Equal(ServiceResult.ForbiddenProblem,
            (await Users.LinkEntraAsync(WebFixtureBase.Technician(), first, objectId.ToString("D"), null)).Problem);

        var linked = await Users.LinkEntraAsync(admin, first, objectId.ToString("D"), "tech@contoso.com");
        Assert.True(linked.Success, linked.Problem);

        var listed = (await Users.ListAsync(admin)).Single(u => u.Id == first);
        Assert.Equal(objectId, listed.EntraObjectId);
        Assert.Equal("tech@contoso.com", listed.EntraAccount);

        // The same Entra account cannot be given to a second user.
        var taken = await Users.LinkEntraAsync(admin, second, objectId.ToString("D"), null);
        Assert.False(taken.Success);
        Assert.Contains("already linked", taken.Problem);

        Assert.True((await Users.UnlinkEntraAsync(admin, first)).Success);
        var unlinked = (await Users.ListAsync(admin)).Single(u => u.Id == first);
        Assert.Null(unlinked.EntraObjectId);
        Assert.Null(unlinked.EntraAccount);

        // Unlinking twice is not an error, and the account is free again.
        Assert.True((await Users.UnlinkEntraAsync(admin, first)).Success);
        Assert.True((await Users.LinkEntraAsync(admin, second, objectId.ToString("D"), null)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var actions = await db.AuditEntries.AsNoTracking()
            .Where(e => e.Action == AuditActions.UserLinked || e.Action == AuditActions.UserUnlinked)
            .Select(e => e.Action).Distinct().ToListAsync();
        Assert.Contains(AuditActions.UserLinked, actions);
        Assert.Contains(AuditActions.UserUnlinked, actions);
    }

    private async Task<Guid> CreateUserAsync(Caller admin, string name)
    {
        var created = await Users.CreateAsync(admin, $"link-{Guid.NewGuid():N}@example.com", name, "a long temporary password",
            [FleetoRoles.Technician]);
        Assert.True(created.Success, created.Problem);
        return created.Value;
    }
}
