using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Web.Services;

/// <summary>One bucket of the check history. Min, Avg and Max are null when the bucket holds no measurement.</summary>
public sealed record HistoryPoint(DateTime Time, double? Min, double? Avg, double? Max, int Values, int Errors, int NoResponses)
{
    public bool HasData => Values + Errors + NoResponses > 0;
}

/// <summary>The history of one check and target on one endpoint over a range, with what is needed to draw and judge it.</summary>
public sealed record CheckHistoryView(
    Guid CheckId,
    string Name,
    CheckType Type,
    string Target,
    IReadOnlyList<string> Targets,
    IReadOnlyDictionary<string, string> Parameters,
    ThresholdKind Kind,
    string Unit,
    double? WarningThreshold,
    double? CriticalThreshold,
    HistoryRange Range,
    DateTime From,
    DateTime To,
    TimeSpan BucketSize,
    IReadOnlyList<HistoryPoint> Points)
{
    /// <summary>
    /// CPU and memory usage checks (0.6.0): per bucket start, the process list of the highest result in that bucket that has one.
    /// Only results of the last 30 days (raw results) have lists, and only those at or above the lowest threshold or run by hand.
    /// </summary>
    public IReadOnlyDictionary<DateTime, ProcessSnapshot> ProcessLists { get; init; } = new Dictionary<DateTime, ProcessSnapshot>();

    /// <summary>
    /// How often the check gets a result. The chart draws the line across empty buckets up to about one interval, so a check that
    /// runs less often than the buckets are wide is a line rather than loose strokes. Null: every empty bucket breaks the line.
    /// </summary>
    public TimeSpan? Interval { get; init; }
}

/// <summary>The process list of one result: when it was measured, its value, and the processes using the most, highest first.</summary>
public sealed record ProcessSnapshot(DateTime At, double Value, IReadOnlyList<ProcessEntry> Processes);

