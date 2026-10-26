using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Security advisories on the dashboard (0.8.0 step 0): every built-in role sees them, also a user limited to clients; only a role
/// with the permission marks one as handled, with a required comment that the audit log holds by its length only; one handling per
/// advisory; a link to another host is never shown; an advisory is new until the user opened the panel after Fleeto stored it.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class AdvisoryServiceTests
{
    private readonly WebFixture _fixture;

    public AdvisoryServiceTests(WebFixture fixture) => _fixture = fixture;

    private AdvisoryService Advisories => _fixture.Services.GetRequiredService<AdvisoryService>();

    private async Task<Guid> SeedAsync(string sourceId, DateTime published, string? link = "https://ccb.belgium.be/advisories/warning")
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var advisory = new Advisory
        {
            Id = Guid.NewGuid(), Source = AdvisorySources.Ccb, SourceId = sourceId, Language = "en", Title = $"Warning {sourceId}", PublishedAt = published,
            Link = link, FetchedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime
        };
        db.Advisories.Add(advisory);
        await db.SaveChangesAsync();
        return advisory.Id;
    }

    private async Task ClearAsync()
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Advisories.ExecuteDeleteAsync();
        await db.AdvisorySources.Where(s => s.Key == AdvisorySources.Ccb).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true));
    }

    private async Task<Caller> UserAsync(string role)
    {
        var id = Guid.NewGuid();
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.Users.Add(new ApplicationUser { Id = id, UserName = $"{id}@example.com", Email = $"{id}@example.com", DisplayName = "Advisory reader", CreatedAt = DateTime.UtcNow });
        await db.SaveChangesAsync();
        return WebFixture.CallerWith(Infrastructure.Data.SystemClientScope.Instance, role) with { UserId = id };
    }

    [Fact]
    public async Task Every_built_in_role_sees_the_advisories_of_the_last_30_days_and_only_admins_and_technicians_handle_them()
    {
        await ClearAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await SeedAsync("recent", now.AddDays(-2));
        await SeedAsync("old", now.AddDays(-31));

        foreach (var role in new[] { FleetoRoles.Admin, FleetoRoles.Technician, FleetoRoles.ReadOnly })
        {
            var panel = await Advisories.GetPanelAsync(await UserAsync(role), markSeen: false);
            Assert.Equal(["Warning recent"], panel!.Advisories.Select(a => a.Title));
        }

        Assert.Contains(Permissions.AdvisoriesView, BuiltInRoles.ReadOnlyPermissions);
        Assert.DoesNotContain(Permissions.AdvisoriesHandle, BuiltInRoles.ReadOnlyPermissions);
        Assert.Contains(Permissions.AdvisoriesHandle, BuiltInRoles.TechnicianPermissions);
        // Public information: not administration, so a user limited to clients keeps it.
        Assert.False(Permissions.Find(Permissions.AdvisoriesView)!.InstanceWide);

        var withoutRole = WebFixture.CallerWith(Infrastructure.Data.SystemClientScope.Instance);
        Assert.Null(await Advisories.GetPanelAsync(withoutRole, markSeen: false));
    }

    [Fact]
    public async Task Handling_needs_the_permission_and_a_comment_happens_once_and_is_audited_without_its_text()
    {
        await ClearAsync();
        var id = await SeedAsync("handle", _fixture.Database.Time.GetUtcNow().UtcDateTime.AddHours(-1));
        var technician = await UserAsync(FleetoRoles.Technician);

        Assert.False((await Advisories.HandleAsync(await UserAsync(FleetoRoles.ReadOnly), id, "done")).Success);
        Assert.False((await Advisories.HandleAsync(technician, id, "  ")).Success);
        Assert.False((await Advisories.HandleAsync(technician, id, new string('x', AdvisoryRules.MaxCommentLength + 1))).Success);
        Assert.True((await Advisories.HandleAsync(technician, id, "Clients with BIND emailed")).Success);
        var again = await Advisories.HandleAsync(await UserAsync(FleetoRoles.Admin), id, "Me too");
        Assert.False(again.Success);
        Assert.Contains("already", again.Problem);

        var advisory = Assert.Single((await Advisories.GetPanelAsync(technician, markSeen: false))!.Advisories);
        Assert.Equal(("Clients with BIND emailed", "Test user"), (advisory.HandleComment, advisory.HandledByName));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = await db.AuditEntries.SingleAsync(a => a.Action == AuditActions.AdvisoryHandled && a.TargetId == id.ToString());
        Assert.DoesNotContain("BIND", audit.DetailsJson);
        Assert.Contains("25", audit.DetailsJson);
    }

    [Fact]
    public async Task An_advisory_is_new_until_the_panel_was_opened_after_it_was_stored()
    {
        await ClearAsync();
        var reader = await UserAsync(FleetoRoles.ReadOnly);
        await SeedAsync("first", _fixture.Database.Time.GetUtcNow().UtcDateTime.AddHours(-3));

        Assert.True(Assert.Single((await Advisories.GetPanelAsync(reader, markSeen: true))!.Advisories).IsNew);
        Assert.False(Assert.Single((await Advisories.GetPanelAsync(reader, markSeen: false))!.Advisories).IsNew);

        _fixture.Database.Time.Advance(TimeSpan.FromMinutes(10));
        await SeedAsync("second", _fixture.Database.Time.GetUtcNow().UtcDateTime.AddHours(-1));
        var panel = (await Advisories.GetPanelAsync(reader, markSeen: true))!;
        Assert.Equal([("Warning second", true), ("Warning first", false)], panel.Advisories.Select(a => (a.Title, a.IsNew)));
    }

    [Fact]
    public async Task A_link_to_another_host_is_never_shown_and_a_disabled_source_shows_nothing()
    {
        await ClearAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await SeedAsync("evil", now.AddHours(-1), "https://ccb.belgium.be.evil.example/advisories/x");
        await SeedAsync("plain", now.AddHours(-2), "http://ccb.belgium.be/advisories/y");
        var admin = await UserAsync(FleetoRoles.Admin);

        Assert.All((await Advisories.GetPanelAsync(admin, markSeen: false))!.Advisories, a => Assert.Null(a.Link));

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.AdvisorySources.Where(s => s.Key == AdvisorySources.Ccb).ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, false));
        }

        Assert.Empty((await Advisories.GetPanelAsync(admin, markSeen: false))!.Advisories);
        await ClearAsync();
    }
}
