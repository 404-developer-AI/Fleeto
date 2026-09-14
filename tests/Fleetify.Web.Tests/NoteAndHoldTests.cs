using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// Guarantees notes (managed endpoints only, author edits, admin deletes, another client's notes unreachable, body never in
/// the audit log, markdown rendered without script or unsafe links) and alert holds (bounds, left out of open counts and
/// lists, ended early).
/// </summary>
[Collection(WebCollection.Name)]
public sealed class NoteAndHoldTests
{
    private readonly WebFixture _fixture;

    public NoteAndHoldTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private NoteService Notes => _fixture.Services.GetRequiredService<NoteService>();
    private AlertService Alerts => _fixture.Services.GetRequiredService<AlertService>();
    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    private async Task<(Client Client, Endpoint Endpoint)> EndpointAsync(EndpointTier tier = EndpointTier.Managed)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        return (client, await _fixture.Database.CreateEndpointAsync(site, tier, "WS-NOTES"));
    }

    [Fact]
    public async Task Notes_are_listed_newest_first_edited_by_their_author_and_deleted_by_an_admin()
    {
        var (_, endpoint) = await EndpointAsync();
        var author = WebFixture.Technician();
        var colleague = WebFixture.Technician();
        var admin = WebFixture.Admin();
        const string secret = "Local admin password is in the vault, ticket 4711";

        var first = await Notes.CreateAsync(author, endpoint.Id, "Replaced the disk.");
        _fixture.Database.Time.Advance(TimeSpan.FromMinutes(1));
        var second = await Notes.CreateAsync(author, endpoint.Id, secret);
        Assert.True(first.Success && second.Success);
        Assert.False((await Notes.CreateAsync(author, endpoint.Id, "   ")).Success);
        Assert.False((await Notes.CreateAsync(author, endpoint.Id, new string('x', Note.MaxBodyLength + 1))).Success);

        var page = await Notes.ListAsync(WebFixture.CallerWith(SystemClientScope.Instance, FleetifyRoles.ReadOnly), endpoint.Id);
        Assert.Equal([second.Value, first.Value], page!.Items.Select(n => n.Id).ToArray());

        Assert.False((await Notes.UpdateAsync(colleague, first.Value, "Changed by someone else")).Success);
        Assert.False((await Notes.UpdateAsync(admin, first.Value, "Changed by an admin")).Success);
        Assert.True((await Notes.UpdateAsync(author, first.Value, "Replaced the disk and the fan.")).Success);
        var edited = (await Notes.ListAsync(author, endpoint.Id))!.Items.Single(n => n.Id == first.Value);
        Assert.Equal("Replaced the disk and the fan.", edited.Body);
        Assert.NotNull(edited.EditedAt);

        Assert.False((await Notes.DeleteAsync(author, second.Value)).Success);
        Assert.True((await Notes.DeleteAsync(admin, second.Value)).Success);
        Assert.Single((await Notes.ListAsync(author, endpoint.Id))!.Items);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var id = endpoint.Id.ToString();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.TargetId == id && a.Action.StartsWith("note.")).ToListAsync();
        Assert.Equal(4, audit.Count);
        Assert.All(audit, a => Assert.DoesNotContain("vault", a.DetailsJson));
        Assert.All(audit, a => Assert.DoesNotContain("disk", a.DetailsJson));
    }

    [Fact]
    public async Task Notes_are_refused_on_agent_only_endpoints_and_for_another_client()
    {
        var (_, agentOnly) = await EndpointAsync(EndpointTier.AgentOnly);
        var (clientA, _) = await EndpointAsync();
        var (_, endpointB) = await EndpointAsync();
        var admin = WebFixture.Admin();

        Assert.False((await Notes.CreateAsync(admin, agentOnly.Id, "Not allowed")).Success);
        Assert.False((await Notes.ListAsync(admin, agentOnly.Id))!.Managed);

        var noteB = await Notes.CreateAsync(admin, endpointB.Id, "Client B only");
        var restricted = WebFixture.CallerWith(new RestrictedClientScope([clientA.Id]), FleetifyRoles.Admin);
        Assert.Null(await Notes.ListAsync(restricted, endpointB.Id));
        Assert.False((await Notes.CreateAsync(restricted, endpointB.Id, "Injected")).Success);
        Assert.False((await Notes.DeleteAsync(restricted, noteB.Value)).Success);

        // Switched back to agent-only: kept, but not shown or changeable.
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpointB.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Tier, EndpointTier.AgentOnly));
        }

        Assert.Empty((await Notes.ListAsync(admin, endpointB.Id))!.Items);
        Assert.False((await Notes.DeleteAsync(admin, noteB.Value)).Success);
        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(1, await check.Notes.CountAsync(n => n.EndpointId == endpointB.Id));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "<script")]
    [InlineData("<img src=x onerror=alert(1)>", "<img")]
    [InlineData("[click](javascript:alert(1))", "href=\"javascript")]
    [InlineData("[click](JaVaScRiPt:alert(1))", "href=")]
    [InlineData("[click](data:text/html;base64,PHNjcmlwdD4=)", "href=")]
    [InlineData("![pixel](https://tracker.example/p.png)", "<img")]
    [InlineData("<javascript:alert(1)>", "href=")]
    [InlineData("[ref][1]\n\n[1]: javascript:alert(1)", "href=")]
    [InlineData("[relative](/settings/users)", "href=")]
    public void Markdown_never_renders_script_images_or_unsafe_links(string markdown, string forbidden)
    {
        var html = NoteMarkdown.ToHtml(markdown).Value;
        Assert.DoesNotContain(forbidden, html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Markdown_keeps_safe_links_with_rel_attributes_and_formatting()
    {
        var html = NoteMarkdown.ToHtml("**Ticket** [SD-12](https://servicedesk.example/tickets/12) and https://example.com").Value;
        Assert.Contains("<strong>Ticket</strong>", html);
        Assert.Contains("href=\"https://servicedesk.example/tickets/12\"", html);
        Assert.Contains("noopener", html);
        Assert.Contains("href=\"https://example.com\"", html);
    }

    [Fact]
    public async Task A_held_alert_is_left_out_of_open_counts_and_lists_until_its_hold_ends()
    {
        var (_, endpoint) = await EndpointAsync();
        var alertId = Guid.NewGuid();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.Alerts.Add(new Alert
            {
                Id = alertId, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = AlertKind.Offline, Severity = AlertSeverity.Critical,
                Title = "WS-NOTES has been offline", OpenedAt = Now, UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var tech = WebFixture.Technician();
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        Assert.Equal(1, (await endpoints.GetAsync(tech, endpoint.Id))!.OpenAlertCount);

        Assert.False((await Alerts.HoldAsync(tech, alertId, Now.AddSeconds(10))).Success);
        Assert.False((await Alerts.HoldAsync(tech, alertId, Now.AddDays(8))).Success);
        Assert.False((await Alerts.HoldAsync(WebFixture.CallerWith(SystemClientScope.Instance, FleetifyRoles.ReadOnly), alertId, Now.AddHours(1))).Success);
        Assert.True((await Alerts.HoldAsync(tech, alertId, Now.AddHours(4))).Success);

        var detail = await endpoints.GetAsync(tech, endpoint.Id);
        Assert.Equal(0, detail!.OpenAlertCount);
        Assert.Equal(1, detail.HeldAlertCount);
        Assert.DoesNotContain((await Alerts.ListAsync(tech, new AlertQuery(AlertStateFilter.Open, null, endpoint.ClientId))).Items, a => a.Id == alertId);
        var held = Assert.Single((await Alerts.ListAsync(tech, new AlertQuery(AlertStateFilter.OnHold, null, endpoint.ClientId))).Items);
        Assert.True(held.IsHeld(Now));
        var dashboard = await _fixture.Services.GetRequiredService<DashboardService>().GetAsync(tech);
        Assert.DoesNotContain(dashboard.OpenAlerts, a => a.Id == alertId);

        // A hold whose time passed counts as ended even before the workers clear it.
        _fixture.Database.Time.Advance(TimeSpan.FromHours(5));
        Assert.Equal(1, (await endpoints.GetAsync(tech, endpoint.Id))!.OpenAlertCount);

        Assert.True((await Alerts.HoldAsync(tech, alertId, Now.AddHours(1))).Success);
        Assert.True((await Alerts.EndHoldAsync(tech, alertId)).Success);
        Assert.False((await Alerts.EndHoldAsync(tech, alertId)).Success);
        Assert.Equal(1, (await endpoints.GetAsync(tech, endpoint.Id))!.OpenAlertCount);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        var id = alertId.ToString();
        Assert.Equal(2, await check.AuditEntries.CountAsync(a => a.TargetId == id && a.Action == AuditActions.AlertHeld));
        Assert.Equal(1, await check.AuditEntries.CountAsync(a => a.TargetId == id && a.Action == AuditActions.AlertHoldEnded));
    }
}
