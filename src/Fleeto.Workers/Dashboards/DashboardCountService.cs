using Fleeto.Infrastructure.Data;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Workers.Dashboards;

/// <summary>
/// Counts the alerts opened per client, UTC day and severity for the charts of the dashboard (0.8.0, DASHBOARDS.md §6), every 10
/// minutes: today and yesterday again, and on the first run every day the alerts still go back to. A chart over 90 days then reads
/// at most 90 rows per client instead of the alerts, and keeps its history after the alerts themselves are removed by retention.
/// Counts older than <see cref="KeepDays"/> are removed.
/// </summary>
public sealed class DashboardCountService : WorkerLoop
{
    public const int KeepDays = 400;

    private const string CountSql = """
        INSERT INTO "DashboardDailyCounts" ("ClientId", "Day", "Warning", "Critical", "CountedAt")
        SELECT a."ClientId", (a."OpenedAt" AT TIME ZONE 'UTC')::date,
               count(*) FILTER (WHERE a."Severity" = 'Warning'), count(*) FILTER (WHERE a."Severity" = 'Critical'), @now
        FROM "Alerts" a
        WHERE a."OpenedAt" >= @from
        GROUP BY 1, 2
        ON CONFLICT ("ClientId", "Day") DO UPDATE
        SET "Warning" = EXCLUDED."Warning", "Critical" = EXCLUDED."Critical", "CountedAt" = EXCLUDED."CountedAt"
        """;

    private readonly IFleetoDbContextFactory _dbFactory;

    public DashboardCountService(IFleetoDbContextFactory dbFactory, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<DashboardCountService> logger)
        : base("dashboard-counts", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(10);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await CountAsync(cancellationToken);
        return false;
    }

    /// <summary>One pass; returns the number of client days written.</summary>
    public async Task<int> CountAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var first = !await db.DashboardDailyCounts.AnyAsync(cancellationToken);
        var from = first ? now.Date.AddDays(-KeepDays) : now.Date.AddDays(-1);
        var written = await db.Database.ExecuteSqlRawAsync(CountSql, [new NpgsqlParameter("now", now), new NpgsqlParameter("from", from)],
            cancellationToken);
        var oldest = DateOnly.FromDateTime(now).AddDays(-KeepDays);
        await db.DashboardDailyCounts.Where(c => c.Day < oldest).ExecuteDeleteAsync(cancellationToken);
        return written;
    }
}
