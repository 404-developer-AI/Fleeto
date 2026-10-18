using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Performance;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Workers.Performance;

/// <summary>
/// Upkeep of the performance monitor (0.7.0, PERFORMANCE.md §6), every 5 minutes: a deploy marker when the workers start on a new
/// version, the minutes of every finished hour rolled up into the hour table, and retention per the settings. The rollup rebuilds
/// an hour whole (delete, then insert), so running it again changes nothing; a watermark remembers the last hour done, and a pass
/// does at most <see cref="MaxHoursPerPass"/> hours. Deletes run in batches like every retention in Fleeto.
/// </summary>
public sealed class PerfMaintenanceService : WorkerLoop
{
    public const string RollupWatermark = "perf-rollup";
    public const int MaxHoursPerPass = 6;

    /// <summary>An hour is rolled up once its last minute had time to be flushed by every component.</summary>
    private static readonly TimeSpan FlushLag = TimeSpan.FromMinutes(3);

    private const int BatchSize = 5000;
    private const int MaxBatchesPerTable = 100;

    private readonly IFleetoDbContextFactory _dbFactory;
    private bool _markerChecked;

    public PerfMaintenanceService(IFleetoDbContextFactory dbFactory, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<PerfMaintenanceService> logger)
        : base("perf-maintenance", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!_markerChecked)
        {
            await AddDeployMarkerAsync(cancellationToken);
            _markerChecked = true;
        }

