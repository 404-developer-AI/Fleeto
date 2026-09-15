using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Services;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleetify.Web.Services;

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
    IReadOnlyList<HistoryPoint> Points);

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

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly TimeProvider _time;

    public CheckHistoryService(IFleetifyDbContextFactory dbFactory, LicenseService licenses, TimeProvider time)
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
        caller.EnsureView();
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

        var unit = certificate ? "days" : CheckCatalog.UnitOf(check.Type, parameters);
        var showThresholds = kind is not (ThresholdKind.Flag or ThresholdKind.ExitCode) && !certificate;
        return new CheckHistoryView(check.Id, check.Name, check.Type, chosen, targets, parameters, kind, unit,
            showThresholds ? check.WarningThreshold : null, showThresholds ? check.CriticalThreshold : null,
            range, from, to, bucket, points);
    }

    private static DateTime Align(DateTime time, TimeSpan bucket, DateTime origin) =>
        origin + TimeSpan.FromTicks((time - origin).Ticks / bucket.Ticks * bucket.Ticks);

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
