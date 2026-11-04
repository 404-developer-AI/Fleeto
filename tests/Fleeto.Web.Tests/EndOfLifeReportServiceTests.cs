using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// The end-of-life report on the web (0.8.0 step 4, VULNERABILITIES.md §12.5): a refresh is asked for at most once a minute and audited, the
/// mark of a paid extension needs "Edit endpoints", a managed endpoint and a release with an extension, and is audited; the settings need
/// the permission Vulnerabilities and refuse a schedule that cannot run.
/// </summary>
[Collection(WebCollection.Name)]
public class EndOfLifeReportServiceTests
{
    private readonly WebFixture _fixture;

    public EndOfLifeReportServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private EndOfLifeReportService Service => _fixture.Services.GetRequiredService<EndOfLifeReportService>();

    [Fact]
    public async Task A_refresh_is_asked_for_at_most_once_a_minute_and_audited()
    {
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.EndOfLifeRefreshRequests.ExecuteDeleteAsync();
        }

        var technician = WebFixtureBase.Technician();
        Assert.True((await Service.RequestRefreshAsync(technician)).Success);
        var again = await Service.RequestRefreshAsync(technician);
        Assert.False(again.Success);
        Assert.Contains("less than a minute", again.Problem);
        _fixture.Database.Time.Advance(EndOfLife.RefreshCooldown);
        Assert.True((await Service.RequestRefreshAsync(technician)).Success);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(2, await check.EndOfLifeRefreshRequests.CountAsync(r => r.UserId == technician.UserId));
        Assert.Equal(2, await check.AuditEntries.CountAsync(a => a.Action == AuditActions.EndOfLifeRefreshRequested && a.ActorId == technician.UserId.ToString()));

        var overview = await Service.GetOverviewAsync(technician, null, null);
        Assert.True(overview.RefreshPending);
        Assert.Null(overview.ReportId);
        Assert.Equal(EndOfLife.Products.Count, overview.Sources.Count);
    }

    [Fact]
    public async Task The_mark_of_a_paid_extension_needs_edit_endpoints_a_managed_endpoint_and_a_release_with_an_extension()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id, "Monitoring");
        var windows10 = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-W10");
        var windows11 = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-W11");
        var free = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-FREE");
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == windows10.Id || e.Id == free.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.OsName, "Windows 10 Pro").SetProperty(e => e.OsVersion, "10.0.19045"));
            await db.EndOfLifeReleases.Where(r => r.Product == EndOfLifeProduct.Windows).ExecuteDeleteAsync();
            db.EndOfLifeReleases.AddRange(
                new EndOfLifeReleaseRecord
                {
                    Product = EndOfLifeProduct.Windows, Release = "10-22h2", Label = "10 22H2", EndOfLifeOn = new DateOnly(2025, 10, 14),
                    ExtensionUntil = new DateOnly(2028, 10, 10), LatestBuild = "10.0.19045", FetchedAt = DateTime.UtcNow
                },
                new EndOfLifeReleaseRecord
                {
                    Product = EndOfLifeProduct.Windows, Release = "11-25h2-w", Label = "11 25H2 (W)", EndOfLifeOn = new DateOnly(2027, 10, 12),
                    LatestBuild = "10.0.26200", FetchedAt = DateTime.UtcNow
                });
            await db.SaveChangesAsync();
        }

        Assert.False((await Service.SetExtensionAsync(WebFixtureBase.CallerWith(Infrastructure.Data.SystemClientScope.Instance, FleetoRoles.ReadOnly),
            windows10.Id, true)).Success);
        var technician = WebFixtureBase.Technician();
        Assert.Contains("no paid extension", (await Service.SetExtensionAsync(technician, windows11.Id, true)).Problem);
        Assert.Contains("managed endpoints only", (await Service.SetExtensionAsync(technician, free.Id, true)).Problem);
        Assert.True((await Service.SetExtensionAsync(technician, windows10.Id, true)).Success);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.Equal("10-22h2", (await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == windows10.Id)).EndOfLifeExtensionRelease);
            var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == AuditActions.EndOfLifeExtensionChanged && a.TargetId == windows10.Id.ToString());
            Assert.Contains("10-22h2", audit.DetailsJson);
        }

        Assert.True((await Service.SetExtensionAsync(technician, windows10.Id, false)).Success);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.Null((await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == windows10.Id)).EndOfLifeExtensionRelease);
        }
    }

    [Fact]
    public async Task The_settings_need_the_permission_vulnerabilities_and_refuse_a_schedule_that_cannot_run()
    {
        var schedule = new EndOfLifeSchedule(EndOfLifeScheduleKind.Weekly, DayOfWeek.Friday, 1, 8, "Europe/Brussels");
        Assert.False((await Service.SaveSettingsAsync(WebFixtureBase.Technician(), schedule, 6)).Success);
        var admin = WebFixtureBase.Admin();
        Assert.False((await Service.SaveSettingsAsync(admin, schedule with { TimeZone = "Nowhere/Land" }, 6)).Success);
        Assert.False((await Service.SaveSettingsAsync(admin, schedule, 30)).Success);
        Assert.True((await Service.SaveSettingsAsync(admin, schedule, 9)).Success);

        var settings = await Service.GetSettingsAsync(admin);
        Assert.Equal((schedule, 9), (settings.Schedule, settings.PeriodMonths));
        Assert.NotNull(settings.NextRun);
        Assert.Equal(DayOfWeek.Friday, TimeZoneInfo.ConvertTimeFromUtc(settings.NextRun!.Value, MaintenanceWindows.FindTimeZone("Europe/Brussels")!).DayOfWeek);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.EndOfLifeSettingsChanged && a.ActorId == admin.UserId.ToString()));
    }
}
