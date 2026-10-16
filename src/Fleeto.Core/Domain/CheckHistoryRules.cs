using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>The time ranges of the check history.</summary>
public enum HistoryRange
{
    Hour,
    Day,
    Week,
    Month,
    Year
}

/// <summary>
/// Rules of the check history (ARCHITECTURE.md §4, Check history): which time a result belongs to, which values count as a
/// measurement, and the resolution per range. Shared by the workers (rollups) and web (charts).
/// </summary>
public static class CheckHistoryRules
{
    /// <summary>How far before its ingest a result may have been collected (an agent that was offline buffers results).</summary>
    public static readonly TimeSpan MaximumBufferAge = TimeSpan.FromDays(7);

    /// <summary>How far an agent clock may run ahead of the server before its time is not used.</summary>
    public static readonly TimeSpan MaximumClockAhead = TimeSpan.FromMinutes(5);

    /// <summary>Hourly and daily rollups are kept this long (13 months and a few days, so a year always fits).</summary>
    public static readonly TimeSpan RollupRetention = TimeSpan.FromDays(400);

    /// <summary>
    /// The time a result is shown at: the agent's collection time when it is plausible, otherwise the ingest time. Ordering and
    /// evaluation still use ingest time only (CLAUDE.md, clock skew): this is for the charts.
    /// </summary>
    public static DateTime EffectiveTime(DateTime agentTime, DateTime ingestTime) =>
        agentTime >= ingestTime - MaximumBufferAge && agentTime <= ingestTime + MaximumClockAhead ? agentTime : ingestTime;

    /// <summary>SQL twin of <see cref="EffectiveTime"/> for alias <c>r</c> of "CheckResults".</summary>
    public const string EffectiveTimeSql =
        """(CASE WHEN r."AgentTime" >= r."Time" - interval '7 days' AND r."AgentTime" <= r."Time" + interval '5 minutes' THEN r."AgentTime" ELSE r."Time" END)""";

    public static DateTime HourBucket(DateTime time) => new(time.Year, time.Month, time.Day, time.Hour, 0, 0, DateTimeKind.Utc);

    public static DateTime DayBucket(DateTime time) => new(time.Year, time.Month, time.Day, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>True for the -1 of a network check that got no answer: counted separately, never part of the chart values.</summary>
    public static bool IsNoResponse(CheckType type, string target, IReadOnlyDictionary<string, string> parameters, double value) =>
        value < 0 && Enum.IsDefined(type) && !(type == CheckType.Http && target == CheckEvaluator.CertificateTarget) &&
        CheckCatalog.ThresholdKindOf(type, parameters) == ThresholdKind.Reachability;

    /// <summary>
    /// How often a check is expected to get a result, so the chart can tell an empty bucket between two results from a real gap.
    /// A check Fleeto evaluates itself has no interval of its own: a folder growth check follows the daily storage scan, the others
    /// the patch sync every 4 hours.
    /// </summary>
    public static TimeSpan ExpectedInterval(CheckType type, int intervalSeconds) =>
        !Enum.IsDefined(type) || !CheckCatalog.Get(type).EvaluatedByFleeto ? TimeSpan.FromSeconds(Math.Max(intervalSeconds, 0))
        : type == CheckType.FolderGrowth ? TimeSpan.FromDays(1)
        : TimeSpan.FromSeconds(CheckCatalog.FleetoEvaluatedIntervalSeconds);

    /// <summary>
    /// The longest time between two buckets with results that the chart still draws as one line: one interval with room for a
    /// result that came a little late, plus one bucket for where in its bucket a result falls. A longer stretch means at least
    /// one result is missing, a real gap.
    /// </summary>
    public static TimeSpan MaximumBridge(TimeSpan interval, TimeSpan bucket) => interval * 1.5 + bucket;

    /// <summary>The length of a range, its bucket size, and whether it reads raw results (hour, day) or rollups.</summary>
    public static (TimeSpan Length, TimeSpan Bucket, bool FromRaw, bool FromDaily) Resolution(HistoryRange range) => range switch
    {
        HistoryRange.Hour => (TimeSpan.FromHours(1), TimeSpan.FromMinutes(1), true, false),
        HistoryRange.Day => (TimeSpan.FromDays(1), TimeSpan.FromMinutes(10), true, false),
        HistoryRange.Week => (TimeSpan.FromDays(7), TimeSpan.FromHours(1), false, false),
        HistoryRange.Month => (TimeSpan.FromDays(30), TimeSpan.FromHours(6), false, false),
        _ => (TimeSpan.FromDays(365), TimeSpan.FromDays(1), false, true)
    };
}
