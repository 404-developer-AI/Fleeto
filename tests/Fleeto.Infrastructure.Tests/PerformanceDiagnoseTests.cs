using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;
using Microsoft.Extensions.Time.Testing;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Diagnose of the performance monitor (0.7.0 step 2, PERFORMANCE.md §4.2) without a database: it runs until its end on server
/// time and never as a stored level, every statement gets one fingerprint and its caller under a budget, a unit of work that sends
/// one statement too often is an N+1 suspect, the rules name the statement and the method, the planner only plans reading
/// statements, and the exports say how much of the period ran in Diagnose.
/// </summary>
public sealed class PerformanceDiagnoseTests
{
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 30, DateTimeKind.Utc);

    private static (PerfRecorder Recorder, FakeTimeProvider Time) Diagnosing(int repeats = 10, int slowEventMs = 1000)
    {
        var time = new FakeTimeProvider(new DateTimeOffset(Now));
        var settings = PerformanceSettings.Default with
        {
            DiagnoseUntil = Now.AddMinutes(30),
            SlowEventMs = slowEventMs,
            Thresholds = new Dictionary<string, double> { [PerfThresholds.NPlusOne] = repeats }
        };
        return (new PerfRecorder("web", new PerfSettingsCache(settings), time), time);
    }

    [Fact]
    public void Diagnose_runs_until_its_end_on_server_time_and_is_never_a_level_of_its_own()
    {
        var settings = PerformanceSettings.Default with { DiagnoseUntil = Now.AddMinutes(60) };
        Assert.True(settings.IsDiagnosing(Now));
        Assert.Equal(PerfLevel.Diagnose, settings.EffectiveLevel(Now));
        Assert.False(settings.IsDiagnosing(Now.AddMinutes(60)));
        Assert.Equal(PerfLevel.Basic, settings.EffectiveLevel(Now.AddMinutes(61)));
        Assert.False((settings with { Level = PerfLevel.Off }).IsDiagnosing(Now));

        // Faster snapshots while it runs, the usual ones after.
        Assert.Equal((5, 15), (settings.StatementSnapshotMinutesAt(Now), settings.TableSnapshotMinutesAt(Now)));
        Assert.Equal((15, 60), (settings.StatementSnapshotMinutesAt(Now.AddHours(2)), settings.TableSnapshotMinutesAt(Now.AddHours(2))));

        Assert.Contains("Off or Basic", (PerformanceSettings.Default with { Level = PerfLevel.Diagnose }).Problem());
        Assert.NotNull((PerformanceSettings.Default with { DiagnoseDefaultMinutes = 300, DiagnoseMaxMinutes = 240 }).Problem());
        Assert.NotNull((PerformanceSettings.Default with { DiagnoseMaxMinutes = PerformanceSettings.MaxDiagnoseMinutes + 1 }).Problem());
        var stored = PerformanceSettings.Parse(settings.Serialize());
        Assert.Equal(settings.DiagnoseUntil, stored.DiagnoseUntil);
    }

    [Fact]
    public void Statements_that_differ_only_in_values_share_one_fingerprint_and_the_caps_hold()
    {
        var statements = new PerfStatements();
        var a = statements.Of("""SELECT * FROM "Clients" WHERE "Code" = 'ACME'""")!;
        var b = statements.Of("""SELECT * FROM "Clients" WHERE "Code" = 'GLOBEX'""")!;
        Assert.Same(a, b);
        Assert.Equal(16, a.Hash.Length);
        Assert.DoesNotContain("ACME", a.Sql);
        Assert.Null(statements.Of("   "));

        for (var i = 0; i < PerfStatements.MaxFingerprints + 10; i++)
        {
            statements.Of($"SELECT \"Column{i}\" FROM \"Clients\"");
        }

        Assert.Null(statements.Of("""SELECT "Never" FROM "Seen" """));
        Assert.True(statements.TakeSeen().Count <= PerfStatements.MaxFingerprints);
        Assert.Empty(statements.TakeSeen());
    }

    [Fact]
    public void The_caller_is_the_first_method_of_Fleeto_on_the_stack_and_walks_stay_within_the_budget()
    {
        Assert.Equal($"{typeof(PerformanceDiagnoseTests).FullName}.{nameof(The_caller_is_the_first_method_of_Fleeto_on_the_stack_and_walks_stay_within_the_budget)}",
            PerfStatements.CallerOf(new StackTrace()));
        Assert.Equal($"{typeof(PerformanceDiagnoseTests).FullName}.{nameof(CallerInLambda)}", CallerInLambda());

        var statements = new PerfStatements();
        var captured = 0;
        for (var i = 0; i < PerfStatements.CallerCapturesPerSecond + 20; i++)
        {
            var fingerprint = statements.Of($"SELECT \"C{i}\" FROM \"T\"")!;
            statements.CaptureCaller(fingerprint, nowSeconds: 1000);
            captured += fingerprint.Callers.Count;
        }

        Assert.Equal(PerfStatements.CallerCapturesPerSecond, captured);
        var next = statements.Of("""SELECT "C0" FROM "T" """)!;
        statements.CaptureCaller(next, nowSeconds: 1001);
        Assert.Single(next.Callers);
    }

    private static string? CallerInLambda()
    {
        Func<string?> find = () => PerfStatements.CallerOf(new StackTrace());
        return find();
    }

    [Fact]
    public void Outside_Diagnose_a_statement_is_not_recorded_and_costs_one_check()
    {
        var recorder = new PerfRecorder("web", new PerfSettingsCache(), TimeProvider.System);
        Assert.False(recorder.Diagnosing);
        Assert.Null(recorder.BeginStatement("""SELECT 1"""));

        var (diagnosing, time) = Diagnosing();
        Assert.True(diagnosing.Diagnosing);
        time.Advance(TimeSpan.FromMinutes(31));
        Assert.False(diagnosing.Diagnosing);
        Assert.Null(diagnosing.BeginStatement("""SELECT 1"""));
    }

    [Fact]
    public void A_statement_sent_too_often_in_one_unit_of_work_is_an_N_plus_one_suspect_and_a_slow_event_keeps_its_heaviest_statements()
    {
        var (recorder, _) = Diagnosing(repeats: 10, slowEventMs: 100);
        var scope = PerfContext.Begin("interaction:/clients");
        PerfStatementStart? first = null;
        for (var i = 0; i < 12; i++)
        {
            var start = recorder.BeginStatement($"""SELECT * FROM "Sites" WHERE "ClientId" = {i}""")!;
            first ??= start;
            recorder.EndStatement(start, 2, failed: false);
        }

        recorder.EndStatement(recorder.BeginStatement("""SELECT * FROM "Clients" """)!, 50, failed: false);
        Assert.Equal("interaction:/clients", first!.Source);
        Thread.Sleep(120);
        recorder.Complete(PerfCollector.Interactions, PerfKind.Interaction, "/clients", "load", scope, error: false);

        var rows = recorder.Swap(DateTime.UtcNow).Rows();
        var statement = rows.Single(r => r.Series.Kind == PerfKind.Statement && r.Series.Key == first.Fingerprint.Hash);
        Assert.Equal(("interaction:/clients", 12), (statement.Series.SubKey, statement.Count));
        var suspect = Assert.Single(rows, r => r.Series.Kind == PerfKind.NPlusOne);
        Assert.Equal(("interaction:/clients", first.Fingerprint.Hash, 1, 12d, 12), (suspect.Series.Key, suspect.Series.SubKey, suspect.Count, suspect.Sum, suspect.DbCount));

        var slow = Assert.Single(recorder.TakeEvents());
        var heaviest = PerformanceReader.ParseStatements(slow.Statements);
        Assert.Equal(2, heaviest.Count);
        Assert.Equal((50d, 1), (heaviest[0].Ms, heaviest[0].Count));
        Assert.Equal((first.Fingerprint.Hash, 12), (heaviest[1].Hash, heaviest[1].Count));
    }

    [Fact]
    public void An_exception_names_the_class_that_threw_it_not_a_compiler_generated_one()
    {
        Exception? caught = null;
        try
        {
            Action thrower = () => throw new InvalidOperationException("never in a key");
            thrower();
        }
        catch (InvalidOperationException ex)
        {
            caught = ex;
        }

        Assert.Equal(typeof(PerformanceDiagnoseTests).FullName, PerfMeterListener.Thrower(caught!));
        Assert.Equal("interaction", PerfMeterListener.SourceKind("interaction:/clients/{ClientId:guid}"));
        Assert.Equal("other", PerfMeterListener.SourceKind("other"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Rules
    // ---------------------------------------------------------------------------------------------------------------

    private static PerfSeries Series(PerfKind kind, string key, string subKey, long count, double sum, double max, double dbMs = 0, string component = "web")
    {
        var histogram = new int[PerfHistogram.BucketCount];
        histogram[PerfHistogram.BucketOf(count == 0 ? 0 : sum / count)] = (int)count;
        return new PerfSeries(component, kind, key, subKey, count, 0, sum, max, PerfHistogram.Trim(histogram), 0, 0, dbMs, 0, count);
    }

    private static PerformanceDataset Data(IReadOnlyList<PerfSeries> series, PerfDatabaseSummary? database = null, IReadOnlyList<PerfIndexView>? indexes = null,
        IReadOnlyList<PerfRuntimeSummary>? runtime = null) =>
        new(Now.AddHours(-1), Now, PerformanceSettings.Default, series, runtime ?? [], null, [], null, database, [], [], [], [])
        {
            Fingerprints = new Dictionary<string, PerfFingerprintView>
            {
                ["a1b2c3d4e5f60718"] = new("a1b2c3d4e5f60718", """SELECT * FROM "Sites" WHERE "ClientId" = ?""", ["Fleeto.Web.Services.SiteService.ListAsync"])
            },
            DiagnoseMinutes = 60,
            Indexes = indexes ?? []
        };

    [Fact]
    public void An_N_plus_one_suspect_names_the_page_the_statement_and_the_method()
    {
        var findings = PerformanceRules.Evaluate(Data([Series(PerfKind.NPlusOne, "interaction:/clients", "a1b2c3d4e5f60718", 20, 20 * 40, 60, dbMs: 800)]));
        var finding = Assert.Single(findings, f => f.Id == "code-n-plus-one:interaction:/clients:a1b2c3d4e5f60718");
        Assert.Equal(PerfSeverity.Warning, finding.Severity);
        Assert.Contains(finding.CodeRefs, r => r is { Kind: "page", Value: "/clients" });
        Assert.Contains(finding.CodeRefs, r => r.Kind == "statement" && r.Detail!.Contains("\"Sites\"", StringComparison.Ordinal));
        Assert.Contains(finding.CodeRefs, r => r is { Kind: "method", Value: "Fleeto.Web.Services.SiteService.ListAsync" });
        Assert.InRange(finding.ImpactMs!.Value, 700, 800);

        // Twice and just over the threshold is not yet a pattern; the monitor's own page never is.
        Assert.DoesNotContain(PerformanceRules.Evaluate(Data([Series(PerfKind.NPlusOne, "interaction:/clients", "a1b2c3d4e5f60718", 2, 24, 12)])),
            f => f.Id.StartsWith("code-n-plus-one", StringComparison.Ordinal));
        Assert.DoesNotContain(PerformanceRules.Evaluate(Data([Series(PerfKind.NPlusOne, PerfKeys.DashboardSource, "a1b2c3d4e5f60718", 50, 5000, 100)])),
            f => f.Id.StartsWith("code-n-plus-one", StringComparison.Ordinal));
    }

    [Fact]
    public void The_costliest_measured_statement_is_a_finding_with_its_senders_and_caller()
    {
        var findings = PerformanceRules.Evaluate(Data(
        [
            Series(PerfKind.Statement, "a1b2c3d4e5f60718", "interaction:/clients", 1000, 40_000, 400),
            Series(PerfKind.Statement, "a1b2c3d4e5f60718", "worker:patch-sync", 500, 20_000, 300, component: "workers"),
            Series(PerfKind.Statement, "ffffffffffffffff", "interaction:/alerts", 100, 1_000, 30)
        ]));
        var finding = Assert.Single(findings, f => f.Id == "database-app-statement:a1b2c3d4e5f60718");
        Assert.Contains(finding.Evidence, e => e.Label == "Sent by" && e.Value.Contains("interaction:/clients", StringComparison.Ordinal) && e.Value.Contains("worker:patch-sync", StringComparison.Ordinal));
        Assert.Contains(finding.CodeRefs, r => r is { Kind: "loop", Value: "patch-sync" });
        Assert.Contains(finding.CodeRefs, r => r.Kind == "method");
        Assert.DoesNotContain(findings, f => f.Id == "database-app-statement:ffffffffffffffff");

        // One slow call is not a pattern.
        Assert.DoesNotContain(PerformanceRules.Evaluate(Data([Series(PerfKind.Statement, "a1b2c3d4e5f60718", "interaction:/clients", 1, 128, 128)])),
            f => f.Id.StartsWith("database-app-statement", StringComparison.Ordinal));
    }

    [Fact]
    public void Sessions_idle_in_a_transaction_or_waiting_for_a_lock_and_duplicate_indexes_are_findings()
    {
        var database = new PerfDatabaseSummary(true, 0.999, 0, 0, 0, 10, 100, 1000, 1L << 30)
        {
            Sessions = new PerfSessionsSummary(3, 10, 2, 900, 700, 1, 30)
        };
        var findings = PerformanceRules.Evaluate(Data([], database,
        [
            new PerfIndexView("Jobs", "IX_Jobs_EndpointId", 1 << 20, 10, false, false, "EndpointId", null),
            new PerfIndexView("Jobs", "IX_Jobs_EndpointId1", 1 << 20, 0, false, false, "EndpointId", "IX_Jobs_EndpointId")
        ]));
        Assert.Equal(PerfSeverity.Critical, findings.Single(f => f.Id == "code-idle-in-transaction").Severity);
        Assert.Equal(PerfSeverity.Warning, findings.Single(f => f.Id == "database-lock-waits").Severity);
        Assert.Single(findings, f => f.Id == "database-duplicate-index:IX_Jobs_EndpointId1");

        var quiet = database with { Sessions = new PerfSessionsSummary(3, 10, 1, 5, 5, 0, null) };
        Assert.DoesNotContain(PerformanceRules.Evaluate(Data([], quiet)), f => f.Id is "code-idle-in-transaction" or "database-lock-waits");
    }

    [Fact]
    public void Diagnose_may_cost_three_times_the_overhead_of_Basic()
    {
        PerfRuntimeSummary Runtime(int diagnoseMinutes) =>
            new("web", 60, "0.7.0", 5, 10, 1 << 28, 1 << 26, 0.5, 1 << 30, 20, 0, 0, 0, 3, 5, 2, 100, 0, 60 * 60_000 * 0.02 * 4, 4) { DiagnoseMinutes = diagnoseMinutes };

        Assert.Contains(PerformanceRules.Evaluate(Data([], runtime: [Runtime(0)])), f => f.Id == "code-monitor-overhead:web");
        Assert.DoesNotContain(PerformanceRules.Evaluate(Data([], runtime: [Runtime(60)])), f => f.Id == "code-monitor-overhead:web");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Planner
    // ---------------------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("""SELECT * FROM "Sites" WHERE "ClientId" = $1 AND "Name" = ? LIMIT ?""", """SELECT * FROM "Sites" WHERE "ClientId" = $1 AND "Name" = $2 LIMIT $3""")]
    [InlineData("""SELECT "Id" FROM "Jobs" WHERE "State" = ? FOR UPDATE SKIP LOCKED;""", """SELECT "Id" FROM "Jobs" WHERE "State" = $1 FOR UPDATE SKIP LOCKED""")]
    [InlineData("""WITH t AS (SELECT "Id" FROM "Alerts") SELECT count(*) FROM t""", """WITH t AS (SELECT "Id" FROM "Alerts") SELECT count(*) FROM t""")]
    [InlineData("""SELECT "Update", "Delete?" FROM "Odd" WHERE "x" = ?""", """SELECT "Update", "Delete?" FROM "Odd" WHERE "x" = $1""")]
    [InlineData("""SELECT "Id" FROM "Jobs" WHERE "At" < @since AND "Tags" @> ? AND "End" < @since""", """SELECT "Id" FROM "Jobs" WHERE "At" < $1 AND "Tags" @> $2 AND "End" < $1""")]
    public void A_reading_statement_is_prepared_with_its_literals_as_parameters(string normalized, string expected)
    {
        var (sql, problem) = PerformancePlanner.Prepare(normalized);
        Assert.Null(problem);
        Assert.Equal(expected, sql);
    }

    [Theory]
    [InlineData("""UPDATE "Endpoints" SET "LastSeenAt" = $1""")]
    [InlineData("""DELETE FROM "Alerts" WHERE "Id" = ?""")]
    [InlineData("""WITH gone AS (DELETE FROM "Alerts" RETURNING "Id") SELECT count(*) FROM gone""")]
    [InlineData("SELECT 1; DROP TABLE \"Clients\"")]
    [InlineData("SELECT * INTO \"Copy\" FROM \"Clients\"")]
    [InlineData("VACUUM \"Clients\"")]
    [InlineData("")]
    public void Anything_that_could_change_data_is_never_planned(string normalized)
    {
        var (sql, problem) = PerformancePlanner.Prepare(normalized);
        Assert.Null(sql);
        Assert.False(string.IsNullOrWhiteSpace(problem));
    }

    [Fact]
    public void The_nodes_of_a_plan_are_read_depth_first_and_full_reads_are_called_out()
    {
        const string json = """
            [{"Plan": {"Node Type": "Nested Loop", "Total Cost": 100.5, "Plan Rows": 10, "Plans": [
              {"Node Type": "Seq Scan", "Relation Name": "Sites", "Total Cost": 80, "Plan Rows": 1000},
              {"Node Type": "Index Scan", "Relation Name": "Clients", "Index Name": "PK_Clients", "Total Cost": 8, "Plan Rows": 1}]}}]
            """;
        var nodes = PerformancePlanner.Nodes(json);
        Assert.Equal(["Nested Loop", "Seq Scan", "Index Scan"], nodes.Select(n => n.NodeType));
        Assert.Equal([0, 1, 1], nodes.Select(n => n.Depth));
        Assert.Equal(["Sites"], new PerfPlan(json, nodes, null).SequentialScans);
        Assert.Empty(PerformancePlanner.Nodes("not json"));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Exports
    // ---------------------------------------------------------------------------------------------------------------

    [Fact]
    public void The_exports_say_how_much_ran_in_Diagnose_and_carry_the_statements_and_callers_redacted()
    {
        var data = Data(
        [
            Series(PerfKind.Statement, "a1b2c3d4e5f60718", "interaction:/clients", 1000, 40_000, 400),
            Series(PerfKind.NPlusOne, "interaction:/clients", "a1b2c3d4e5f60718", 20, 800, 60, dbMs: 800)
        ]) with { };
        data = data with
        {
            Fingerprints = new Dictionary<string, PerfFingerprintView>
            {
                ["a1b2c3d4e5f60718"] = new("a1b2c3d4e5f60718", """SELECT * FROM "Sites" WHERE "Name" = 'ACME-HQ'""", ["Fleeto.Web.Services.SiteService.ListAsync"])
            },
            DiagnoseMinutes = 30
        };
        var findings = PerformanceRules.Evaluate(data);
        var scores = PerformanceRules.Score(data, findings);
        var redactor = PerfRedactor.ForInstance(["ACME"], ["acme-hq"]);

        var markdown = PerformanceReport.Markdown(data, findings, scores, Now, "0.7.0-alpha.2", redactor);
        Assert.Contains("share of the period in Diagnose: 50% (30 minutes)", markdown);
        Assert.Contains("## Statements measured in Diagnose", markdown);
        Assert.Contains("## N+1 suspects", markdown);
        Assert.Contains("`Fleeto.Web.Services.SiteService.ListAsync`", markdown);
        Assert.DoesNotContain("ACME", markdown, StringComparison.OrdinalIgnoreCase);

        var json = PerformanceReport.Json(data, findings, scores, Now, "0.7.0-alpha.2", redactor);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(PerformanceReport.JsonSchemaVersion, document.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(30, document.RootElement.GetProperty("period").GetProperty("diagnoseMinutes").GetInt32());
        Assert.Equal("Fleeto.Web.Services.SiteService.ListAsync",
            document.RootElement.GetProperty("fingerprints")[0].GetProperty("callers")[0].GetString());
        Assert.DoesNotContain("ACME", json, StringComparison.OrdinalIgnoreCase);

        var zip = PerformanceReport.Zip(markdown, json, [new PerformanceReport.PlanFile("fingerprint-a1b2c3d4e5f60718", "[]")]);
        using var archive = new ZipArchive(new MemoryStream(zip));
        Assert.Equal(["fleeto-performance.md", "fleeto-performance.json", "query-plans/fingerprint-a1b2c3d4e5f60718.json"],
            archive.Entries.Select(e => e.FullName));
        using var reader = new StreamReader(archive.GetEntry("fleeto-performance.md")!.Open(), Encoding.UTF8);
        Assert.Equal(markdown, reader.ReadToEnd());
    }
}