        var more = await RollupAsync(cancellationToken);
        await ApplyRetentionAsync(Perf.Recorder.Settings.Current, cancellationToken);
        return more;
    }

    /// <summary>Adds a deploy marker when the last one names another version. Server and agent release under one version.</summary>
    public async Task AddDeployMarkerAsync(CancellationToken cancellationToken)
    {
        var label = $"Fleeto {FleetoVersion.Current}";
        await using var db = _dbFactory.CreateSystem();
        var last = await db.PerfMarkers.AsNoTracking().Where(m => m.Kind == PerfMarkerKind.Deploy).OrderByDescending(m => m.Time)
            .Select(m => m.Label).FirstOrDefaultAsync(cancellationToken);
        if (last == label)
        {
            return;
        }

        db.PerfMarkers.Add(new PerfMarker { Time = Time.GetUtcNow().UtcDateTime, Kind = PerfMarkerKind.Deploy, Label = label });
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Rolls up finished hours after the watermark. Returns true when more hours wait.</summary>
    public async Task<bool> RollupAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var lastFinished = HourOf(now - FlushLag).AddHours(-1);
        await using var db = _dbFactory.CreateSystem();

        var watermark = await db.WorkerWatermarks.SingleOrDefaultAsync(w => w.Name == RollupWatermark, cancellationToken);
        DateTime next;
        if (watermark is null)
        {
            var first = await db.PerfMinutes.MinAsync(m => (DateTime?)m.Minute, cancellationToken);
            if (first is null)
            {
                return false;
            }

            next = HourOf(first.Value);
        }
        else
        {
            next = new DateTime(watermark.Value, DateTimeKind.Utc).AddHours(1);
        }

        var done = 0;
        for (var hour = next; hour <= lastFinished && done < MaxHoursPerPass; hour = hour.AddHours(1), done++)
        {
            await RollupHourAsync(db, hour, cancellationToken);
            if (watermark is null)
            {
                watermark = new WorkerWatermark { Name = RollupWatermark };
                db.WorkerWatermarks.Add(watermark);
            }

            watermark.Value = hour.Ticks;
            watermark.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return next.AddHours(done) <= lastFinished;
    }

    /// <summary>Rebuilds one hour from its minutes: delete what was there, insert the sums. Idempotent.</summary>
    public static async Task RollupHourAsync(FleetoDbContext db, DateTime hour, CancellationToken cancellationToken)
    {
        var end = hour.AddHours(1);
        var minutes = await db.PerfMinutes.AsNoTracking().Where(m => m.Minute >= hour && m.Minute < end).ToListAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.PerfHours.Where(h => h.Hour == hour).ExecuteDeleteAsync(cancellationToken);
        foreach (var group in minutes.GroupBy(m => (m.Component, m.Kind, m.Key, m.SubKey)))
        {
            var histogram = group.Aggregate((int[]?)null, (total, m) => PerfHistogram.Add(total, m.Histogram)) ?? [];
            db.PerfHours.Add(new PerfHour
            {
                Hour = hour,
                Component = group.Key.Component,
                Kind = group.Key.Kind,
                Key = group.Key.Key,
                SubKey = group.Key.SubKey,
                Count = group.Sum(m => (long)m.Count),
                Errors = group.Sum(m => (long)m.Errors),
                Sum = group.Sum(m => m.Sum),
                Min = group.Min(m => m.Min),
                Max = group.Max(m => m.Max),
                Histogram = PerfHistogram.Trim(histogram),
                Bytes = group.Sum(m => m.Bytes),
                DbCount = group.Sum(m => (long)m.DbCount),
                DbMs = group.Sum(m => m.DbMs),
                ExternalMs = group.Sum(m => m.ExternalMs),
                // A component that restarted within a minute wrote it twice: the busiest minute adds those up.
                PeakCount = group.GroupBy(m => m.Minute).Max(g => g.Sum(m => m.Count))
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Deletes what is older than the retention settings, in batches.</summary>
    public async Task ApplyRetentionAsync(PerformanceSettings settings, CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await DeleteAsync("""
            DELETE FROM "PerfMinutes" WHERE ("Minute", "Id") IN (SELECT "Minute", "Id" FROM "PerfMinutes" WHERE "Minute" < @cutoff LIMIT 5000)
            """, now.AddDays(-settings.MinuteRetentionDays), cancellationToken);
        await DeleteAsync("""
            DELETE FROM "PerfRuntimeMinutes" WHERE "Id" IN (SELECT "Id" FROM "PerfRuntimeMinutes" WHERE "Minute" < @cutoff LIMIT 5000)
            """, now.AddDays(-settings.MinuteRetentionDays), cancellationToken);
        await DeleteAsync("""
            DELETE FROM "PerfHostMinutes" WHERE "Minute" IN (SELECT "Minute" FROM "PerfHostMinutes" WHERE "Minute" < @cutoff LIMIT 5000)
            """, now.AddDays(-settings.MinuteRetentionDays), cancellationToken);
        await DeleteAsync("""
            DELETE FROM "PerfHours" WHERE "Id" IN (SELECT "Id" FROM "PerfHours" WHERE "Hour" < @cutoff LIMIT 5000)
            """, now.AddDays(-settings.HourRetentionDays), cancellationToken);
        await DeleteAsync("""
            DELETE FROM "PerfMarkers" WHERE "Id" IN (SELECT "Id" FROM "PerfMarkers" WHERE "Time" < @cutoff LIMIT 5000)
            """, now.AddDays(-settings.HourRetentionDays), cancellationToken);
        await DeleteAsync("""
            DELETE FROM "PerfEvents" WHERE "Id" IN (SELECT "Id" FROM "PerfEvents" WHERE "Time" < @cutoff LIMIT 5000)
            """, now.AddDays(-settings.EventRetentionDays), cancellationToken);
        // A statement no longer seen goes with the last hour row that can refer to it.
        await DeleteAsync("""
            DELETE FROM "PerfFingerprints" WHERE ("Component", "Hash") IN
              (SELECT "Component", "Hash" FROM "PerfFingerprints" WHERE "LastSeen" < @cutoff LIMIT 5000)
            """, now.AddDays(-settings.HourRetentionDays), cancellationToken);
        var snapshots = now.AddDays(-settings.SnapshotRetentionDays);
        await DeleteAsync("""
            DELETE FROM "PerfStatementSnapshots" WHERE "Id" IN (SELECT "Id" FROM "PerfStatementSnapshots" WHERE "Time" < @cutoff LIMIT 5000)
            """, snapshots, cancellationToken);
        await DeleteAsync("""
            DELETE FROM "PerfDatabaseSnapshots" WHERE "Time" IN (SELECT "Time" FROM "PerfDatabaseSnapshots" WHERE "Time" < @cutoff LIMIT 5000)
            """, snapshots, cancellationToken);
        await DeleteAsync("""
            DELETE FROM "PerfTableSnapshots" WHERE ("Time", "Table") IN (SELECT "Time", "Table" FROM "PerfTableSnapshots" WHERE "Time" < @cutoff LIMIT 5000)
            """, snapshots, cancellationToken);
    }

    private async Task DeleteAsync(string sql, DateTime cutoff, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        for (var batch = 0; batch < MaxBatchesPerTable; batch++)
        {
            var deleted = await db.Database.ExecuteSqlRawAsync(sql, [new NpgsqlParameter<DateTime>("cutoff", cutoff)], cancellationToken);
            if (deleted < BatchSize)
            {
                return;
            }
        }
    }

    private static DateTime HourOf(DateTime utc) => new(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, DateTimeKind.Utc);
}
