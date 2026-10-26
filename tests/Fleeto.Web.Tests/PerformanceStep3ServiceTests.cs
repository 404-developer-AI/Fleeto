using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;
using Fleeto.Web.Performance;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Settings, Performance in web, step 3 (0.7.0): an edge check or benchmark is asked for by users who manage the monitor, at most one
/// of a kind a minute, audited and handed to the workers; budgets are stored with the settings; and what a browser reports is
/// recorded under the route template of its page and only while the collector Browser is on.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class PerformanceStep3ServiceTests
{
    private readonly WebFixture _fixture;

    public PerformanceStep3ServiceTests(WebFixture fixture) => _fixture = fixture;

    private PerformanceService Performance => _fixture.Services.GetRequiredService<PerformanceService>();

    [Fact]
    public async Task A_probe_is_asked_for_by_users_who_manage_the_monitor_once_a_minute_and_is_audited()
    {
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.PerfProbes.ExecuteDeleteAsync();
        }

        Assert.False((await Performance.RequestProbeAsync(WebFixture.Technician(), PerfProbeKind.EdgeCheck)).Success);

        var admin = WebFixture.Admin();
        var published = _fixture.Database.Bus.Published.Count(p => p.Channel == NotificationChannels.PerformanceProbes);
        var first = await Performance.RequestProbeAsync(admin, PerfProbeKind.EdgeCheck);
        Assert.True(first.Success);
        Assert.Equal(published + 1, _fixture.Database.Bus.Published.Count(p => p.Channel == NotificationChannels.PerformanceProbes));

        var again = await Performance.RequestProbeAsync(admin, PerfProbeKind.EdgeCheck);
        Assert.False(again.Success);
        Assert.Contains("less than a minute ago", again.Problem);
        Assert.True((await Performance.RequestProbeAsync(admin, PerfProbeKind.Benchmark)).Success);
        Assert.False((await Performance.RequestProbeAsync(admin, (PerfProbeKind)42)).Success);

        var probe = await Performance.GetProbeAsync(admin, first.Value);
        Assert.NotNull(probe);
        Assert.True(probe.Running);
        Assert.Null(await Performance.GetProbeAsync(WebFixture.Technician(), first.Value));

        var view = (await Performance.GetAsync(admin, PerfPeriod.Hour1))!;
        Assert.Equal(2, view.Data.Probes.Count);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        var audits = await check.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.PerformanceProbeRequested).Select(a => a.DetailsJson).ToListAsync();
        Assert.Contains(audits, a => a.Contains("EdgeCheck", StringComparison.Ordinal));
        Assert.Contains(audits, a => a.Contains("Benchmark", StringComparison.Ordinal));
        Assert.Equal(1, await check.PerfProbes.CountAsync(p => p.Kind == PerfProbeKind.EdgeCheck));
    }

    [Fact]
    public async Task Budgets_are_stored_with_the_settings_and_a_wrong_one_is_refused()
    {
        var admin = WebFixture.Admin();
        var refused = await Performance.SaveSettingsAsync(admin, PerformanceSettings.Default with { Budgets = [new(PerfKind.Interaction, "/clients", "", 0)] });
        Assert.False(refused.Success);
        Assert.Contains("between 1 ms and 1 hour", refused.Problem);

        var budgets = PerfBudgets.Parse("page /clients load = 800\napi GET /api/v1/endpoints = 300").Budgets;
        Assert.True((await Performance.SaveSettingsAsync(admin, PerformanceSettings.Default with { Budgets = budgets, FrontendSamplePercent = 30 })).Success);
        var stored = (await Performance.GetSettingsAsync(admin))!;
        Assert.Equal(budgets, stored.Budgets);
        Assert.Equal(30, stored.FrontendSamplePercent);
        Assert.Equal(2, PerformanceRules.BudgetResults((await Performance.GetAsync(admin, PerfPeriod.Hour1))!.Data).Count);

        Assert.True((await Performance.SaveSettingsAsync(admin, PerformanceSettings.Default)).Success);
    }

    [Fact]
    public void A_browser_report_is_recorded_under_the_route_template_of_its_page_while_the_collector_is_on()
    {
        var routes = _fixture.Services.GetRequiredService<PageRoutes>();
        var settings = new PerfSettingsCache(PerformanceSettings.Default);
        var recorder = new PerfRecorder("web", settings, _fixture.Database.Time);
        var receiver = new PerfVitalsReceiver(recorder, routes, _fixture.Database.Time);
        PerfVitalSample[] report =
        [
            new($"/clients/{Guid.NewGuid()}", "lcp", 1200),
            new($"/clients/{Guid.NewGuid()}", "view", 0),
            new("/does-not-exist", "lcp", 1200),
            new(null, "circuit", 35),
        ];

        Assert.Equal(3, receiver.Accept(report));
        var keys = recorder.Swap(DateTime.UtcNow).Rows().Select(r => (r.Series.Kind, r.Series.Key, r.Series.SubKey)).ToList();
        Assert.Contains((PerfKind.Vital, "/clients/{ClientId:guid}", "lcp"), keys);
        Assert.Contains((PerfKind.Vital, "/clients/{ClientId:guid}", "view"), keys);
        Assert.Contains((PerfKind.Network, "circuit", ""), keys);
        Assert.DoesNotContain(keys, k => k.Key.Contains("does-not-exist", StringComparison.Ordinal));

        settings.Set(PerformanceSettings.Default with { DisabledCollectors = [PerfCollector.Frontend] });
        Assert.Equal(0, receiver.Accept(report));
        Assert.False(receiver.Samples());
        Assert.False(receiver.Wanted());

        var budget = new PerfVitalsBudget();
        var now = new DateTime(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
        Assert.Equal(PerfVitals.MaxReportsPerMinute, Enumerable.Range(0, 20).Count(_ => budget.Take(now)));
        Assert.True(budget.Take(now.AddMinutes(1)));
    }
}
