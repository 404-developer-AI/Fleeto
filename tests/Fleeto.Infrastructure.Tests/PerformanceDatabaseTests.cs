using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Performance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The performance monitor against a real database (0.7.0): a minute is written in one batch and read back with its histogram, a
/// component can write only its own rows, database time is attributed to the work that caused it, and the reader gives statement
/// differences that survive a reset and says when there is no baseline.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PerformanceDatabaseTests
{
    private readonly Testing.TestDatabase _db;

    public PerformanceDatabaseTests(DatabaseFixture fixture) => _db = fixture.Database;

    private async Task ClearAsync()
    {
        await using var db = _db.DbFactory.CreateSystem();
        await db.PerfMinutes.ExecuteDeleteAsync();
        await db.PerfHours.ExecuteDeleteAsync();
        await db.PerfRuntimeMinutes.ExecuteDeleteAsync();
        await db.PerfEvents.ExecuteDeleteAsync();
        await db.PerfStatementSnapshots.ExecuteDeleteAsync();
        await db.PerfDatabaseSnapshots.ExecuteDeleteAsync();
        await db.PerfTableSnapshots.ExecuteDeleteAsync();
        await db.PerfHostMinutes.ExecuteDeleteAsync();
        await db.WorkerWatermarks.Where(w => w.Name == PerformanceReader.RollupWatermark).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task A_minute_is_written_in_one_batch_and_read_back_as_a_series_with_its_percentiles()
    {
        await ClearAsync();
        var cache = new PerfSettingsCache();
        var recorder = new PerfRecorder("web", cache, _db.Time);
        var minute = PerfRecorder.MinuteOf(_db.Time.GetUtcNow().UtcDateTime).AddMinutes(-5);
        recorder.Swap(minute);
        for (var i = 0; i < 100; i++)
        {
            recorder.Record(PerfCollector.Interactions, PerfKind.Interaction, "/clients", "load", i < 95 ? 50 : 3_000, dbCount: 2, dbMs: 10);
        }

        recorder.Count(PerfCollector.Runtime, PerfKind.Exception, "System.InvalidOperationException", count: 3);
        recorder.Event(PerfKind.Interaction, "/clients", "load", 3_000, 2, 10, 0, failed: false);
        recorder.Runtime.Add(new RuntimeSample(12, 300 << 20, 100 << 20, 5, 1 << 20, 20, 3, 4, 3, 7, 5, 2, 0));

        var flusher = new PerfFlusher(recorder, _db.DataSource, _db.Time, NullLogger<PerfFlusher>.Instance);
        await flusher.FlushAsync(minute.AddMinutes(1), CancellationToken.None);

        await using var db = _db.DbFactory.CreateSystem();
        var rows = await db.PerfMinutes.AsNoTracking().Where(m => m.Minute == minute).ToListAsync();
        var interaction = rows.Single(r => r.Kind == PerfKind.Interaction);
        Assert.Equal(("web", "/clients", "load", 100, 200), (interaction.Component, interaction.Key, interaction.SubKey, interaction.Count, interaction.DbCount));
        Assert.Equal(3, rows.Single(r => r.Kind == PerfKind.Exception).Count);
        var runtime = await db.PerfRuntimeMinutes.AsNoTracking().SingleAsync(r => r.Minute == minute);
        Assert.Equal(("web", 12d, 7), (runtime.Component, runtime.CpuPercent, runtime.Connections));
        Assert.True(runtime.OverheadMs > 0);
        Assert.Single(await db.PerfEvents.AsNoTracking().Where(e => e.Key == "/clients").ToListAsync());

        var data = await PerformanceReader.ReadAsync(db, minute, minute.AddMinutes(1), PerformanceSettings.Default, CancellationToken.None);
        var series = data.Series.Single(s => s.Kind == PerfKind.Interaction);
        Assert.Equal(100, series.Count);
        Assert.InRange(series.P50!.Value, 40, 50);
        Assert.InRange(series.P99!.Value, 2_000, 3_000);
        Assert.Equal(100, series.PeakCount);
        Assert.Equal("web", Assert.Single(data.Runtime).Component);
    }

    [Fact]
    public async Task A_component_writes_only_rows_of_its_own_name()
    {
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_gateway");
        await Execute(connection, InsertMinute("gateway"));
        await Execute(connection, "SAVEPOINT other");
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, InsertMinute("web")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        await Execute(connection, "ROLLBACK TO SAVEPOINT other");
        var read = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, """SELECT count(*) FROM "PerfMinutes" """));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, read.SqlState);
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Database_time_is_added_to_the_scope_of_the_work_that_caused_it()
    {
        var recorder = new PerfRecorder("workers", new PerfSettingsCache(), _db.Time);
        using var listener = new PerfMeterListener(recorder);
        listener.Start();

        using (var scope = PerfContext.Begin("worker:test-loop"))
        {
            for (var i = 0; i < 3; i++)
            {
                await using var command = _db.DataSource.CreateCommand("SELECT pg_sleep(0.01)");
                await command.ExecuteNonQueryAsync();
            }

            Assert.True(scope.DbCount >= 3);
            Assert.True(scope.DbMs >= 25);
        }

        var db = recorder.Swap(DateTime.UtcNow).Rows().Where(r => r.Series is { Kind: PerfKind.Db, Key: "worker:test-loop" }).ToList();
        Assert.True(db.Sum(r => r.Count) >= 3);
    }

    [Fact]
    public async Task Statement_differences_survive_a_reset_and_a_period_without_baseline_says_so()
    {
        await ClearAsync();
        var from = PerfRecorder.MinuteOf(_db.Time.GetUtcNow().UtcDateTime).AddHours(-1);
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.PerfDatabaseSnapshots.Add(Snapshot(from.AddMinutes(15), hit: 1_000, read: 100));
            db.PerfDatabaseSnapshots.Add(Snapshot(from.AddMinutes(45), hit: 1_900, read: 200));
            db.PerfStatementSnapshots.AddRange(
                Statement(from.AddMinutes(15), 1, calls: 100, total: 1_000),
                Statement(from.AddMinutes(45), 1, calls: 160, total: 1_600),
                Statement(from.AddMinutes(15), 2, calls: 500, total: 9_000),
                Statement(from.AddMinutes(45), 2, calls: 50, total: 700),
                Statement(from.AddMinutes(45), 3, calls: 10, total: 10));
            await db.SaveChangesAsync();
        }

        await using (var db = _db.DbFactory.CreateSystem())
        {
            var noBaseline = await PerformanceReader.ReadAsync(db, from, from.AddHours(1), PerformanceSettings.Default, CancellationToken.None);
            Assert.False(noBaseline.Database!.Baseline);
            Assert.Null(noBaseline.Database.CacheHitRatio);
            Assert.Contains(noBaseline.Missing, m => m.StartsWith("No database snapshot before the start", StringComparison.Ordinal));
            var statements = noBaseline.Statements.ToDictionary(s => s.QueryId);
            Assert.Equal((60, 600d), (statements[1].Calls, statements[1].TotalMs));
            // Its counters went down: reset in between, so everything since counts.
            Assert.Equal((50, 700d), (statements[2].Calls, statements[2].TotalMs));
            Assert.False(statements.ContainsKey(3));

            var withBaseline = await PerformanceReader.ReadAsync(db, from.AddMinutes(20), from.AddHours(1), PerformanceSettings.Default, CancellationToken.None);
            Assert.True(withBaseline.Database!.Baseline);
            Assert.Equal(900d / 1_000, withBaseline.Database.CacheHitRatio);
        }
    }

    [Fact]
    public async Task A_long_period_reads_rolled_up_hours_and_the_minutes_after_them()
    {
        await ClearAsync();
        var now = PerfRecorder.MinuteOf(_db.Time.GetUtcNow().UtcDateTime);
        var rolled = new DateTime(now.Year, now.Month, now.Day, now.Hour, 0, 0, DateTimeKind.Utc).AddHours(-3);
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.PerfHours.Add(new PerfHour
            {
                Hour = rolled, Component = "web", Kind = PerfKind.Http, Key = "/api/v1/clients", SubKey = "GET 2xx", Count = 1_000, Sum = 50_000, Max = 900,
                Histogram = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1_000], PeakCount = 40
            });
            db.WorkerWatermarks.Add(new WorkerWatermark { Name = PerformanceReader.RollupWatermark, Value = rolled.Ticks, UpdatedAt = now });
            // A minute inside the rolled-up hour must not be counted twice; one after it must count.
            db.PerfMinutes.Add(Minute(rolled.AddMinutes(10), 999));
            db.PerfMinutes.Add(Minute(now.AddMinutes(-30), 5));
            await db.SaveChangesAsync();
        }

        await using (var db = _db.DbFactory.CreateSystem())
        {
            var data = await PerformanceReader.ReadAsync(db, now.AddHours(-24), now.AddMinutes(1), PerformanceSettings.Default, CancellationToken.None);
            var series = data.Series.Single(s => s.Key == "/api/v1/clients");
            Assert.Equal(1_005, series.Count);
            Assert.Equal(40, series.PeakCount);
            Assert.Equal(1_005, PerfHistogram.Total(series.Histogram));

            var timeline = await PerformanceReader.ReadTimelineAsync(db, now.AddHours(-6), now.AddMinutes(1), TimeSpan.FromMinutes(5), CancellationToken.None);
            // The timeline reads minute rows up to 24 hours, the rolled-up hour included.
            Assert.Equal(1_004, timeline.Sum(p => p.Requests.Count));
        }
    }

    [Fact]
    public async Task The_page_reads_every_period_with_the_privileges_of_web()
    {
        // Found on the test VPS on 2026-10-20: a period longer than the minute rows failed with "permission denied for table
        // WorkerWatermarks", which the owner role of the other tests never sees. A part that catches its own failure counts too.
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_web");
        await using var db = new FleetoDbContext(new DbContextOptionsBuilder<FleetoDbContext>().UseNpgsql(connection).Options);
        await db.Database.UseTransactionAsync(transaction);

        var refused = new List<string>();
        // Other tests provoke refusals on purpose; only the ones thrown in this test's own flow count.
        var mine = new AsyncLocal<bool> { Value = true };
        void OnException(object? sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (mine.Value && e.Exception is PostgresException { SqlState: PostgresErrorCodes.InsufficientPrivilege } denied)
            {
                lock (refused)
                {
                    refused.Add(denied.MessageText);
                }
            }
        }

        AppDomain.CurrentDomain.FirstChanceException += OnException;
        try
        {
            var now = PerfRecorder.MinuteOf(_db.Time.GetUtcNow().UtcDateTime);
            foreach (var length in new[] { TimeSpan.FromMinutes(15), TimeSpan.FromHours(6), TimeSpan.FromDays(1), TimeSpan.FromDays(7) })
            {
                var from = now - length;
                var bucket = TimeSpan.FromMinutes(5);
                await PerformanceReader.ReadAsync(db, from, now, PerformanceSettings.Default, CancellationToken.None);
                await PerformanceReader.ReadTimelineAsync(db, from, now, bucket, CancellationToken.None);
                await PerformanceReader.ReadRuntimeTimelineAsync(db, from, now, bucket, CancellationToken.None);
                await PerformanceReader.ReadHostTimelineAsync(db, from, now, bucket, CancellationToken.None);
                await PerformanceReader.ReadKeyTimelineAsync(db, from, now, bucket, PerfKind.WorkerRun, CancellationToken.None);
            }

            await PerformanceReader.ReadIndexesAsync(db, CancellationToken.None);
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= OnException;
        }

        Assert.Empty(refused);
        await transaction.RollbackAsync();
    }

    private static PerfMinute Minute(DateTime minute, int count) => new()
    {
        Minute = minute, Component = "web", Kind = PerfKind.Http, Key = "/api/v1/clients", SubKey = "GET 2xx", Count = count, Sum = count * 50d, Max = 60,
        Histogram = [0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, count]
    };

    private static PerfDatabaseSnapshot Snapshot(DateTime time, long hit, long read) => new()
    {
        Time = time, BlocksHit = hit, BlocksRead = read, Connections = 5, MaxConnections = 100, DatabaseBytes = 1 << 30
    };

    private static PerfStatementSnapshot Statement(DateTime time, long queryId, long calls, double total) => new()
    {
        Time = time, QueryId = queryId, Query = $"SELECT {queryId}", Calls = calls, TotalMs = total
    };

    private static string InsertMinute(string component) => $"""
        INSERT INTO "PerfMinutes" ("Minute", "Component", "Kind", "Key", "SubKey", "Count", "Errors", "Sum", "Min", "Max", "Histogram", "Bytes", "DbCount", "DbMs", "ExternalMs")
        VALUES (now(), '{component}', 'AgentMessage', 'Heartbeat', 'agent', 1, 0, 1, 1, 1, '{"{1}"}', 0, 0, 0, 0)
        """;

    private async Task EnsureRolesAsync()
    {
        await using var command = _db.DataSource.CreateCommand(string.Join('\n', Hosting.DatabaseRoles.Application.Select(r =>
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;")));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // test-only statements built from constants
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
