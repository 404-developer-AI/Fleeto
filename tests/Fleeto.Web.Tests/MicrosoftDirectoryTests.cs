using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees of choosing users from Microsoft (0.5.0): only admins ask, web never answers a question itself — it writes it
/// for the workers and deletes the answer once read — and a user added from Microsoft is linked, has no password and is
/// audited. One Entra ID account belongs to at most one user.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class MicrosoftDirectoryTests
{
    private const string TenantId = "8fa1c6b2-91d2-4b4e-9a0e-2f3c4d5e6a7b";
    private const string ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d";

    private readonly WebFixture _fixture;

    public MicrosoftDirectoryTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private UserAdminService Users => _fixture.Services.GetRequiredService<UserAdminService>();

    // Real time: the service polls for the answer, and the fake clock of the fixture does not move by itself.
    private MicrosoftDirectoryService Directory => new(_fixture.Database.DbFactory, _fixture.Database.Bus, TimeProvider.System);

    [Fact]
    public async Task A_user_added_from_Microsoft_is_linked_has_no_password_and_is_audited()
    {
        await ConfigureAsync();
        var admin = WebFixtureBase.Admin();
        var objectId = Guid.NewGuid();
        var email = $"entra-{Guid.NewGuid():N}@contoso.com";

        var created = await Users.CreateFromEntraAsync(admin, objectId, email, "An Engineer", email, [FleetoRoles.Technician]);
        Assert.True(created.Success, created.Problem);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Id == created.Value);
        Assert.Equal(objectId, user.EntraObjectId);
        Assert.Equal(TenantId, user.EntraTenantId);
        Assert.Equal(email, user.EntraAccount);
        Assert.Null(user.PasswordHash);
        Assert.Contains(FleetoRoles.Technician, (await Users.ListAsync(admin)).Single(u => u.Id == created.Value).Roles);

        var actions = await db.AuditEntries.AsNoTracking().Where(a => a.TargetId == created.Value.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.UserCreated, actions);
        Assert.Contains(AuditActions.UserLinked, actions);

        // The same account cannot be added or linked twice, and an address in use is not taken over.
        Assert.Contains("already linked", (await Users.CreateFromEntraAsync(admin, objectId, $"other-{Guid.NewGuid():N}@contoso.com", "Again", null,
            [FleetoRoles.Technician])).Problem);
        Assert.Contains("already exists", (await Users.CreateFromEntraAsync(admin, Guid.NewGuid(), email, "Same address", null,
            [FleetoRoles.Technician])).Problem);
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Users.CreateFromEntraAsync(WebFixtureBase.Technician(), Guid.NewGuid(),
            $"t-{Guid.NewGuid():N}@contoso.com", "Nope", null, [FleetoRoles.Technician])).Problem);
    }

    [Fact]
    public async Task Adding_from_Microsoft_needs_a_configured_sign_in()
    {
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Settings.Where(s => s.Key == SettingKeys.EntraSignIn).ExecuteDeleteAsync();
        }

        var result = await Users.CreateFromEntraAsync(WebFixtureBase.Admin(), Guid.NewGuid(), $"n-{Guid.NewGuid():N}@contoso.com", "Nobody", null,
            [FleetoRoles.Technician]);
        Assert.False(result.Success);
        Assert.Contains("Settings, Sign-in", result.Problem);
    }

    [Fact]
    public async Task A_search_is_answered_by_the_workers_and_read_once()
    {
        await ConfigureAsync();
        var admin = WebFixtureBase.Admin();
        var linkedEmail = $"linked-{Guid.NewGuid():N}@contoso.com";
        var linkedId = Guid.NewGuid();
        Assert.True((await Users.CreateFromEntraAsync(admin, linkedId, linkedEmail, "Linked One", linkedEmail, [FleetoRoles.ReadOnly])).Success);
        var freeId = Guid.NewGuid();

        MicrosoftRequest? asked = null;
        // Stands in for the workers: answers the question that web announced.
        using var worker = _fixture.Database.Bus.Subscribe(NotificationChannels.MicrosoftRequests, async (payload, _) =>
        {
            await using var db = _fixture.Database.DbFactory.CreateSystem();
            var request = await db.MicrosoftRequests.SingleAsync(r => r.Id == Guid.Parse(payload));
            asked = request;
            request.State = MicrosoftRequestState.Completed;
            request.ResultJson = MicrosoftAnswers.Serialize(new List<DirectoryUser>
            {
                new(linkedId, "Linked One", linkedEmail, linkedEmail),
                new(freeId, "Free One", "free@contoso.com", null)
            });
            await db.SaveChangesAsync();
        });

        var result = await Directory.SearchUsersAsync(admin, "\"free\": one");
        Assert.True(result.Success, result.Problem);
        Assert.Equal(MicrosoftRequestKind.SearchUsers, asked!.Kind);
        // What reaches the workers cannot change the search clause.
        Assert.Equal("free one", asked.Query);

        var users = result.Value!;
        Assert.Equal(linkedEmail, users.Single(u => u.ObjectId == linkedId).LinkedTo);
        var free = users.Single(u => u.ObjectId == freeId);
        Assert.Null(free.LinkedTo);
        Assert.Equal("free@contoso.com", free.Email);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await check.MicrosoftRequests.AnyAsync(r => r.Id == asked.Id));
    }

    [Fact]
    public async Task A_failed_answer_is_shown_and_only_admins_may_ask()
    {
        await ConfigureAsync();
        using var worker = _fixture.Database.Bus.Subscribe(NotificationChannels.MicrosoftRequests, async (payload, _) =>
        {
            await using var db = _fixture.Database.DbFactory.CreateSystem();
            var request = await db.MicrosoftRequests.SingleAsync(r => r.Id == Guid.Parse(payload));
            request.State = MicrosoftRequestState.Failed;
            request.FailureReason = "Microsoft refused to list the users of the tenant.";
            await db.SaveChangesAsync();
        });

        var result = await Directory.TestSignInAsync(WebFixtureBase.Admin());
        Assert.False(result.Success);
        Assert.Equal("Microsoft refused to list the users of the tenant.", result.Problem);

        Assert.Equal(ServiceResult.ForbiddenProblem, (await Directory.SearchUsersAsync(WebFixtureBase.Technician(), "a")).Problem);
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Directory.TestEmailAsync(WebFixtureBase.Technician())).Problem);
    }

    private async Task ConfigureAsync()
    {
        await _fixture.Services.GetRequiredService<SettingsStore>().SetAsync(SettingKeys.EntraSignIn, new EntraSignInSettings
        {
            Enabled = false,
            TenantId = TenantId,
            ClientId = ClientId,
            ClientSecret = "secret-value",
            ClientSecretExpiresAt = _fixture.Database.Time.GetUtcNow().UtcDateTime.AddDays(90)
        }, encrypted: true, userId: null);
    }
}
