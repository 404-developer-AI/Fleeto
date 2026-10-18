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

    [Fact]
    public async Task Diagnose_starts_for_a_chosen_length_ends_by_itself_and_every_start_and_stop_is_audited()
    {
        var admin = WebFixture.Admin();
        Assert.False((await Performance.StartDiagnoseAsync(WebFixture.Technician(), 60)).Success);
        Assert.Contains("5 to 240 minutes", (await Performance.StartDiagnoseAsync(admin, 241)).Problem);

        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        Assert.True((await Performance.StartDiagnoseAsync(admin, 60)).Success);
        var view = (await Performance.GetAsync(admin, PerfPeriod.Hour1))!;
        Assert.Equal(now.AddMinutes(60), view.DiagnoseUntil);
        Assert.True(_fixture.Services.GetRequiredService<PerfSettingsCache>().Current.IsDiagnosing(now));

        // Saving the settings keeps Diagnose running; the level Off ends it.
        Assert.True((await Performance.SaveSettingsAsync(admin, view.Settings with { SlowEventMs = 900, DiagnoseUntil = null })).Success);
        Assert.Equal(now.AddMinutes(60), (await Performance.GetSettingsAsync(admin))!.DiagnoseUntil);

        Assert.True((await Performance.StopDiagnoseAsync(admin)).Success);
        Assert.Null((await Performance.GetSettingsAsync(admin))!.DiagnoseUntil);
        Assert.True((await Performance.StopDiagnoseAsync(admin)).Success);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var actions = await db.AuditEntries.AsNoTracking()
                .Where(a => a.Action == AuditActions.PerformanceDiagnoseStarted || a.Action == AuditActions.PerformanceDiagnoseStopped)
                .OrderBy(a => a.Id).Select(a => new { a.Action, a.DetailsJson }).ToListAsync();
            Assert.Equal([AuditActions.PerformanceDiagnoseStarted, AuditActions.PerformanceDiagnoseStopped], actions.TakeLast(2).Select(a => a.Action));
            Assert.Contains("\"minutes\": 60", actions[^2].DetailsJson);
        }

        Assert.True((await Performance.StartDiagnoseAsync(admin, 30)).Success);
        Assert.True((await Performance.SaveSettingsAsync(admin, PerformanceSettings.Default with { Level = PerfLevel.Off })).Success);
        Assert.Null((await Performance.GetSettingsAsync(admin))!.DiagnoseUntil);
        Assert.Contains("Off", (await Performance.StartDiagnoseAsync(admin, 60)).Problem);
        Assert.True((await Performance.SaveSettingsAsync(admin, PerformanceSettings.Default)).Success);
    }

    [Fact]
    public async Task A_marker_is_stored_masked_and_its_audit_entry_holds_its_length_only()
    {
        await _fixture.Database.CreateClientAsync("MARKCLIENT");
        var admin = WebFixture.Admin();
        Assert.False((await Performance.AddMarkerAsync(WebFixture.Technician(), "Added an index")).Success);
        Assert.False((await Performance.AddMarkerAsync(admin, "   ")).Success);
        Assert.True((await Performance.AddMarkerAsync(admin, "Added an index on Jobs for MARKCLIENT")).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var marker = await db.PerfMarkers.AsNoTracking().Where(m => m.Kind == PerfMarkerKind.Manual).OrderByDescending(m => m.Id).FirstAsync();
        Assert.Equal("Added an index on Jobs for {client}", marker.Label);
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.PerformanceMarkerAdded).OrderByDescending(a => a.Id).FirstAsync();
        Assert.DoesNotContain("index", audit.DetailsJson, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("37", audit.DetailsJson);
    }

    [Fact]
    public async Task A_stored_statement_is_planned_a_table_analyzed_and_both_are_audited()
    {
        var admin = WebFixture.Admin();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.PerfFingerprints.Add(new PerfFingerprint
            {
                Component = "web",
                Hash = "abcdefabcdef0123",
                Sql = """SELECT "Id" FROM "Clients" WHERE "Code" = ?""",
                FirstSeen = DateTime.UtcNow,
                LastSeen = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        Assert.False((await Performance.PlanAsync(WebFixture.Technician(), PerfPlanTarget.Fingerprint, "abcdefabcdef0123")).Success);
        Assert.False((await Performance.PlanAsync(admin, PerfPlanTarget.Fingerprint, "0000000000000000")).Success);
        var plan = await Performance.PlanAsync(admin, PerfPlanTarget.Fingerprint, "abcdefabcdef0123");
        Assert.True(plan.Success);
        Assert.True(plan.Value!.Planned, plan.Value.Problem);
        Assert.Contains(plan.Value.Nodes, n => n.Relation == "Clients");

        Assert.False((await Performance.AnalyzeAsync(WebFixture.Technician(), "Clients")).Success);
        Assert.Contains("no table", (await Performance.AnalyzeAsync(admin, "NoSuchTable")).Problem);
        Assert.True((await Performance.AnalyzeAsync(admin, "Clients")).Success);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == AuditActions.PerformancePlanned));
        var analyzed = await check.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.PerformanceTableAnalyzed).Select(a => a.DetailsJson)
            .ToListAsync();
        Assert.Contains(analyzed, details => details.Contains("Clients", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_JSON_and_ZIP_exports_are_audited_with_their_format_and_the_ZIP_holds_both_reports()
    {
        var admin = WebFixture.Admin();
        Assert.False((await Performance.ExportAsync(WebFixture.Technician(), PerfPeriod.Hour1, PerfExportFormat.Zip, "downloaded")).Success);

        var json = (await Performance.ExportAsync(admin, PerfPeriod.Hour1, PerfExportFormat.Json, "downloaded")).Value!;
        Assert.Equal("application/json", json.ContentType);
        using (var document = System.Text.Json.JsonDocument.Parse(json.Content))
        {
            Assert.Equal(PerformanceReport.JsonSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
        }

        var zip = (await Performance.ExportAsync(admin, PerfPeriod.Hour1, PerfExportFormat.Zip, "downloaded")).Value!;
        Assert.EndsWith(".zip", zip.FileName, StringComparison.Ordinal);
        using var archive = new System.IO.Compression.ZipArchive(new MemoryStream(zip.Content));
        Assert.Contains(archive.Entries, e => e.FullName == "fleeto-performance.md");
        Assert.Contains(archive.Entries, e => e.FullName == "fleeto-performance.json");

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var formats = await db.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.PerformanceExported).Select(a => a.DetailsJson).ToListAsync();
        Assert.Contains(formats, details => details.Contains("\"zip\"", StringComparison.Ordinal));
        Assert.Contains(formats, details => details.Contains("\"json\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_settings_of_PostgreSQL_are_read_live_for_users_with_the_permission()
    {
        Assert.Null(await Performance.GetLiveDatabaseAsync(WebFixture.Technician()));
        var live = (await Performance.GetLiveDatabaseAsync(WebFixture.Admin()))!;
        Assert.Contains(live.Settings, s => s.Name == "shared_buffers");
        Assert.StartsWith("17", live.ServerVersion, StringComparison.Ordinal);
    }

    private const string TestingFqdn = Testing.TestDatabase.Fqdn;
}