/// <summary>
/// The process list of one CPU or memory usage result as a report for the customer (0.7.0): the copied image, with the chart of
/// the period in <see cref="History"/> and the bucket of the list marked. Users are personal
/// data: the report goes to whoever the user hands it to, so only the export itself is audited, never what it holds.
/// </summary>
public sealed record ProcessListReport(string Hostname, string ClientCode, string ClientName, string SiteName, string CheckName, CheckType Type,
    ProcessSnapshot Snapshot, double? WarningThreshold, double? CriticalThreshold, string GeneratedBy, DateTime GeneratedAt,
    CheckHistoryView History, DateTime Bucket)
{
    /// <summary>A file name without characters a file system refuses: <c>fleeto-cpu-SRV01-2026-10-20-1430.png</c>.</summary>
    public string FileName(string extension)
    {
        var name = $"fleeto-{(Type == CheckType.CpuUsage ? "cpu" : "memory")}-{Hostname}-{Snapshot.At:yyyy-MM-dd-HHmm}";
        var clean = new string(name.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-').ToArray());
        while (clean.Contains("--", StringComparison.Ordinal))
        {
            clean = clean.Replace("--", "-", StringComparison.Ordinal);
        }

        return clean.TrimEnd('-') + "." + extension;
    }
}

/// <summary>
/// Check history per check of an endpoint (ARCHITECTURE.md §4, Check history): the last hour and day from raw results (kept 30 days),
/// the last week, month and year from the hourly and daily rollups the workers maintain (kept 13 months). Every bucket in the range
/// is returned, empty ones included, so a gap in the chart is a real gap. Managed endpoints only.
/// </summary>
public sealed class CheckHistoryService
{
    /// <summary>Raw SQL skips the EF query filters, so each statement filters on the caller's clients itself.</summary>
    private static readonly string RawSql = $$"""
        SELECT date_bin(@bucket, {{CheckHistoryRules.EffectiveTimeSql}}, @origin) AS "Time",
               min(r."Value") FILTER (WHERE r."Error" = '' AND NOT (@reachability AND r."Value" < 0)) AS "Min",
               avg(r."Value") FILTER (WHERE r."Error" = '' AND NOT (@reachability AND r."Value" < 0)) AS "Avg",
               max(r."Value") FILTER (WHERE r."Error" = '' AND NOT (@reachability AND r."Value" < 0)) AS "Max",
               (count(*) FILTER (WHERE r."Error" = '' AND NOT (@reachability AND r."Value" < 0)))::int AS "Values",
               (count(*) FILTER (WHERE r."Error" <> ''))::int AS "Errors",
               (count(*) FILTER (WHERE r."Error" = '' AND @reachability AND r."Value" < 0))::int AS "NoResponses"
        FROM "CheckResults" r
        WHERE r."EndpointId" = @endpointId AND r."CheckDefinitionId" = @checkId AND r."Target" = @target
          AND (@allClients OR r."ClientId" = ANY(@clientIds))
          AND r."Time" >= @from AND r."Time" < @to + interval '7 days'
          AND {{CheckHistoryRules.EffectiveTimeSql}} >= @from AND {{CheckHistoryRules.EffectiveTimeSql}} < @to
        GROUP BY 1
        """;

    /// <summary>Per bucket the highest result that carries a process list. Reads the partial index on results with a list.</summary>
    private static readonly string ProcessListSql = $$"""
        SELECT DISTINCT ON (1) date_bin(@bucket, {{CheckHistoryRules.EffectiveTimeSql}}, @origin) AS "Time",
               {{CheckHistoryRules.EffectiveTimeSql}} AS "At", r."Value", r."ProcessesJson"::text AS "ProcessesJson"
        FROM "CheckResults" r
        WHERE r."EndpointId" = @endpointId AND r."CheckDefinitionId" = @checkId AND r."Target" = @target
          AND r."ProcessesJson" IS NOT NULL AND r."Error" = ''
          AND (@allClients OR r."ClientId" = ANY(@clientIds))
          AND r."Time" >= @from AND r."Time" < @to + interval '7 days'
          AND {{CheckHistoryRules.EffectiveTimeSql}} >= @from AND {{CheckHistoryRules.EffectiveTimeSql}} < @to
        ORDER BY 1, r."Value" DESC, r."Time" DESC
        """;

    private const string RollupSql = """
        SELECT date_bin(@bucket, h."Bucket", @origin) AS "Time",
               min(h."MinValue") AS "Min",
               CASE WHEN sum(h."ValueCount") > 0 THEN sum(h."SumValue") / sum(h."ValueCount") END AS "Avg",
               max(h."MaxValue") AS "Max",
               sum(h."ValueCount")::int AS "Values",
               sum(h."ErrorCount")::int AS "Errors",
               sum(h."NoResponseCount")::int AS "NoResponses"
        FROM "{0}" h
        WHERE h."EndpointId" = @endpointId AND h."CheckDefinitionId" = @checkId AND h."Target" = @target
          AND (@allClients OR h."ClientId" = ANY(@clientIds))
          AND h."Bucket" >= @from AND h."Bucket" < @to
        GROUP BY 1
        """;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly TimeProvider _time;

    public CheckHistoryService(IFleetoDbContextFactory dbFactory, LicenseService licenses, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _time = time;
    }

    /// <summary>
    /// The history of a check that applies to the endpoint. <paramref name="target"/> null picks the first target that has results.
    /// Returns null when the endpoint or check does not exist, the check does not apply, or the endpoint is not managed.
    /// </summary>
    public async Task<CheckHistoryView?> GetAsync(Caller caller, Guid endpointId, Guid checkId, string? target, HistoryRange range,
        CancellationToken cancellationToken = default)
    {
        caller.Ensure(Permissions.ChecksView);
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null ||
            TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) != EndpointTier.Managed)
        {
            return null;
        }

        var checks = await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: true, cancellationToken);
        if (checks.FirstOrDefault(c => c.Id == checkId) is not { } check)
        {
            return null;
        }

        var targets = await db.CheckStates.AsNoTracking()
            .Where(s => s.EndpointId == endpointId && s.CheckDefinitionId == checkId)
            .OrderBy(s => s.Target)
            .Select(s => s.Target)
            .ToListAsync(cancellationToken);
        var chosen = target is not null && (targets.Contains(target) || targets.Count == 0) ? target : targets.FirstOrDefault() ?? string.Empty;

        var parameters = CheckParameters.Parse(check.Definition.ParametersJson);
        var (length, bucket, fromRaw, fromDaily) = CheckHistoryRules.Resolution(range);
        var now = _time.GetUtcNow().UtcDateTime;
        // Buckets are aligned to whole bucket sizes, so a refresh shows the same buckets and the last one is the current one.
        var origin = new DateTime(2000, 1, 3, 0, 0, 0, DateTimeKind.Utc);
        var to = Align(now, bucket, origin) + bucket;
        var from = to - length;

        // The certificate result of an HTTP(S) check is days left, judged against its own parameters rather than the thresholds.
        var certificate = check.Type == CheckType.Http && chosen == CheckEvaluator.CertificateTarget;
        var kind = certificate ? ThresholdKind.LowerIsWorse : CheckCatalog.ThresholdKindOf(check.Type, parameters);
        var reachability = kind == ThresholdKind.Reachability;
#pragma warning disable EF1002 // The rollup table name is one of two constants; every value is a parameter.
        var sql = fromRaw ? RawSql : string.Format(System.Globalization.CultureInfo.InvariantCulture, RollupSql, fromDaily ? "CheckResultsDaily" : "CheckResultsHourly");
        var rows = await db.Database.SqlQueryRaw<HistoryRow>(sql,
                new NpgsqlParameter("bucket", bucket),
                new NpgsqlParameter("origin", origin),
                new NpgsqlParameter("endpointId", endpointId),
                new NpgsqlParameter("checkId", checkId),
                new NpgsqlParameter("target", chosen),
                new NpgsqlParameter("allClients", caller.Scope.AllClients),
                new NpgsqlParameter("clientIds", caller.Scope.ClientIds.ToArray()),
                new NpgsqlParameter("from", from),
                new NpgsqlParameter("to", to),
                new NpgsqlParameter("reachability", reachability))
            .ToListAsync(cancellationToken);
