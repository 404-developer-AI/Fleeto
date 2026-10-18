using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The pure building blocks of the performance monitor (0.7.0, PERFORMANCE.md §5): histograms that add up and give percentiles exact
/// to the bucket, keys that stay bounded, SQL that keeps no value, a redactor that masks what slips through, route templates
/// instead of URLs, and settings that are checked whatever the page sent.
/// </summary>
public sealed class PerformanceCoreTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.1, 0)]
    [InlineData(0.11, 1)]
    [InlineData(1, 4)]
    [InlineData(1.01, 5)]
    [InlineData(300_000, 36)]
    [InlineData(300_001, 37)]
    [InlineData(double.MaxValue, 37)]
    public void A_value_falls_in_the_bucket_whose_upper_bound_it_does_not_exceed(double ms, int bucket)
    {
        Assert.Equal(bucket, PerfHistogram.BucketOf(ms));
        Assert.Equal(38, PerfHistogram.BucketCount);
    }

    [Fact]
    public void Histograms_add_up_and_the_sum_gives_the_same_percentiles_as_one_histogram_of_everything()
    {
        var random = new Random(7);
        var all = new int[PerfHistogram.BucketCount];
        int[]? total = null;
        for (var part = 0; part < 10; part++)
        {
            var histogram = new int[PerfHistogram.BucketCount];
            for (var i = 0; i < 500; i++)
            {
                var value = Math.Exp(random.NextDouble() * 9);
                histogram[PerfHistogram.BucketOf(value)]++;
                all[PerfHistogram.BucketOf(value)]++;
            }

            total = PerfHistogram.Add(total, PerfHistogram.Trim(histogram));
        }

        Assert.Equal(all, PerfHistogram.Add(new int[PerfHistogram.BucketCount], total));
        Assert.Equal(5000, PerfHistogram.Total(total));
        Assert.Equal(PerfHistogram.Percentile(all, 0.95), PerfHistogram.Percentile(total, 0.95));
    }

    [Fact]
    public void A_percentile_lies_within_the_bucket_of_the_true_value_and_never_above_the_largest_value()
    {
        var values = Enumerable.Range(1, 1000).Select(i => (double)i).ToList();
        var histogram = new int[PerfHistogram.BucketCount];
        foreach (var value in values)
        {
            histogram[PerfHistogram.BucketOf(value)]++;
        }

        var p95 = PerfHistogram.Percentile(histogram, 0.95, max: 1000)!.Value;
        Assert.InRange(p95, PerfHistogram.LowerBound(PerfHistogram.BucketOf(950)), PerfHistogram.UpperBound(PerfHistogram.BucketOf(950)));
        Assert.True(PerfHistogram.Percentile(histogram, 1, max: 1000) <= 1000);
        Assert.Null(PerfHistogram.Percentile([], 0.5));
        Assert.Null(PerfHistogram.Percentile(null, 0.5));
    }

    [Fact]
    public void Trimming_drops_trailing_zeros_only()
    {
        Assert.Equal(new[] { 0, 3, 0, 1 }, PerfHistogram.Trim([0, 3, 0, 1, 0, 0]));
        Assert.Empty(PerfHistogram.Trim([0, 0]));
    }

    [Fact]
    public void Time_above_a_threshold_counts_only_the_part_above_it()
    {
        var histogram = new int[PerfHistogram.BucketCount];
        histogram[PerfHistogram.BucketOf(10)] = 100;
        histogram[PerfHistogram.BucketOf(2_000)] = 10;
        var above = PerfHistogram.TimeAbove(histogram, 1_000);
        // Ten values in the bucket 1500-2000 (middle 1750) are 750 ms above the threshold each; the fast ones count nothing.
        Assert.Equal(7_500, above, 3);
    }

    [Fact]
    public void A_window_keeps_at_most_the_cap_of_keys_per_kind_and_counts_the_rest_under_other()
    {
        var window = new PerfWindow(DateTime.UtcNow);
        for (var i = 0; i < PerfKeys.MaxKeysPerKind + 50; i++)
        {
            window.Add(PerfKind.Http, $"/route/{i}", "GET 2xx", 5, histogram: true, error: false, bytes: 10, dbCount: 0, dbMs: 0, externalMs: 0);
        }

        window.Add(PerfKind.WorkerRun, "loop", "60", 5, histogram: true, error: true, bytes: 0, dbCount: 2, dbMs: 3, externalMs: 0);
        var rows = window.Rows();
        var http = rows.Where(r => r.Series.Kind == PerfKind.Http).ToList();
        Assert.Equal(PerfKeys.MaxKeysPerKind + 1, http.Count);
        Assert.Equal(50, http.Single(r => r.Series.Key == PerfKeys.Other).Count);
        Assert.Equal(PerfKeys.MaxKeysPerKind + 50, http.Sum(r => r.Count));
        var loop = rows.Single(r => r.Series.Kind == PerfKind.WorkerRun);
        Assert.Equal((1, 1, 2, 3.0), (loop.Count, loop.Errors, loop.DbCount, loop.DbMs));
    }

    [Fact]
    public void A_window_aggregates_concurrent_writes_exactly()
    {
        var window = new PerfWindow(DateTime.UtcNow);
        Parallel.For(0, 10_000, i => window.Add(PerfKind.Db, "worker:a", string.Empty, i % 10, histogram: true, error: false, bytes: 1, dbCount: 0,
            dbMs: 0, externalMs: 0));
        var row = window.Rows().Single();
        Assert.Equal(10_000, row.Count);
        Assert.Equal(45_000, row.Sum, 6);
        Assert.Equal((0d, 9d), (row.Min, row.Max));
        Assert.Equal(10_000, PerfHistogram.Total(row.Histogram));
        Assert.Equal(10_000, row.Bytes);
    }

    [Fact]
    public void Keys_are_cut_to_their_maximum_and_a_missing_key_becomes_other()
    {
        Assert.Equal(PerfKeys.MaxKeyLength, PerfKeys.Key(new string('a', 500)).Length);
        Assert.Equal(PerfKeys.OtherSource, PerfKeys.Key(" "));
        Assert.Equal("429", PerfKeys.StatusClass(429));
        Assert.Equal("4xx", PerfKeys.StatusClass(404));
        Assert.Equal("error", PerfKeys.StatusClass(0));
    }

    [Theory]
    [InlineData("SELECT * FROM \"Clients\" WHERE \"Code\" = 'ACME' AND \"Id\" = 42", "SELECT * FROM \"Clients\" WHERE \"Code\" = ? AND \"Id\" = ?")]
    [InlineData("select 1.5e3, -7, x1, \"Col2\"", "select ?, ?, x1, \"Col2\"")]
    [InlineData("SELECT a FROM t WHERE id IN (1, 2, 3) AND k = ANY(ARRAY[$1, $2])", "SELECT a FROM t WHERE id IN (?) AND k = ANY(ARRAY[?])")]
    [InlineData("INSERT INTO t (a, b) VALUES (1, 'x'), (2, 'y'), (3, 'z')", "INSERT INTO t (a, b) VALUES (?, ?)")]
    [InlineData("SELECT 'it''s' -- a comment with 'quote'\nFROM t /* block 'x' */", "SELECT ? FROM t")]
    [InlineData("SELECT E'a\\'b', $tag$ body ' $tag$, $$x$$, $1", "SELECT ?, ?, ?, $1")]
    [InlineData("SELECT \"a\"\"b\" FROM \"T 1\" WHERE \"x_y\" = $12", "SELECT \"a\"\"b\" FROM \"T 1\" WHERE \"x_y\" = $12")]
    public void Sql_is_normalized_without_any_value_and_keeps_names_and_parameters(string sql, string expected)
    {
        Assert.Equal(expected, SqlFingerprint.Normalize(sql));
    }

    [Fact]
    public void Statements_that_differ_only_in_values_have_one_fingerprint()
    {
        var a = SqlFingerprint.Normalize("SELECT * FROM t WHERE id IN (1,2) AND name = 'a'");
        var b = SqlFingerprint.Normalize("SELECT  *  FROM t WHERE id IN (5, 6, 7) AND name = 'bbb'");
        Assert.Equal(a, b);
        Assert.Equal(SqlFingerprint.Hash(a), SqlFingerprint.Hash(b));
        Assert.Equal(16, SqlFingerprint.Hash(a).Length);
        Assert.NotEqual(SqlFingerprint.Hash(a), SqlFingerprint.Hash(SqlFingerprint.Normalize("SELECT * FROM u")));
    }

    [Theory]
    [InlineData("Mail to jan.peeters@example.com failed", "Mail to {email} failed")]
    [InlineData("endpoint 3f2504e0-4f89-11d3-9a0c-0305e82c3301 gone", "endpoint {guid} gone")]
    [InlineData("from 192.168.10.5 and fe80::1ff:fe23:4567:890a", "from {ip} and {ip}")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIn0.abc_DEF-123", "token {token}")]
    [InlineData("key AbCdEf0123456789AbCdEf0123456789xyz", "key {token}")]
    [InlineData("host rmm.customer.example timed out", "host {host} timed out")]
    [InlineData("at 2026-10-18 10:00:00 the loop ran", "at 2026-10-18 10:00:00 the loop ran")]
    [InlineData("System.Net.Http.HttpRequestException", "System.Net.Http.HttpRequestException")]
    [InlineData("pg_catalog.pg_class and snake_case_name", "pg_catalog.pg_class and snake_case_name")]
    [InlineData("SELECT \"Id\"::text, $1::uuid", "SELECT \"Id\"::text, $1::uuid")]
    public void The_redactor_masks_values_in_free_text_and_leaves_names_alone(string text, string expected)
    {
        Assert.Equal(expected, PerfRedactor.Default.Text(text));
    }

    [Fact]
    public void The_redactor_of_an_instance_masks_its_client_codes_and_host_names()
    {
        var redactor = PerfRedactor.ForInstance(["ACME", "IT"], ["srv-dc01.acme.local", "PC-0042"]);
        Assert.Equal("{client} sync of {host} and {host}; IT stays", redactor.Text("ACME sync of SRV-DC01 and pc-0042; IT stays"));
        Assert.Equal("Marker {n} for {client}", redactor.AdminText("Marker 12 for acme"));
        Assert.Equal("SELECT * FROM t WHERE c = ?", redactor.Sql("SELECT * FROM t WHERE c = 'ACME'"));
    }

    [Theory]
    [InlineData("/clients/3f2504e0-4f89-11d3-9a0c-0305e82c3301?tab=checks#x", "/clients/{id}")]
    [InlineData("/jobs/12345/output", "/jobs/{id}/output")]
    [InlineData("/users/jan@example.com", "/users/{id}")]
    [InlineData("/a/0123456789abcdef0123", "/a/{id}")]
    [InlineData("/clients/{ClientId:guid}", "/clients/{ClientId:guid}")]
    [InlineData("/settings/monitoring-templates", "/settings/monitoring-templates")]
    [InlineData("/", "/")]
    public void A_reported_route_keeps_its_template_and_loses_its_values(string route, string expected)
    {
        Assert.Equal(expected, PerfRedactor.Route(route));
        Assert.Null(PerfRedactor.Route("  "));
    }

    [Fact]
    public void A_url_is_matched_to_its_route_template_with_literals_before_parameters()
    {
        var routes = new RouteTemplates([
            "/", "/clients", "/clients/{ClientId:guid}", "/clients/{ClientId:guid}/sites/{SiteId:guid}", "/settings/tasks/new",
            "/settings/tasks/{Value}", "/endpoints/{EndpointId:guid}/{Tab?}", "/files/{*Path}"
        ]);
        var id = Guid.NewGuid();
        Assert.Equal("/clients/{ClientId:guid}", routes.Match($"https://rmm.example/clients/{id}?tab=1"));
        Assert.Equal("/clients/{ClientId:guid}/sites/{SiteId:guid}", routes.Match($"/clients/{id}/sites/{Guid.NewGuid()}"));
        Assert.Equal("/settings/tasks/new", routes.Match("/SETTINGS/tasks/new"));
        Assert.Equal("/settings/tasks/{Value}", routes.Match("/settings/tasks/other"));
        Assert.Equal("/endpoints/{EndpointId:guid}/{Tab?}", routes.Match($"/endpoints/{id}"));
        Assert.Equal("/endpoints/{EndpointId:guid}/{Tab?}", routes.Match($"/endpoints/{id}/checks"));
        Assert.Equal("/files/{*Path}", routes.Match("/files/a/b/c"));
        Assert.Equal("/", routes.Match("https://rmm.example/"));
        Assert.Equal(RouteTemplates.Unmatched, routes.Match("/clients/not-a-guid"));
    }

    [Fact]
    public void Settings_default_to_basic_with_every_collector_on_and_round_trip_as_json()
    {
        var settings = PerformanceSettings.Default;
        Assert.True(settings.IsOn);
        Assert.All(Enum.GetValues<PerfCollector>(), c => Assert.True(settings.Collects(c)));
        Assert.Null(settings.Problem());
        Assert.Equal(1000, settings.Threshold(PerfThresholds.SlowInteraction));

        var changed = settings with
        {
            Level = PerfLevel.Off,
            DisabledCollectors = [PerfCollector.Host],
            Thresholds = new Dictionary<string, double> { [PerfThresholds.SlowInteraction] = 2500 }
        };
        var parsed = PerformanceSettings.Parse(changed.Serialize());
        Assert.Equal(PerfLevel.Off, parsed.Level);
        Assert.Equal(new[] { PerfCollector.Host }, parsed.DisabledCollectors);
        Assert.Equal(2500, parsed.Threshold(PerfThresholds.SlowInteraction));
        Assert.False(parsed.Collects(PerfCollector.Database));
        Assert.Contains("\"Off\"", changed.Serialize());
    }

    [Fact]
    public void Unreadable_or_absent_settings_are_the_defaults_and_an_out_of_range_threshold_counts_as_its_default()
    {
        Assert.Equal(PerformanceSettings.Default, PerformanceSettings.Parse(null));
        Assert.Equal(PerformanceSettings.Default, PerformanceSettings.Parse("{not json"));
        var settings = PerformanceSettings.Default with { Thresholds = new Dictionary<string, double> { [PerfThresholds.SlowInteraction] = 5 } };
        Assert.Equal(1000, settings.Threshold(PerfThresholds.SlowInteraction));
        Assert.NotNull(settings.Problem());
    }

    [Fact]
    public void Settings_that_cannot_be_saved_say_why()
    {
        Assert.Contains("between", (PerformanceSettings.Default with { SlowEventMs = 10 }).Problem());
        Assert.NotNull((PerformanceSettings.Default with { MinuteRetentionDays = 31 }).Problem());
        Assert.NotNull((PerformanceSettings.Default with { HourRetentionDays = 3, MinuteRetentionDays = 7 }).Problem());
        Assert.NotNull((PerformanceSettings.Default with { Thresholds = new Dictionary<string, double> { ["nope"] = 1 } }).Problem());
        Assert.NotNull((PerformanceSettings.Default with { Level = (PerfLevel)9 }).Problem());
        Assert.NotNull((PerformanceSettings.Default with { DisabledCollectors = [(PerfCollector)99] }).Problem());
        Assert.All(PerfThresholds.All, t => Assert.InRange(t.Default, t.Min, t.Max));
        Assert.Equal(PerfThresholds.All.Count, PerfThresholds.All.Select(t => t.Key).Distinct().Count());
    }

    [Fact]
    public async Task A_scope_collects_database_time_from_its_async_flow_until_it_ends()
    {
        PerfScope outer;
        using (outer = PerfContext.Begin("interaction:/a"))
        {
            var inner = PerfContext.Begin("worker:b");
            await Task.Run(() => PerfContext.Current!.AddDb(5));
            Assert.Equal("worker:b", PerfContext.Source);
            inner.Dispose();
            Assert.Equal("interaction:/a", PerfContext.Source);
            inner.AddDb(100);
            Assert.Equal((1, 5.0), (inner.DbCount, inner.DbMs));
            Assert.Equal((2, 105.0), (outer.DbCount, outer.DbMs));
            outer.AddUserWait(TimeSpan.FromHours(1));
            Assert.Equal(0, outer.ElapsedMs);
        }

        Assert.True(outer.Ended);
        Assert.Equal(PerfKeys.OtherSource, PerfContext.Source);
    }

    [Fact]
    public void The_recorder_records_nothing_when_the_level_is_off_or_the_collector_is_switched_off()
    {
        var cache = new PerfSettingsCache();
        var recorder = new PerfRecorder("web", cache, TimeProvider.System);
        recorder.Record(PerfCollector.Http, PerfKind.Http, "/api/v1/clients", "GET 2xx", 12);
        cache.Set(PerformanceSettings.Default with { DisabledCollectors = [PerfCollector.Http] });
        recorder.Record(PerfCollector.Http, PerfKind.Http, "/api/v1/clients", "GET 2xx", 12);
        recorder.Record(PerfCollector.Workers, PerfKind.WorkerRun, "loop", "60", 1);
        cache.Set(PerformanceSettings.Default with { Level = PerfLevel.Off });
        recorder.Record(PerfCollector.Workers, PerfKind.WorkerRun, "loop", "60", 1);
        recorder.Count(PerfCollector.Runtime, PerfKind.Exception, "System.Exception");

        var rows = recorder.Swap(DateTime.UtcNow).Rows();
        Assert.Equal(1, rows.Single(r => r.Series.Kind == PerfKind.Http).Count);
        Assert.Equal(1, rows.Single(r => r.Series.Kind == PerfKind.WorkerRun).Count);
        Assert.DoesNotContain(rows, r => r.Series.Kind == PerfKind.Exception);
    }

    [Fact]
    public void A_completed_scope_is_recorded_with_its_split_and_kept_as_an_event_when_slow()
    {
        var recorder = new PerfRecorder("workers", new PerfSettingsCache(PerformanceSettings.Default with { SlowEventMs = 100 }), TimeProvider.System);
        var scope = PerfContext.Begin("worker:slow");
        scope.AddDb(40);
        Thread.Sleep(150);
        recorder.Complete(PerfCollector.Workers, PerfKind.WorkerRun, "slow", "60", scope, error: true);
        var row = recorder.Swap(DateTime.UtcNow).Rows().Single();
        Assert.Equal((1, 1, 1), (row.Count, row.Errors, row.DbCount));
        Assert.True(row.Sum >= 100);
        var slowEvent = Assert.Single(recorder.TakeEvents());
        Assert.Equal(("workers", PerfKind.WorkerRun, "slow", true), (slowEvent.Component, slowEvent.Kind, slowEvent.Key, slowEvent.Failed));
        Assert.Null(PerfContext.Current);
    }
}
