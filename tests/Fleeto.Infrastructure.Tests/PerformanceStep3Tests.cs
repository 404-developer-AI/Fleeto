using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The performance monitor, step 3 (0.7.0): what a browser may report passes a whitelist with ranges and caps, Web Vitals and
/// network time become findings above their thresholds and with enough samples, budgets are parsed with a reason for every wrong
/// line and judged against the period, comparisons find regressions against the previous period and the previous version, and
/// critical findings become problems of Instance health with a cooldown.
/// </summary>
public sealed class PerformanceStep3Tests
{
    private static readonly DateTime From = new(2026, 10, 26, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = From.AddHours(1);

    private static int[] Histogram(params (double Value, int Count)[] values)
    {
        var histogram = new int[PerfHistogram.BucketCount];
        foreach (var (value, count) in values)
        {
            histogram[PerfHistogram.BucketOf(value)] += count;
        }

        return PerfHistogram.Trim(histogram);
    }

    private static PerfSeries Series(PerfKind kind, string key, string subKey, int count, double value, string component = "web") =>
        new(component, kind, key, subKey, count, 0, value * count, value, Histogram((value, count)), 0, 0, 0, 0, count);

    private static PerfSeries Views(string route, int count) => new("web", PerfKind.Vital, route, PerfVitals.View, count, 0, 0, 0, [], 0, 0, 0, 0, count);

    private static PerformanceDataset Data(IReadOnlyList<PerfSeries> series, PerformanceSettings? settings = null, IReadOnlyList<PerfComparison>? comparisons = null) =>
        new(From, To, settings ?? PerformanceSettings.Default, series, [], null, [], null, null, [], [], [], [])
        {
            Comparisons = comparisons ?? []
        };

    private static string? RouteOf(string path) => path.StartsWith("/clients", StringComparison.Ordinal) ? "/clients/{ClientId:guid}" : path == "/" ? "/" : null;

    // ---- What a browser may report ----

    [Fact]
    public void A_report_keeps_only_known_metrics_in_range_on_known_pages()
    {
        var records = PerfVitals.Accept(
        [
            new("/clients/5f1c?tab=checks#x", "lcp", 1800),
            new("/clients/5f1c", "cls", 0.12),
            new("/clients/5f1c", "view", 0),
            new("/unknown", "lcp", 1000),
            new("/clients/5f1c", "password", 1),
            new("/clients/5f1c", "inp", -5),
            new("/clients/5f1c", "inp", double.NaN),
            new("/clients/5f1c", "lcp", 500_000),
            new("//evil.example/clients", "lcp", 100),
            new("https://evil.example/clients", "lcp", 100),
            new(null, "circuit", 42),
            new("/whatever", "connect", 120),
            new("/clients/5f1c", null, 1),
        ], RouteOf);

        Assert.Equal(5, records.Count);
        Assert.Contains(new PerfVitalRecord(PerfKind.Vital, "/clients/{ClientId:guid}", "lcp", 1800), records);
        Assert.Contains(new PerfVitalRecord(PerfKind.Vital, "/clients/{ClientId:guid}", "cls", 0.12 * PerfVitals.ClsScale), records);
        Assert.Contains(new PerfVitalRecord(PerfKind.Vital, "/clients/{ClientId:guid}", "view", 0), records);
        Assert.Contains(new PerfVitalRecord(PerfKind.Network, "circuit", "", 42), records);
        Assert.Contains(new PerfVitalRecord(PerfKind.Network, "connect", "", 120), records);
        Assert.DoesNotContain(records, r => r.Key.Contains("5f1c", StringComparison.Ordinal));
    }

    [Fact]
    public void A_report_is_cut_at_its_item_cap()
    {
        var samples = Enumerable.Range(0, 500).Select(_ => new PerfVitalSample("/", "long-task", 80)).ToList();
        Assert.Equal(PerfVitals.MaxItems, PerfVitals.Accept(samples, RouteOf).Count);
        Assert.Empty(PerfVitals.Accept(null, RouteOf));
    }

    [Fact]
    public void A_page_reports_in_Diagnose_always_and_otherwise_for_its_share_of_page_loads()
    {
        var now = From;
        var settings = PerformanceSettings.Default with { FrontendSamplePercent = 10 };
        Assert.True(settings.SamplesFrontend(now, 9.99));
        Assert.False(settings.SamplesFrontend(now, 10));
        Assert.True((settings with { DiagnoseUntil = now.AddMinutes(5) }).SamplesFrontend(now, 99));
        Assert.False((settings with { FrontendSamplePercent = 0 }).SamplesFrontend(now, 0));
        Assert.False((settings with { DisabledCollectors = [PerfCollector.Frontend] }).SamplesFrontend(now, 0));
        Assert.False((settings with { Level = PerfLevel.Off }).SamplesFrontend(now, 0));
    }

    [Fact]
    public void The_new_settings_are_checked_and_survive_storage()
    {
        Assert.Contains("between 0 and 100", (PerformanceSettings.Default with { FrontendSamplePercent = 101 }).Problem());
        Assert.Contains("1 to 168 hours", (PerformanceSettings.Default with { HealthCooldownHours = 0 }).Problem());
        Assert.NotNull((PerformanceSettings.Default with { Budgets = [new(PerfKind.Exception, "x", "", 10)] }).Problem());

        var settings = PerformanceSettings.Default with
        {
            FrontendSamplePercent = 25,
            HealthProblems = false,
            HealthCooldownHours = 6,
            Budgets = [new(PerfKind.Interaction, "/clients", "load", 800), new(PerfKind.Http, "/api/v1/endpoints", "GET", 300)]
        };
        Assert.Null(settings.Problem());
        var stored = PerformanceSettings.Parse(settings.Serialize());
        Assert.Equal(25, stored.FrontendSamplePercent);
        Assert.False(stored.HealthProblems);
        Assert.Equal(6, stored.HealthCooldownHours);
        Assert.Equal(settings.Budgets, stored.Budgets);

        // Settings stored before step 3 take the defaults.
        var old = PerformanceSettings.Parse("""{"level":"Basic"}""");
        Assert.Equal((10, true, 24, 0), (old.FrontendSamplePercent, old.HealthProblems, old.HealthCooldownHours, old.Budgets.Count));
        Assert.True(old.Collects(PerfCollector.Frontend));
    }

    // ---- Budgets ----

    [Fact]
    public void Budgets_are_read_one_per_line()
    {
        var (budgets, problem) = PerfBudgets.Parse("""
            # pages
            page /clients load = 800
            page /alerts=1500ms
            api GET /api/v1/endpoints = 300
            api /api/v1/alerts = 250.5
            loop patch-sync = 5000
            message CheckResults = 200
            """);
        Assert.Null(problem);
        Assert.Equal(new PerfBudget[]
        {
            new(PerfKind.Interaction, "/clients", "load", 800),
            new(PerfKind.Interaction, "/alerts", "", 1500),
            new(PerfKind.Http, "/api/v1/endpoints", "GET", 300),
            new(PerfKind.Http, "/api/v1/alerts", "", 250.5),
            new(PerfKind.WorkerRun, "patch-sync", "", 5000),
            new(PerfKind.AgentMessage, "CheckResults", "", 200),
        }, budgets);
        Assert.Equal(budgets, PerfBudgets.Parse(PerfBudgets.Format(budgets)).Budgets);
    }

    [Theory]
    [InlineData("page /clients 800", "Line 1")]
    [InlineData("route /clients = 800", "start with page")]
    [InlineData("page clients = 800", "starts with /")]
    [InlineData("page /clients load = fast", "number of milliseconds")]
    [InlineData("page /clients load = 0", "number of milliseconds")]
    [InlineData("page /clients other = 800", "page budget")]
    [InlineData("api GET /a /b = 800", "API budget")]
    [InlineData("loop a b = 800", "loop <name>")]
    [InlineData("loop sync = 1\nloop sync = 2", "more than one budget")]
    public void A_budget_that_cannot_be_read_says_why(string text, string expected)
    {
        var (budgets, problem) = PerfBudgets.Parse(text);
        Assert.Empty(budgets);
        Assert.Contains(expected, problem);
    }

    [Fact]
    public void A_budget_covers_its_flows_and_is_a_finding_when_they_go_over_it()
    {
        var settings = PerformanceSettings.Default with
        {
            Budgets =
            [
                new(PerfKind.Http, "/api/v1/endpoints", "GET", 300),
                new(PerfKind.Interaction, "/clients", "load", 800),
                new(PerfKind.WorkerRun, "patch-sync", "", 5000),
            ]
        };
        var data = Data(
        [
            Series(PerfKind.Http, "/api/v1/endpoints", "GET 2xx", 100, 500),
            Series(PerfKind.Http, "/api/v1/endpoints", "GET 4xx", 10, 20),
            Series(PerfKind.Http, "/api/v1/endpoints", "POST 2xx", 10, 9000),
            Series(PerfKind.Interaction, "/clients", "load", 50, 400),
            Series(PerfKind.Interaction, "/clients", "event", 50, 4000),
        ], settings);

        var results = PerformanceRules.BudgetResults(data);
        Assert.Equal(110, results[0].Count);
        Assert.False(results[0].Met);
        Assert.True(results[1].Met);
        Assert.False(results[2].Measured);
        Assert.True(results[2].Met);

        var findings = PerformanceRules.Evaluate(data).Where(f => f.Id.StartsWith("budget:", StringComparison.Ordinal)).ToList();
        var over = Assert.Single(findings);
        Assert.Equal("budget:api:/api/v1/endpoints:GET", over.Id);
        Assert.Equal(PerfCategory.Code, over.Category);
        Assert.Contains(over.CodeRefs, r => r is { Kind: "api", Value: "/api/v1/endpoints" });
        Assert.True(over.ImpactMs > 0);
    }

    // ---- Frontend and network ----

    [Fact]
    public void Web_vitals_above_their_thresholds_are_findings_with_enough_samples_only()
    {
        const string page = "/clients/{ClientId:guid}";
        var slow = Data(
        [
            Views(page, 40),
            Series(PerfKind.Vital, page, PerfVitals.Lcp, 40, 5_000),
            Series(PerfKind.Vital, page, PerfVitals.Inp, 40, 600),
            Series(PerfKind.Vital, page, PerfVitals.Cls, 40, 0.3 * PerfVitals.ClsScale),
            Series(PerfKind.Vital, page, PerfVitals.LongTask, 40, 400),
            Series(PerfKind.Interaction, page, "event", 40, 30),
        ]);
        var findings = PerformanceRules.Evaluate(slow).ToDictionary(f => f.Id);

        var lcp = findings[$"frontend-lcp:{page}"];
        Assert.Equal((PerfCategory.Frontend, PerfSeverity.Critical), (lcp.Category, lcp.Severity));
        Assert.Contains(lcp.CodeRefs, r => r is { Kind: "page", Value: page });
        Assert.Equal(PerfSeverity.Critical, findings[$"frontend-inp:{page}"].Severity);
        Assert.Contains("browser", findings[$"frontend-inp:{page}"].Cause, StringComparison.Ordinal);
        Assert.Equal(PerfSeverity.Warning, findings[$"frontend-cls:{page}"].Severity);
        Assert.Contains("layout shift 0.", findings[$"frontend-cls:{page}"].Title, StringComparison.Ordinal);
        Assert.True(findings.ContainsKey($"frontend-long-tasks:{page}"));

        var scores = PerformanceRules.Score(slow, findings.Values.ToList());
        Assert.True(scores.Single(s => s.Info.Category == PerfCategory.Frontend).Score < 100);

        var few = Data([Views(page, 3), Series(PerfKind.Vital, page, PerfVitals.Lcp, 3, 9_000)]);
        Assert.DoesNotContain(PerformanceRules.Evaluate(few), f => f.Category == PerfCategory.Frontend);

        var fast = Data([Views(page, 40), Series(PerfKind.Vital, page, PerfVitals.Lcp, 40, 900), Series(PerfKind.Vital, page, PerfVitals.Inp, 40, 80),
            Series(PerfKind.Vital, page, PerfVitals.Cls, 40, 20)]);
        Assert.DoesNotContain(PerformanceRules.Evaluate(fast), f => f.Category == PerfCategory.Frontend);
        Assert.Equal(100, PerformanceRules.Score(fast, []).Single(s => s.Info.Category == PerfCategory.Frontend).Score);
    }

    [Fact]
    public void The_monitors_own_page_is_never_a_frontend_finding()
    {
        var data = Data([Views(PerformanceRules.DashboardRoute, 40), Series(PerfKind.Vital, PerformanceRules.DashboardRoute, PerfVitals.Lcp, 40, 20_000)]);
        Assert.Empty(PerformanceRules.Vitals(data));
        Assert.DoesNotContain(PerformanceRules.Evaluate(data), f => f.Category == PerfCategory.Frontend);
    }

    [Fact]
    public void A_slow_live_connection_and_a_slow_connection_setup_are_network_findings()
    {
        var data = Data([Series(PerfKind.Network, PerfVitals.CircuitRoundTrip, "", 100, 400), Series(PerfKind.Network, PerfVitals.Connect, "", 20, 900)]);
        var findings = PerformanceRules.Evaluate(data);
        var circuit = Assert.Single(findings, f => f.Id == "network-circuit-round-trip");
        Assert.Equal(PerfCategory.Network, circuit.Category);
        Assert.Contains("closer to its users", circuit.Action, StringComparison.Ordinal);
        Assert.Single(findings, f => f.Id == "network-connect");

        var quick = Data([Series(PerfKind.Network, PerfVitals.CircuitRoundTrip, "", 100, 40)]);
        Assert.DoesNotContain(PerformanceRules.Evaluate(quick), f => f.Category == PerfCategory.Network);
        Assert.Equal(100, PerformanceRules.Score(quick, []).Single(s => s.Info.Category == PerfCategory.Network).Score);
    }

    // ---- Comparisons ----

    [Fact]
    public void A_comparison_holds_flows_with_enough_samples_on_both_sides_largest_change_first()
    {
        var before = new[]
        {
            Series(PerfKind.Interaction, "/clients", "load", 100, 200),
            Series(PerfKind.Interaction, "/alerts", "load", 100, 200),
            Series(PerfKind.WorkerRun, "patch-sync", "60", 5, 100, "workers"),
            Series(PerfKind.Db, "other", "", 100, 1),
        };
        var now = new[]
        {
            Series(PerfKind.Interaction, "/clients", "load", 100, 500),
            Series(PerfKind.Interaction, "/alerts", "load", 100, 210),
            Series(PerfKind.WorkerRun, "patch-sync", "60", 100, 900, "workers"),
            Series(PerfKind.Db, "other", "", 100, 50),
            Series(PerfKind.Interaction, "/new", "load", 100, 50),
        };
        var rows = PerfComparison.Compare(before, now, 20);
        Assert.Equal(new[] { "/clients", "/alerts" }, rows.Select(r => r.Key));
        Assert.True(rows[0].Change > 1);
    }

    [Fact]
    public void A_regression_against_the_previous_period_or_version_is_a_finding()
    {
        PerfComparisonRow Row(double before, double now) => new(PerfKind.Interaction, "/clients", "load", 100, before, 100, now);
        var comparisons = new List<PerfComparison>
        {
            new(PerfComparisonKind.PreviousPeriod, "previous period", "this period", From.AddHours(-1), From, From, To, [Row(200, 500)]),
            new(PerfComparisonKind.Version, "0.7.0-alpha.12", "0.7.0-alpha.13", From, From.AddMinutes(30), From.AddMinutes(30), To, [Row(200, 260)]),
        };
        var findings = PerformanceRules.Evaluate(Data([], comparisons: comparisons)).Where(f => f.Id.StartsWith("regression:", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, findings.Count);
        var period = findings.Single(f => f.Id.StartsWith("regression:PreviousPeriod", StringComparison.Ordinal));
        Assert.Equal(PerfSeverity.Warning, period.Severity);
        var version = findings.Single(f => f.Id.StartsWith("regression:Version", StringComparison.Ordinal));
        Assert.Equal(PerfSeverity.Info, version.Severity);
        Assert.Contains("version 0.7.0-alpha.12", version.Title, StringComparison.Ordinal);

        // Slower by a share, but only a few milliseconds: not worth a finding.
        var small = new List<PerfComparison> { new(PerfComparisonKind.PreviousPeriod, "a", "b", From, From, From, To, [Row(10, 25)]) };
        Assert.DoesNotContain(PerformanceRules.Evaluate(Data([], comparisons: small)), f => f.Id.StartsWith("regression:", StringComparison.Ordinal));
    }

    // ---- Instance health ----

    private static PerfFinding Finding(string id, PerfSeverity severity) =>
        new(id, PerfCategory.Code, severity, $"Title of {id}", "What it means.", [], "Cause.", "Do this.", [], 100);

    [Fact]
    public void Critical_findings_become_warnings_of_instance_health_unless_they_cool_down()
    {
        var findings = new[]
        {
            Finding("code-slow-interaction:/clients/{ClientId:guid}:load", PerfSeverity.Critical),
            Finding("network-circuit-round-trip", PerfSeverity.Critical),
            Finding("code-slow-route:/api/v1/alerts:GET 2xx", PerfSeverity.Warning),
        };
        var cooling = new HashSet<string>(StringComparer.Ordinal) { PerformanceHealth.KeyOf("network-circuit-round-trip") };
        var problem = Assert.Single(PerformanceHealth.Problems(findings, cooling));
        Assert.Equal(PerformanceHealth.KeyOf(findings[0].Id), problem.Key);
        Assert.Equal((HealthComponent.Performance, AlertSeverity.Warning), (problem.Component, problem.Severity));
        Assert.StartsWith("Performance: Title of", problem.Title, StringComparison.Ordinal);
        Assert.Contains("Do this.", problem.Detail, StringComparison.Ordinal);
        Assert.True(problem.Key.Length <= 100);
        Assert.Equal(PerformanceHealth.KeyOf("x"), PerformanceHealth.KeyOf("x"));

        var many = Enumerable.Range(0, 20).Select(i => Finding($"f{i}", PerfSeverity.Critical));
        Assert.Equal(PerformanceHealth.MaxProblems, PerformanceHealth.Problems(many, new HashSet<string>()).Count);
    }

    // ---- The report ----

    [Fact]
    public void The_report_holds_the_browser_budgets_comparisons_and_probes()
    {
        const string page = "/clients/{ClientId:guid}";
        var settings = PerformanceSettings.Default with { Budgets = [new(PerfKind.Interaction, page, "load", 100)] };
        var data = Data(
            [Views(page, 40), Series(PerfKind.Vital, page, PerfVitals.Lcp, 40, 3_000), Series(PerfKind.Network, PerfVitals.CircuitRoundTrip, "", 50, 80),
                Series(PerfKind.Interaction, page, "load", 40, 300)],
            settings,
            [new(PerfComparisonKind.PreviousPeriod, "previous period", "this period", From.AddHours(-1), From, From, To,
                [new(PerfKind.Interaction, page, "load", 40, 100, 40, 300)])]) with
        {
            Probes = [new PerfProbeView(Guid.NewGuid(), "EdgeCheck", From, From.AddSeconds(5), null, [new("HTTP version", "HTTP/2", true)])]
        };
        var findings = PerformanceRules.Evaluate(data);
        var markdown = PerformanceReport.Markdown(data, findings, PerformanceRules.Score(data, findings), To, "0.7.0", PerfRedactor.Default);
        Assert.Contains("## Frontend: Web Vitals per page", markdown, StringComparison.Ordinal);
        Assert.Contains("## Network", markdown, StringComparison.Ordinal);
        Assert.Contains("over budget", markdown, StringComparison.Ordinal);
        Assert.Contains("### This period against the previous one", markdown, StringComparison.Ordinal);
        Assert.Contains("- HTTP version: HTTP/2", markdown, StringComparison.Ordinal);
        Assert.DoesNotContain("comes with 0.7.0 step 3", markdown, StringComparison.Ordinal);

        var json = PerformanceReport.Json(data, findings, PerformanceRules.Score(data, findings), To, "0.7.0", PerfRedactor.Default);
        Assert.Contains("\"budgets\"", json, StringComparison.Ordinal);
        Assert.Contains("\"comparisons\"", json, StringComparison.Ordinal);
        Assert.Contains("\"probes\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Probe_results_survive_storage_and_unreadable_ones_are_empty()
    {
        IReadOnlyList<PerfProbeItem> items = [new("TLS version", "TLS 1.3", true), new("DNS", "4 ms", null, "advice")];
        Assert.Equal(items, PerformanceReader.ParseItems(PerformanceReader.SerializeItems(items)));
        Assert.Empty(PerformanceReader.ParseItems("{not json"));
        var view = PerformanceReader.ToView(new PerfProbe { Id = Guid.NewGuid(), Kind = PerfProbeKind.Benchmark, RequestedAt = From });
        Assert.True(view.Running);
        Assert.Equal("Benchmark", view.Kind);
    }
}