#pragma warning restore EF1002

        var byTime = rows.ToDictionary(r => DateTime.SpecifyKind(r.Time, DateTimeKind.Utc));
        var points = new List<HistoryPoint>();
        for (var t = from; t < to; t += bucket)
        {
            points.Add(byTime.TryGetValue(t, out var row)
                ? new HistoryPoint(t, row.Min, row.Avg, row.Max, row.Values, row.Errors, row.NoResponses)
                : new HistoryPoint(t, null, null, null, 0, 0, 0));
        }

        var processLists = new Dictionary<DateTime, ProcessSnapshot>();
        if (ProcessListRules.Applies(check.Type))
        {
            var listRows = await db.Database.SqlQueryRaw<ProcessListRow>(ProcessListSql,
                    new NpgsqlParameter("bucket", bucket),
                    new NpgsqlParameter("origin", origin),
                    new NpgsqlParameter("endpointId", endpointId),
                    new NpgsqlParameter("checkId", checkId),
                    new NpgsqlParameter("target", chosen),
                    new NpgsqlParameter("allClients", caller.Scope.AllClients),
                    new NpgsqlParameter("clientIds", caller.Scope.ClientIds.ToArray()),
                    new NpgsqlParameter("from", from),
                    new NpgsqlParameter("to", to))
                .ToListAsync(cancellationToken);
            foreach (var row in listRows)
            {
                var processes = ProcessListRules.Parse(row.ProcessesJson);
                if (processes.Count > 0)
                {
                    processLists[DateTime.SpecifyKind(row.Time, DateTimeKind.Utc)] =
                        new ProcessSnapshot(DateTime.SpecifyKind(row.At, DateTimeKind.Utc), row.Value, processes);
                }
            }
        }

        var unit = certificate ? "days" : CheckCatalog.UnitOf(check.Type, parameters);
        var showThresholds = kind is not (ThresholdKind.Flag or ThresholdKind.ExitCode) && !certificate;
        return new CheckHistoryView(check.Id, check.Name, check.Type, chosen, targets, parameters, kind, unit,
            showThresholds ? check.WarningThreshold : null, showThresholds ? check.CriticalThreshold : null,
            range, from, to, bucket, points)
        {
            ProcessLists = processLists,
            Interval = CheckHistoryRules.ExpectedInterval(check.Type, check.IntervalSeconds)
        };
    }

    /// <summary>
    /// The process list of one bucket as a report (0.7.0), read again with the caller's own scope and permission, and the export
    /// audited with the check and the time of the result, never a process or user.
    /// </summary>
    public async Task<ServiceResult<ProcessListReport>> GetProcessListReportAsync(Caller caller, Guid endpointId, Guid checkId, string? target,
        HistoryRange range, DateTime bucket, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.ChecksView))
        {
            return ServiceResult<ProcessListReport>.Forbidden();
        }

        var view = await GetAsync(caller, endpointId, checkId, target, range, cancellationToken);
        if (view is null)
        {
            return ServiceResult<ProcessListReport>.NotFound("check");
        }

        if (!view.ProcessLists.TryGetValue(bucket, out var snapshot))
        {
            return ServiceResult<ProcessListReport>.Fail("This process list is no longer in the history. Close the history and open it again.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking()
            .Where(e => e.Id == endpointId)
            .Select(e => new
            {
                e.Hostname, e.ClientId,
                ClientCode = db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Code).FirstOrDefault(),
                ClientName = db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Name).FirstOrDefault(),
                SiteName = db.Sites.Where(s => s.Id == e.SiteId).Select(s => s.Name).FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult<ProcessListReport>.NotFound("endpoint");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var report = new ProcessListReport(endpoint.Hostname, endpoint.ClientCode ?? string.Empty, endpoint.ClientName ?? string.Empty,
            endpoint.SiteName ?? string.Empty, view.Name, view.Type, snapshot, view.WarningThreshold, view.CriticalThreshold, caller.Name, now,
            view, bucket);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.ProcessListExported, "Endpoint", endpointId.ToString(), endpoint.ClientId,
            new { endpoint.Hostname, CheckId = checkId, Check = view.Name, Type = view.Type.ToString(), snapshot.At }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<ProcessListReport>.Ok(report);
    }

    private static DateTime Align(DateTime time, TimeSpan bucket, DateTime origin) =>
        origin + TimeSpan.FromTicks((time - origin).Ticks / bucket.Ticks * bucket.Ticks);

    private sealed class ProcessListRow
    {
        public DateTime Time { get; set; }
        public DateTime At { get; set; }
        public double Value { get; set; }
        public string? ProcessesJson { get; set; }
    }

    private sealed class HistoryRow
    {
        public DateTime Time { get; set; }
        public double? Min { get; set; }
        public double? Avg { get; set; }
        public double? Max { get; set; }
        public int Values { get; set; }
        public int Errors { get; set; }
        public int NoResponses { get; set; }
    }
}
