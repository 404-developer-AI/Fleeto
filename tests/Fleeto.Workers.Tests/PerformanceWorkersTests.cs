using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;
using Fleeto.Workers.Backups;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Performance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The performance monitor in the workers (0.7.0): every loop is measured by its base class, minutes roll up into hours idempotently,
/// retention follows the settings, a new version leaves one deploy marker, the database is read as snapshots, and a backup carries
/// the performance tables without their rows.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PerformanceWorkersTests
{
    private readonly WorkersFixture _fixture;

    public PerformanceWorkersTests(WorkersFixture fixture) => _fixture = fixture;

    private PerfMaintenanceService Maintenance() =>
        new(_fixture.Db.DbFactory, _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<PerfMaintenanceService>.Instance);

    private async Task ClearAsync()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.PerfMinutes.ExecuteDeleteAsync();
        await db.PerfHours.ExecuteDeleteAsync();
        await db.PerfEvents.ExecuteDeleteAsync();
        await db.PerfMarkers.ExecuteDeleteAsync();
        await db.WorkerWatermarks.Where(w => w.Name == PerfMaintenanceService.RollupWatermark).ExecuteDeleteAsync();
    }

    private static PerfMinute Minute(DateTime minute, string key, int count, int[] histogram) => new()
    {
        Minute = minute, Component = "web", Kind = PerfKind.Interaction, Key = key, SubKey = "load", Count = count, Sum = count * 10d, Min = 5, Max = 20,
        Histogram = histogram, DbCount = count, DbMs = count * 2d
    };

    [Fact]
    public async Task Minutes_roll_up_into_their_hour_and_a_second_rollup_changes_nothing()
    {
        await ClearAsync();
        var hour = new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.PerfMinutes.AddRange(
                Minute(hour.AddMinutes(1), "/clients", 10, [0, 2, 8]),
                Minute(hour.AddMinutes(1), "/clients", 5, [0, 0, 0, 5]),
                Minute(hour.AddMinutes(2), "/clients", 7, [7]),
                Minute(hour.AddMinutes(59), "/alerts", 1, [1]),
                Minute(hour.AddHours(1), "/clients", 100, [100]));
            await db.SaveChangesAsync();
        }

        for (var pass = 0; pass < 2; pass++)
        {
            await using var db = _fixture.Db.DbFactory.CreateSystem();
            await PerfMaintenanceService.RollupHourAsync(db, hour, CancellationToken.None);
        }

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var hours = await db.PerfHours.AsNoTracking().Where(h => h.Hour == hour).ToListAsync();
            Assert.Equal(2, hours.Count);
            var clients = hours.Single(h => h.Key == "/clients");
            Assert.Equal((22L, 220d, 22L, 15), (clients.Count, clients.Sum, clients.DbCount, clients.PeakCount));
            Assert.Equal(new[] { 7, 2, 8, 5 }, clients.Histogram);
        }
    }

    [Fact]
    public async Task The_rollup_follows_its_watermark_and_stops_at_the_last_finished_hour()
    {
        await ClearAsync();
        var now = _fixture.Now;
        var current = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            for (var h = 3; h >= 0; h--)
            {
                db.PerfMinutes.Add(Minute(current.AddHours(-h).AddMinutes(30), "/clients", 1, [1]));
            }

            await db.SaveChangesAsync();
        }

        var maintenance = Maintenance();
        while (await maintenance.RollupAsync(CancellationToken.None))
        {
        }

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var rolled = await db.PerfHours.AsNoTracking().Select(h => h.Hour).OrderBy(h => h).ToListAsync();
            var lastFinished = new DateTime((now - TimeSpan.FromMinutes(3)).Ticks - (now - TimeSpan.FromMinutes(3)).Ticks % TimeSpan.TicksPerHour, DateTimeKind.Utc).AddHours(-1);
            Assert.Equal(Enumerable.Range(0, 4).Select(i => current.AddHours(i - 3)).Where(h => h <= lastFinished), rolled);
            var watermark = await db.WorkerWatermarks.AsNoTracking().SingleAsync(w => w.Name == PerfMaintenanceService.RollupWatermark);
            Assert.Equal(lastFinished, new DateTime(watermark.Value, DateTimeKind.Utc));
        }
    }

    [Fact]
    public async Task Retention_deletes_what_is_older_than_the_settings()
    {
        await ClearAsync();
        var now = _fixture.Now;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.PerfMinutes.AddRange(Minute(now.AddDays(-3), "/old", 1, [1]), Minute(now.AddHours(-1), "/new", 1, [1]));
            db.PerfEvents.AddRange(
                new PerfEvent { Time = now.AddDays(-20), Component = "web", Kind = PerfKind.Interaction, Key = "/old", DurationMs = 2_000 },
                new PerfEvent { Time = now.AddDays(-1), Component = "web", Kind = PerfKind.Interaction, Key = "/new", DurationMs = 2_000 });
            await db.SaveChangesAsync();
        }

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.PerfFingerprints.ExecuteDeleteAsync();
            db.PerfFingerprints.AddRange(
                new PerfFingerprint { Component = "web", Hash = "0000000000000old", Sql = "SELECT ?", FirstSeen = now.AddDays(-100), LastSeen = now.AddDays(-95) },
                new PerfFingerprint { Component = "web", Hash = "0000000000000new", Sql = "SELECT ?", FirstSeen = now.AddDays(-100), LastSeen = now.AddDays(-3) });
            await db.SaveChangesAsync();
        }

        await Maintenance().ApplyRetentionAsync(PerformanceSettings.Default with { MinuteRetentionDays = 2 }, CancellationToken.None);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(new[] { "/new" }, await db.PerfMinutes.Select(m => m.Key).ToListAsync());
            Assert.Equal(new[] { "/new" }, await db.PerfEvents.Select(e => e.Key).ToListAsync());
            // A statement is kept as long as an hour row can refer to it.
            Assert.Equal(new[] { "0000000000000new" }, await db.PerfFingerprints.Select(f => f.Hash).ToListAsync());
        }
    }

    [Fact]
    public async Task A_new_version_leaves_one_deploy_marker()
    {
        await ClearAsync();
        var maintenance = Maintenance();
        await maintenance.AddDeployMarkerAsync(CancellationToken.None);
        await maintenance.AddDeployMarkerAsync(CancellationToken.None);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var marker = await db.PerfMarkers.AsNoTracking().SingleAsync();
        Assert.Equal((PerfMarkerKind.Deploy, $"Fleeto {FleetoVersion.Current}"), (marker.Kind, marker.Label));
    }

    [Fact]
    public async Task The_database_and_its_tables_are_read_as_snapshots()
    {
        var service = new PerfSnapshotService(_fixture.Db.DbFactory, _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<PerfSnapshotService>.Instance);
        await service.SnapshotDatabaseAsync(CancellationToken.None);
        await service.SnapshotTablesAsync(CancellationToken.None);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var snapshot = await db.PerfDatabaseSnapshots.AsNoTracking().OrderByDescending(s => s.Time).FirstAsync();
        Assert.True(snapshot.DatabaseBytes > 0);
        Assert.True(snapshot.MaxConnections > 0);
        // Sessions are counted by the workers (0.7.0 step 2): web may not see other roles' sessions.
        Assert.NotNull(snapshot.ActiveSessions);
        Assert.NotNull(snapshot.IdleInTransactionSessions);
        Assert.NotNull(snapshot.LockWaitingSessions);
        var tables = await db.PerfTableSnapshots.AsNoTracking().Where(t => t.Time == db.PerfTableSnapshots.Max(x => x.Time)).ToListAsync();
        Assert.Contains(tables, t => t.Table == "Endpoints");
        Assert.Contains(tables, t => t.Table == "PerfMinutes");
    }

    [Fact]
    public async Task A_backup_holds_the_performance_tables_without_their_rows_but_keeps_the_settings()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var tables = await PerformanceBackup.TablesWithoutDataAsync(db, NullLogger.Instance, CancellationToken.None);
        Assert.Contains(("public", "PerfMinutes"), tables);
        Assert.Contains(("public", "PerfStatementSnapshots"), tables);
        Assert.Contains(("public", "PerfFingerprints"), tables);
        Assert.DoesNotContain(("public", "PerformanceSettings"), tables);
        foreach (var table in PerformanceBackup.Tables)
        {
            var exists = await db.Database.SqlQueryRaw<bool>("""SELECT to_regclass(format('public.%I', {0})) IS NOT NULL AS "Value" """, table)
                .SingleAsync();
            Assert.True(exists, table);
        }
    }

    [Fact]
    public async Task Every_run_of_a_loop_is_measured_by_its_base_class_and_idle_runs_are_left_out()
    {
        var recorder = new PerfRecorder("workers", new PerfSettingsCache(), _fixture.Db.Time);
        using var listener = new PerfMeterListener(recorder);
        listener.Start();
        Perf.Use(recorder);
        try
        {
            using var loop = new ProbeLoop(_fixture);
            using var cts = new CancellationTokenSource();
            await loop.StartAsync(cts.Token);
            await loop.Ran.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await loop.StopAsync(CancellationToken.None);

            var rows = recorder.Swap(DateTime.UtcNow).Rows();
            var run = rows.Single(r => r.Series is { Kind: PerfKind.WorkerRun, Key: "perf-probe" });
            Assert.Equal("3600", run.Series.SubKey);
            Assert.True(run.DbCount >= 1);
            Assert.Contains(rows, r => r.Series is { Kind: PerfKind.Db, Key: "worker:perf-probe" });
        }
        finally
        {
            Perf.Use(PerfRecorder.Disabled);
        }
    }

    private sealed class ProbeLoop(WorkersFixture fixture)
        : WorkerLoop("perf-probe", fixture.Heartbeat(), TimeProvider.System, NullLogger.Instance)
    {
        public TaskCompletionSource Ran { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override TimeSpan Interval => TimeSpan.FromHours(1);

        protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
        {
            await using var db = fixture.Db.DbFactory.CreateSystem();
            await db.Endpoints.CountAsync(cancellationToken);
            _ = Task.Delay(200, CancellationToken.None).ContinueWith(_ => Ran.TrySetResult(), TaskScheduler.Default);
            return false;
        }
    }
}
