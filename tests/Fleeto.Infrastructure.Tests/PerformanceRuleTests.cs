using Fleeto.Core.Performance;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The findings of the performance monitor (0.7.0, PERFORMANCE.md §7): every rule fires on its evidence and not without it, respects
/// its threshold and the minimum number of samples, explains itself and points to code; findings are ranked by impact, and a
/// category without data has no score rather than a fake 100.
/// </summary>
public sealed class PerformanceRuleTests
{
    private static readonly DateTime From = new(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(1);

    private static int[] Histogram(params (double Ms, int Count)[] values)
    {
        var histogram = new int[PerfHistogram.BucketCount];
        foreach (var (ms, count) in values)
        {
            histogram[PerfHistogram.BucketOf(ms)] += count;
        }

        return PerfHistogram.Trim(histogram);
    }

    private static PerfSeries Series(PerfKind kind, string key, string subKey, int count, double ms, double dbShare = 0, double dbPerCall = 0,
        long errors = 0, long bytes = 0, string component = "web", long? peak = null) =>
        new(component, kind, key, subKey, count, errors, ms * count, ms, Histogram((ms, count)), bytes, (long)(dbPerCall * count), ms * count * dbShare, 0,
            peak ?? count);

    private static PerformanceDataset Data(IReadOnlyList<PerfSeries>? series = null, PerfHostSummary? host = null,
        IReadOnlyList<PerfRuntimeSummary>? runtime = null, IReadOnlyList<PerfStatementDelta>? statements = null, PerfDatabaseSummary? database = null,
        IReadOnlyList<PerfTableSummary>? tables = null, IReadOnlyList<PerfOverlapMinute>? minutes = null, string? statementsProblem = null,
        PerformanceSettings? settings = null) =>
        new(From, To, settings ?? PerformanceSettings.Default, series ?? [], runtime ?? [], host, statements ?? [], statementsProblem, database, tables ?? [],
            minutes ?? [], [], []);

    private static PerfHostSummary Host(double cpu = 20, double steal = 0, long availableGb = 4, double? latency = 2, double? load = 1) =>
        new(60, 4, cpu, cpu + 5, steal, steal, 1, load, 8L << 30, availableGb << 30, 0, 0, latency, 10, 100L << 30, 50L << 30, null);

    private static PerfRuntimeSummary Runtime(string component = "web", double gc = 0.5, int queue = 0, long timeouts = 0, int busy = 5, int poolMax = 100,
        double overheadMs = 100) =>
        new(component, 60, "0.7.0", 5, 10, 300L << 20, 100L << 20, gc, 1L << 30, 20, queue, 0, 0, 3, busy, 2, poolMax, timeouts, overheadMs, 4);

    private static PerfFinding? Find(PerformanceDataset data, string id) => PerformanceRules.Evaluate(data).SingleOrDefault(f => f.Id == id);

    [Fact]
    public void A_quiet_healthy_period_has_no_findings_and_scores_100_where_there_is_data()
    {
        var data = Data(
            [Series(PerfKind.Interaction, "/clients", "load", 200, 120, dbShare: 0.3, dbPerCall: 3), Series(PerfKind.WorkerRun, "check-evaluation", "5", 600, 40, component: "workers")],
            Host(), [Runtime()]);
        var findings = PerformanceRules.Evaluate(data);
        Assert.Empty(findings);
        var scores = PerformanceRules.Score(data, findings);
        Assert.Equal(100, scores.Single(s => s.Info.Category == PerfCategory.Code).Score);
        Assert.Equal(100, scores.Single(s => s.Info.Category == PerfCategory.Hosting).Score);
        Assert.Null(scores.Single(s => s.Info.Category == PerfCategory.Agents).Score);
        Assert.Null(scores.Single(s => s.Info.Category == PerfCategory.Frontend).Score);
        Assert.Null(scores.Single(s => s.Info.Category == PerfCategory.Network).Score);
        Assert.All(scores, s => Assert.True(s.Info.Measured));
    }

    [Theory]
    [InlineData(900, null)]
    [InlineData(1_500, PerfSeverity.Warning)]
    [InlineData(5_000, PerfSeverity.Critical)]
    public void A_slow_page_is_found_by_its_95th_percentile(double ms, PerfSeverity? expected)
    {
        var finding = Find(Data([Series(PerfKind.Interaction, "/clients/{ClientId:guid}", "load", 100, ms)]), "code-slow-interaction:/clients/{ClientId:guid}:load");
        Assert.Equal(expected, finding?.Severity);
        if (finding is not null)
        {
            Assert.Equal(PerfCategory.Code, finding.Category);
            Assert.Contains(finding.CodeRefs, r => r is { Kind: "page", Value: "/clients/{ClientId:guid}" });
            Assert.True(finding.ImpactMs > 0);
            Assert.False(string.IsNullOrWhiteSpace(finding.Cause));
            Assert.False(string.IsNullOrWhiteSpace(finding.Action));
        }
    }

    [Fact]
    public void A_slow_page_with_too_few_samples_or_the_monitor_page_itself_is_no_finding()
    {
        Assert.Empty(PerformanceRules.Evaluate(Data([Series(PerfKind.Interaction, "/alerts", "event", 5, 4_000)])));
        Assert.Empty(PerformanceRules.Evaluate(Data([Series(PerfKind.Interaction, PerformanceRules.DashboardRoute, "load", 100, 4_000)])));
        var lower = PerformanceSettings.Default with { Thresholds = new Dictionary<string, double> { [PerfThresholds.MinSamples] = 5 } };
        Assert.NotNull(Find(Data([Series(PerfKind.Interaction, "/alerts", "event", 5, 4_000)], settings: lower), "code-slow-interaction:/alerts:event"));
    }

    [Fact]
    public void The_cause_of_a_slow_page_follows_where_its_time_went()
    {
        var database = Find(Data([Series(PerfKind.Interaction, "/alerts", "load", 100, 2_000, dbShare: 0.9, dbPerCall: 4)]), "code-slow-interaction:/alerts:load");
        Assert.StartsWith("Database work", database!.Cause);
        var own = Find(Data([Series(PerfKind.Interaction, "/alerts", "load", 100, 2_000, dbShare: 0.1, dbPerCall: 1)]), "code-slow-interaction:/alerts:load");
        Assert.StartsWith("Our own code", own!.Cause);
    }

    [Fact]
    public void Many_queries_per_interaction_is_a_finding_with_the_time_of_the_extra_queries_as_impact()
    {
        var finding = Find(Data([Series(PerfKind.Interaction, "/clients", "load", 50, 300, dbShare: 0.5, dbPerCall: 40)]), "code-many-queries:/clients:load");
        Assert.NotNull(finding);
        Assert.Equal(PerfSeverity.Warning, finding.Severity);
        // 40 commands per load against a threshold of 20: half of the database time is extra.
        Assert.Equal(50 * 300 * 0.5 / 2, finding.ImpactMs!.Value, 3);
        Assert.Null(Find(Data([Series(PerfKind.Interaction, "/clients", "load", 50, 300, dbPerCall: 10)]), "code-many-queries:/clients:load"));
    }

    [Fact]
    public void Slow_and_large_api_routes_are_found_but_a_download_is_never_large()
    {
        var data = Data([
            Series(PerfKind.Http, "/api/v1/endpoints", "GET 2xx", 100, 2_000, bytes: 100L * 2 * 1024 * 1024),
            Series(PerfKind.Http, "/agent/download/{platform}", "GET 2xx", 100, 200, bytes: 100L * 20 * 1024 * 1024)
        ]);
        Assert.NotNull(Find(data, "code-slow-route:/api/v1/endpoints:GET 2xx"));
        Assert.NotNull(Find(data, "code-large-response:/api/v1/endpoints"));
        Assert.Null(Find(data, "code-large-response:/agent/download/{platform}"));
    }

    [Fact]
    public void Failing_or_rate_limited_external_calls_are_found_per_host()
    {
        var data = Data([
            Series(PerfKind.External, "app.eu.action1.com", "2xx", 90, 300, component: "workers"),
            Series(PerfKind.External, "app.eu.action1.com", "429", 10, 100, component: "workers"),
            Series(PerfKind.External, "graph.microsoft.com", "2xx", 100, 100, component: "workers")
        ]);
        var finding = Find(data, "code-external-errors:app.eu.action1.com");
        Assert.NotNull(finding);
        Assert.Contains("429", finding.Title);
        Assert.Null(Find(data, "code-external-errors:graph.microsoft.com"));
    }

    [Fact]
    public void Runtime_problems_are_found_per_component()
    {
        var findings = PerformanceRules.Evaluate(Data(runtime: [Runtime("gateway", gc: 12, queue: 500, timeouts: 3, overheadMs: 60 * 60_000 * 4 * 0.05)]));
        Assert.Contains(findings, f => f is { Id: "code-db-pool-timeouts:gateway", Severity: PerfSeverity.Critical });
        Assert.Contains(findings, f => f.Id == "code-thread-pool:gateway");
        Assert.Contains(findings, f => f is { Id: "code-gc-pause:gateway", Severity: PerfSeverity.Critical });
        Assert.Contains(findings, f => f.Id == "code-monitor-overhead:gateway");
        Assert.Contains(PerformanceRules.Evaluate(Data(runtime: [Runtime("web", busy: 100, poolMax: 100)])), f => f.Id == "code-db-pool-full:web");
    }

    [Fact]
    public void Hosting_findings_name_the_vps_and_steal_says_code_changes_do_not_help()
    {
        var findings = PerformanceRules.Evaluate(Data(host: Host(cpu: 92, steal: 12, availableGb: 0, latency: 150, load: 12), runtime: [Runtime()]));
        Assert.Contains(findings, f => f.Id == "hosting-cpu-high");
        var steal = Assert.Single(findings, f => f.Id == "hosting-cpu-steal");
        Assert.Equal(PerfSeverity.Critical, steal.Severity);
        Assert.Contains("Code changes do not help", steal.Action);
        Assert.Contains(findings, f => f.Id == "hosting-memory-low");
        Assert.Contains(findings, f => f.Id == "hosting-disk-latency");
        Assert.Contains(findings, f => f.Id == "hosting-load");
        var cpu = findings.Single(f => f.Id == "hosting-cpu-high");
        Assert.Contains("outside this instance", cpu.Cause);
    }

    [Fact]
    public void The_statements_that_cost_most_are_found_and_ranked_by_their_total_time()
    {
        var statements = new List<PerfStatementDelta>
        {
            new(1, "SELECT * FROM \"Alerts\" WHERE \"ClientId\" = $1", 100_000, 50_000, 100_000, 0, 0),
            new(2, "SELECT * FROM \"CheckResults\" WHERE \"Time\" > $1", 20, 30_000, 2_000_000, 50_000, 10_000),
            new(3, "SELECT 1", 1_000, 10, 1_000, 0, 0)
        };
        var findings = PerformanceRules.Evaluate(Data(statements: statements));
        var top = findings.Where(f => f.Id.StartsWith("database-top-statement:", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "database-top-statement:1", "database-top-statement:2" }, top.Select(f => f.Id));
        Assert.Contains(top[1].CodeRefs, r => r is { Kind: "query", Detail: not null });
        Assert.Equal(PerfSeverity.Warning, top[1].Severity);
        Assert.Contains("runs very often", top[0].Cause);
    }

    [Fact]
    public void Sequential_scans_of_a_big_table_temp_files_and_dead_rows_are_found()
    {
        var tables = new List<PerfTableSummary>
        {
            new("Alerts", 2L << 30, 1L << 20, 2_000_000, 500, 500L * 2_000_000, 10, 2_000_000, 900_000, null, null, false, false, false, 0),
            new("Notes", 1L << 20, 0, 50_000, 500, 500L * 50_000, 10, 50_000, 10, null, null, false, false, false, 0)
        };
        var findings = PerformanceRules.Evaluate(Data(tables: tables,
            database: new PerfDatabaseSummary(true, 0.90, 10, 500L << 20, 0, 10, 100, 100_000, 10L << 30)));
        Assert.Contains(findings, f => f.Id == "database-seq-scan:Alerts");
        Assert.DoesNotContain(findings, f => f.Id == "database-seq-scan:Notes");
        Assert.Contains(findings, f => f.Id == "database-temp-files");
        Assert.Contains(findings, f => f is { Id: "maintenance-cache-hit", Severity: PerfSeverity.Critical });
        Assert.Contains(findings, f => f is { Id: "maintenance-dead-rows:Alerts", Severity: PerfSeverity.Warning });
    }

    [Fact]
    public void Without_a_baseline_the_database_deltas_are_no_findings()
    {
        var findings = PerformanceRules.Evaluate(Data(database: new PerfDatabaseSummary(false, null, null, null, null, 10, 100, 100, 1L << 30)));
        Assert.Empty(findings);
    }

    [Fact]
    public void A_hypertable_without_its_policies_is_found_only_where_timescale_is_installed()
    {
        PerfTableSummary Table(string name, bool hyper, bool compressed, bool retention) =>
            new(name, 1L << 30, null, 1_000_000, null, null, null, 1_000_000, 0, null, null, hyper, compressed, retention, hyper ? 10 : 0);

        var installed = PerformanceRules.Evaluate(Data(tables: [Table("CheckResults", true, false, true), Table("PerfMinutes", true, false, true)]));
        Assert.Contains(installed, f => f.Id == "maintenance-hypertable:CheckResults");
        Assert.DoesNotContain(installed, f => f.Id == "maintenance-hypertable:PerfMinutes");
        Assert.Empty(PerformanceRules.Evaluate(Data(tables: [Table("CheckResults", false, false, false)])));
    }

    [Fact]
    public void Missing_statement_statistics_say_how_to_get_them()
    {
        var finding = Find(Data(statementsProblem: "pg_stat_statements is not installed in this database."), "maintenance-statistics-missing");
        Assert.NotNull(finding);
        Assert.Contains("install.sh", finding.Action);
    }

    [Fact]
    public void A_loop_slower_than_its_interval_and_a_failing_loop_are_found()
    {
        var findings = PerformanceRules.Evaluate(Data([
            Series(PerfKind.WorkerRun, "patch-sync", "60", 30, 90_000, component: "workers"),
            Series(PerfKind.WorkerRun, "retention", "3600", 10, 2_000, errors: 5, component: "workers")
        ]));
        Assert.Contains(findings, f => f.Id == "background-loop-slow:patch-sync");
        Assert.DoesNotContain(findings, f => f.Id == "background-loop-slow:retention");
        Assert.Contains(findings, f => f is { Id: "background-loop-failures:retention", Severity: PerfSeverity.Warning });
    }

    [Fact]
    public void A_loop_that_slows_pages_down_needs_minutes_on_both_sides_and_an_absolute_gap()
    {
        var minutes = new List<PerfOverlapMinute>();
        for (var i = 0; i < 40; i++)
        {
            var withLoop = i % 2 == 0;
            minutes.Add(new PerfOverlapMinute(From.AddMinutes(i), Histogram((withLoop ? 1_200 : 150, 20)), 20,
                withLoop ? new HashSet<string> { "integration-sync", "check-evaluation" } : new HashSet<string> { "check-evaluation" }));
        }

        var findings = PerformanceRules.Evaluate(Data(minutes: minutes));
        var overlap = Assert.Single(findings, f => f.Id.StartsWith("background-loop-overlap:", StringComparison.Ordinal));
        Assert.Equal("background-loop-overlap:integration-sync", overlap.Id);
        Assert.True(overlap.ImpactMs > 0);

        // The same ratio at a few milliseconds is no evidence.
        var fast = minutes.Select(m => m with { InteractionHistogram = Histogram((m.Loops.Contains("integration-sync") ? 12 : 1.5, 20)) }).ToList();
        Assert.DoesNotContain(PerformanceRules.Evaluate(Data(minutes: fast)), f => f.Id.StartsWith("background-loop-overlap:", StringComparison.Ordinal));
        // Too few minutes without the loop.
        Assert.DoesNotContain(PerformanceRules.Evaluate(Data(minutes: minutes.Take(12).ToList())),
            f => f.Id.StartsWith("background-loop-overlap:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_storm_of_live_updates_and_slow_notifications_are_found()
    {
        var findings = PerformanceRules.Evaluate(Data([
            Series(PerfKind.Broadcast, "CheckResultsChanged", string.Empty, 6_000, 25),
            Series(PerfKind.Notification, "fleeto_check_results", "handled", 500, 3_000, component: "workers")
        ]));
        Assert.Contains(findings, f => f.Id == "background-broadcast:CheckResultsChanged");
        Assert.Contains(findings, f => f.Id == "background-notification-lag:workers:fleeto_check_results");
    }

    [Fact]
    public void The_agent_channel_rules_find_slow_ingest_slow_delivery_large_messages_and_storms()
    {
        var findings = PerformanceRules.Evaluate(Data([
            Series(PerfKind.AgentMessage, "CheckResults", "agent", 1_000, 3_000, dbShare: 0.8, component: "gateway", bytes: 1_000L * 300 * 1024),
            Series(PerfKind.AgentMessage, "Heartbeat", "agent", 1_000, 5, component: "gateway"),
            Series(PerfKind.JobDispatch, "Script", "delivered", 10, 60_000, component: "gateway"),
            Series(PerfKind.Connection, "accepted", string.Empty, 5_000, 0, component: "gateway", peak: 2_000)
        ]));
        Assert.Contains(findings, f => f is { Id: "agents-slow-message:CheckResults:agent", Severity: PerfSeverity.Critical });
        Assert.DoesNotContain(findings, f => f.Id.StartsWith("agents-slow-message:Heartbeat", StringComparison.Ordinal));
        Assert.Contains(findings, f => f.Id == "agents-payload:CheckResults:agent");
        Assert.Contains(findings, f => f.Id == "agents-job-delivery:Script");
        Assert.Contains(findings, f => f.Id == "agents-reconnect-storm");
    }

    [Fact]
    public void Findings_are_ranked_by_impact_so_a_frequent_small_delay_outranks_a_rare_long_one()
    {
        var findings = PerformanceRules.Evaluate(Data([
            Series(PerfKind.Interaction, "/rare", "load", 20, 30_000),
            Series(PerfKind.Interaction, "/frequent", "event", 100_000, 1_500)
        ]));
        Assert.Equal("code-slow-interaction:/frequent:event", findings[0].Id);
        Assert.Equal("code-slow-interaction:/rare:load", findings[1].Id);
    }

    [Fact]
    public void The_score_drops_per_finding_by_severity_and_never_below_zero()
    {
        var data = Data(runtime: [Runtime("a", gc: 12, queue: 500, timeouts: 3), Runtime("b", gc: 12, queue: 500, timeouts: 3)]);
        var findings = PerformanceRules.Evaluate(data);
        var code = PerformanceRules.Score(data, findings).Single(s => s.Info.Category == PerfCategory.Code);
        Assert.Equal(0, code.Score);
        Assert.Equal(findings.Count(f => f.Category == PerfCategory.Code), code.Findings);
    }

    [Fact]
    public void Numbers_in_findings_are_culture_invariant()
    {
        var culture = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("nl-BE");
            Assert.Equal("1.5 s", PerformanceRules.Ms(1_500));
            Assert.Equal("2.5%", PerformanceRules.Pct(2.5));
            Assert.Equal("1,234,567", PerformanceRules.Count(1_234_567));
            Assert.Equal("1.5 KB", PerformanceRules.Bytes(1_536));
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = culture;
        }
    }
}
