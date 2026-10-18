using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Settings, Performance in web (0.7.0): only for users with the permission, settings checked by the server and audited with what
/// changed, every component told, and an export that is audited and holds no client code or host name of the instance.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class PerformanceServiceTests
{
    private readonly WebFixture _fixture;

    public PerformanceServiceTests(WebFixture fixture) => _fixture = fixture;

    private PerformanceService Performance => _fixture.Services.GetRequiredService<PerformanceService>();

    [Fact]
    public async Task Only_users_with_the_permission_see_the_page_change_its_settings_or_export()
    {
        var technician = WebFixture.Technician();
        Assert.Null(await Performance.GetAsync(technician, PerfPeriod.Hour1));
        Assert.Null(await Performance.GetSettingsAsync(technician));
        Assert.False((await Performance.SaveSettingsAsync(technician, PerformanceSettings.Default)).Success);
        Assert.False((await Performance.ExportMarkdownAsync(technician, PerfPeriod.Hour1, "copied")).Success);

        var view = await Performance.GetAsync(WebFixture.Admin(), PerfPeriod.Hour1);
        Assert.NotNull(view);
        Assert.True(view.CanManage);
        Assert.Equal(TimeSpan.FromHours(1), view.To - view.From);
        Assert.Equal(60, view.Timeline.Count);
        Assert.Contains(view.Scores, s => s.Info.Category == PerfCategory.Agents);
        Assert.Contains(Permissions.PerformanceManage, BuiltInRoles.AdminPermissions);
        Assert.DoesNotContain(Permissions.PerformanceView, BuiltInRoles.TechnicianPermissions);
        Assert.True(Permissions.Find(Permissions.PerformanceView)!.InstanceWide);
    }

    [Fact]
    public async Task Saving_settings_checks_them_audits_the_change_and_tells_every_component()
    {
        var admin = WebFixture.Admin();
        var refused = await Performance.SaveSettingsAsync(admin, PerformanceSettings.Default with { MinuteRetentionDays = 100 });
        Assert.False(refused.Success);
        Assert.Contains("Minute data is kept", refused.Problem);

        var published = _fixture.Database.Bus.Published.Count(p => p.Channel == NotificationChannels.Performance);
        var settings = PerformanceSettings.Default with
        {
            DisabledCollectors = [PerfCollector.Host, PerfCollector.Host],
            Thresholds = new Dictionary<string, double> { [PerfThresholds.SlowInteraction] = 2_000, [PerfThresholds.SlowRequest] = 1_000 }
        };
        Assert.True((await Performance.SaveSettingsAsync(admin, settings)).Success);

        var stored = (await Performance.GetSettingsAsync(admin))!;
        Assert.Equal(new[] { PerfCollector.Host }, stored.DisabledCollectors);
        Assert.Equal(2_000, stored.Threshold(PerfThresholds.SlowInteraction));
        // A threshold at its default is not stored, so a later release can change the default.
        Assert.False(stored.Thresholds.ContainsKey(PerfThresholds.SlowRequest));
        Assert.Equal(published + 1, _fixture.Database.Bus.Published.Count(p => p.Channel == NotificationChannels.Performance));
        Assert.False(_fixture.Services.GetRequiredService<PerfSettingsCache>().Current.Collects(PerfCollector.Host));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.PerformanceSettingsChanged).OrderByDescending(a => a.Id).FirstAsync();
        Assert.Contains("\"after\"", audit.DetailsJson);
        Assert.Contains("2000", audit.DetailsJson);

        Assert.True((await Performance.SaveSettingsAsync(admin, PerformanceSettings.Default)).Success);
    }

    [Fact]
    public async Task The_export_is_audited_and_masks_the_client_codes_and_host_names_of_the_instance()
    {
        var client = await _fixture.Database.CreateClientAsync("PERFCLIENT");
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "PERF-SECRET-HOST");
        var now = PerfRecorder.MinuteOf(_fixture.Database.Time.GetUtcNow().UtcDateTime);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            // A key that carries a value by mistake: the redactor still masks it.
            db.PerfMinutes.Add(new PerfMinute
            {
                Minute = now.AddMinutes(-10), Component = "workers", Kind = PerfKind.WorkerRun, Key = "sync PERFCLIENT on perf-secret-host", SubKey = "60",
                Count = 5, Sum = 500, Max = 200, Histogram = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 5]
            });
            await db.SaveChangesAsync();
        }

        var result = await Performance.ExportMarkdownAsync(WebFixture.Admin(), PerfPeriod.Hour1, "downloaded");
        Assert.True(result.Success);
        var report = result.Value!;
        Assert.StartsWith("# Fleeto performance report", report);
        Assert.Contains("## Instructions for Claude Code", report);
        Assert.Contains("## Missing data / limitations", report);
        Assert.Contains("sync {client} on {host}", report);
        Assert.DoesNotContain("PERFCLIENT", report);
        Assert.DoesNotContain("PERF-SECRET-HOST", report, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(TestingFqdn, report);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        var exports = await check.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.PerformanceExported).Select(a => a.DetailsJson).ToListAsync();
        Assert.Contains(exports, details => details.Contains("downloaded", StringComparison.Ordinal));
    }

    private const string TestingFqdn = Testing.TestDatabase.Fqdn;
}
