using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleeto.Core.Performance;

/// <summary>A collector of the performance monitor that an admin can switch off on its own (PERFORMANCE.md §8).</summary>
public enum PerfCollector
{
    Interactions,
    Http,
    Database,
    Workers,
    External,
    Runtime,
    Host,
    Snapshots,
    AgentChannel,
    Signer,
    LiveUpdates
}

public sealed record PerfCollectorInfo(PerfCollector Collector, string Label, string Description);

/// <summary>A tunable threshold of the findings, with its default and the range the settings page accepts.</summary>
public sealed record PerfThresholdInfo(string Key, PerfCategory Category, string Label, string Description, string Unit, double Default, double Min,
    double Max);

/// <summary>
/// The settings of the performance monitor (0.7.0, PERFORMANCE.md §8), one row in the database as JSON, read by all four
/// components. Not secret, so not in the encrypted settings store. Defaults are in code: an absent value means the default, so a
/// threshold added later takes its default on every instance, and a collector added later is on.
/// </summary>
public sealed record PerformanceSettings
{
    public const int MinRetentionDays = 1;

    /// <summary>The ceiling of <see cref="DiagnoseMaxMinutes"/>: Diagnose is for an investigation, not for leaving on.</summary>
    public const int MaxDiagnoseMinutes = 24 * 60;

    /// <summary>Off or Basic. Diagnose is not a level of its own but Basic with <see cref="DiagnoseUntil"/>.</summary>
    public PerfLevel Level { get; init; } = PerfLevel.Basic;

    /// <summary>
    /// The end of Diagnose on server time, or null. Diagnose is on while the clock is before it, evaluated on every read, so it
    /// always ends by itself (spec §2.4). Set by starting and stopping Diagnose, never by the settings form.
    /// </summary>
    public DateTime? DiagnoseUntil { get; init; }

    /// <summary>How long Diagnose runs when an admin starts it without choosing.</summary>
    public int DiagnoseDefaultMinutes { get; init; } = 60;

    /// <summary>The longest Diagnose an admin can start.</summary>
    public int DiagnoseMaxMinutes { get; init; } = 240;

    /// <summary>Statement statistics and the database are read this often while Diagnose runs.</summary>
    public int DiagnoseStatementSnapshotMinutes { get; init; } = 5;

    /// <summary>Tables are read this often while Diagnose runs.</summary>
    public int DiagnoseTableSnapshotMinutes { get; init; } = 15;

    /// <summary>Collectors switched off. Stored as the ones that are off, so a collector added later is on.</summary>
    public IReadOnlyList<PerfCollector> DisabledCollectors { get; init; } = [];

    /// <summary>An interaction, request or worker run slower than this is kept on its own, with its split.</summary>
    public int SlowEventMs { get; init; } = 1000;

    public int StatementSnapshotMinutes { get; init; } = 15;
    public int TableSnapshotMinutes { get; init; } = 60;

    public int MinuteRetentionDays { get; init; } = 7;
    public int HourRetentionDays { get; init; } = 90;
    public int EventRetentionDays { get; init; } = 14;
    public int SnapshotRetentionDays { get; init; } = 30;

    /// <summary>Thresholds that differ from their default, by <see cref="PerfThresholdInfo.Key"/>.</summary>
    public IReadOnlyDictionary<string, double> Thresholds { get; init; } = new Dictionary<string, double>();

    public static PerformanceSettings Default { get; } = new();

    [JsonIgnore]
    public bool IsOn => Level != PerfLevel.Off;

    public bool Collects(PerfCollector collector) => IsOn && !DisabledCollectors.Contains(collector);

    /// <summary>Whether Diagnose runs at <paramref name="utcNow"/> (server time): the level is on and its end is later.</summary>
    public bool IsDiagnosing(DateTime utcNow) => IsOn && DiagnoseUntil is { } until && utcNow < until;

    /// <summary>The level that runs at <paramref name="utcNow"/>: Diagnose while it lasts, else the stored level.</summary>
    public PerfLevel EffectiveLevel(DateTime utcNow) => IsDiagnosing(utcNow) ? PerfLevel.Diagnose : Level;

    /// <summary>How often statement statistics are read at <paramref name="utcNow"/>.</summary>
    public int StatementSnapshotMinutesAt(DateTime utcNow) =>
        IsDiagnosing(utcNow) ? Math.Min(StatementSnapshotMinutes, DiagnoseStatementSnapshotMinutes) : StatementSnapshotMinutes;

    /// <summary>How often tables are read at <paramref name="utcNow"/>.</summary>
    public int TableSnapshotMinutesAt(DateTime utcNow) =>
        IsDiagnosing(utcNow) ? Math.Min(TableSnapshotMinutes, DiagnoseTableSnapshotMinutes) : TableSnapshotMinutes;

    /// <summary>The value of a threshold: the stored one within its range, else the default.</summary>
    public double Threshold(string key)
    {
        var info = PerfThresholds.Find(key) ?? throw new ArgumentException($"Unknown performance threshold {key}.", nameof(key));
        return Thresholds.TryGetValue(key, out var value) && value >= info.Min && value <= info.Max ? value : info.Default;
    }

    /// <summary>Why these settings cannot be saved, or null. Checked by the server whatever the page sent.</summary>
    public string? Problem()
    {
        if (Level is not (PerfLevel.Off or PerfLevel.Basic))
        {
            return "Choose a level: Off or Basic. Diagnose is started from the top of the page and ends by itself.";
        }

        if (DisabledCollectors.Any(c => !Enum.IsDefined(c)))
        {
            return "A collector in the list does not exist. Refresh the page and try again.";
        }

        if (SlowEventMs is < 100 or > 600_000)
        {
            return "A slow event is between 100 ms and 10 minutes.";
        }

        if (DiagnoseMaxMinutes is < 15 or > MaxDiagnoseMinutes)
        {
            return $"The longest Diagnose is between 15 minutes and {MaxDiagnoseMinutes / 60} hours.";
        }

        if (DiagnoseDefaultMinutes < 5 || DiagnoseDefaultMinutes > DiagnoseMaxMinutes)
        {
            return "The default length of Diagnose is at least 5 minutes and at most its longest.";
        }

        if (DiagnoseStatementSnapshotMinutes is < 1 or > 60 || DiagnoseTableSnapshotMinutes is < 5 or > 1440)
        {
            return "In Diagnose, statement statistics are read every 1 to 60 minutes and table statistics every 5 minutes to once a day.";
        }

        if (StatementSnapshotMinutes is < 5 or > 60)
        {
            return "Statement statistics are read every 5 to 60 minutes.";
        }

        if (TableSnapshotMinutes is < 15 or > 1440)
        {
            return "Table statistics are read every 15 minutes to once a day.";
        }

        if (MinuteRetentionDays is < MinRetentionDays or > PerfRetention.MaxMinuteDays)
        {
            return $"Minute data is kept 1 to {PerfRetention.MaxMinuteDays} days.";
        }

        if (HourRetentionDays < MinuteRetentionDays || HourRetentionDays > 400)
        {
            return "Hour data is kept at least as long as minute data, and at most 400 days.";
        }

        if (EventRetentionDays is < MinRetentionDays or > 90 || SnapshotRetentionDays is < MinRetentionDays or > 90)
        {
            return "Slow events and database snapshots are kept 1 to 90 days.";
        }

        foreach (var (key, value) in Thresholds)
        {
            if (PerfThresholds.Find(key) is not { } info)
            {
                return $"The threshold {key} does not exist. Refresh the page and try again.";
            }

            if (double.IsNaN(value) || value < info.Min || value > info.Max)
            {
                return $"\"{info.Label}\" is between {Format(info.Min)} and {Format(info.Max)} {info.Unit}.";
            }
        }

        return null;
    }

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public string Serialize() => JsonSerializer.Serialize(this, Json);

    /// <summary>The stored settings, or the defaults when nothing or something unreadable is stored.</summary>
    public static PerformanceSettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            return JsonSerializer.Deserialize<PerformanceSettings>(json, Json) ?? Default;
        }
        catch (JsonException)
        {
            return Default;
        }
    }

    private static string Format(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

public static class PerfRetention
{
    /// <summary>
    /// The longest minute data is kept. Where TimescaleDB is installed its retention policy drops older chunks of the minute
    /// table as a ceiling; the workers delete per the setting below it.
    /// </summary>
    public const int MaxMinuteDays = 30;
}

public static class PerfCollectors
{
    public static readonly IReadOnlyList<PerfCollectorInfo> All =
    [
        new(PerfCollector.Interactions, "Page interactions", "Page loads and events of the Blazor pages, per route template."),
        new(PerfCollector.Http, "HTTP requests", "The public API, sign-in and downloads, per route template."),
        new(PerfCollector.Database, "Database commands", "Time and count of database commands per source, and the connection pool."),
        new(PerfCollector.Workers, "Worker loops", "Every run of every workers loop, with its database time."),
        new(PerfCollector.External, "External calls", "Outbound HTTP calls per host: Action1, Microsoft, webhooks, S3."),
        new(PerfCollector.Runtime, "Runtime", "CPU, memory, garbage collection, thread pool and exceptions of every component."),
        new(PerfCollector.Host, "VPS", "CPU with steal and iowait, load, memory, swap, disk latency and throttling, every minute."),
        new(PerfCollector.Snapshots, "Database statistics", "Statement statistics, database and table statistics from PostgreSQL."),
        new(PerfCollector.AgentChannel, "Agent channel", "Connections, messages, acknowledgement times and job delivery in the gateway."),
        new(PerfCollector.Signer, "Signer", "Signing requests per kind: wait, time to sign and outcome."),
        new(PerfCollector.LiveUpdates, "Live updates and notifications", "Live updates to open pages and database notifications per channel."),
    ];
}

/// <summary>The thresholds of the findings (PERFORMANCE.md §7). Keys are stored, so they never change.</summary>
public static class PerfThresholds
{
    public const string HostCpuWarning = "hosting.cpuWarningPercent";
    public const string HostStealWarning = "hosting.stealWarningPercent";
    public const string HostMemoryWarning = "hosting.memoryAvailableWarningPercent";
    public const string HostDiskLatency = "hosting.diskLatencyWarningMs";
    public const string SlowInteraction = "code.slowInteractionMs";
    public const string SlowRequest = "code.slowRequestMs";
    public const string QueriesPerInteraction = "code.queriesPerInteraction";
    public const string DatabaseShare = "code.databaseSharePercent";
    public const string LargeResponse = "code.largeResponseKb";
    public const string ExternalErrors = "code.externalErrorPercent";
    public const string GcPause = "code.gcPausePercent";
    public const string ThreadPoolQueue = "code.threadPoolQueue";
    public const string NPlusOne = "code.nPlusOneRepeats";
    public const string MonitorOverhead = "code.monitorOverheadPercent";
    public const string MinSamples = "code.minSamples";
    public const string StatementShare = "database.statementSharePercent";
    public const string SlowStatement = "database.slowStatementMs";
    public const string TempBytes = "database.tempMb";
    public const string CacheHit = "maintenance.cacheHitPercent";
    public const string DeadTuples = "maintenance.deadTuplePercent";
    public const string LoopOverlap = "background.overlapGapMs";
    public const string NotificationLag = "background.notificationLagMs";
    public const string BroadcastsPerMinute = "background.broadcastsPerMinute";
    public const string IngestP95 = "agents.ingestP95Ms";
    public const string JobDelivery = "agents.jobDeliveryP95Ms";
    public const string ReconnectsPerMinute = "agents.reconnectsPerMinute";
    public const string AgentPayload = "agents.payloadKb";

    public static readonly IReadOnlyList<PerfThresholdInfo> All =
    [
        new(HostCpuWarning, PerfCategory.Hosting, "VPS CPU", "Average CPU of the VPS above which the hosting is a bottleneck.", "%", 80, 10, 100),
        new(HostStealWarning, PerfCategory.Hosting, "CPU steal", "CPU time the hypervisor gave to other VMs: a noisy neighbour.", "%", 5, 1, 50),
        new(HostMemoryWarning, PerfCategory.Hosting, "Available memory", "Lowest available memory of the VPS before it is under pressure.", "%", 10, 1, 50),
        new(HostDiskLatency, PerfCategory.Hosting, "Disk latency", "Average time per disk operation above which the disk is slow.", "ms", 20, 1, 1000),
        new(SlowInteraction, PerfCategory.Code, "Slow page interaction", "95th percentile of a page load or event above which it is slow.", "ms", 1000, 100, 60_000),
        new(SlowRequest, PerfCategory.Code, "Slow request", "95th percentile of an API or sign-in request above which it is slow.", "ms", 1000, 100, 60_000),
        new(QueriesPerInteraction, PerfCategory.Code, "Queries per interaction",
            "Average database commands per page load or event above which the page asks too much.", "commands", 20, 2, 1000),
        new(DatabaseShare, PerfCategory.Code, "Database share", "Share of the time of a slow page or request spent in the database.", "%", 70, 10, 100),
        new(LargeResponse, PerfCategory.Code, "Large response", "Average response size of an API route above which it is large.", "KB", 1024, 16, 102_400),
        new(ExternalErrors, PerfCategory.Code, "External errors", "Share of failed calls to an external host.", "%", 5, 0.1, 100),
        new(GcPause, PerfCategory.Code, "Garbage collection pauses", "Share of time a component spent paused for garbage collection.", "%", 5, 0.5, 50),
        new(ThreadPoolQueue, PerfCategory.Code, "Thread pool queue", "Work items waiting for a thread, at the peak of a minute.", "items", 100, 5, 100_000),
        new(NPlusOne, PerfCategory.Code, "N+1 repeats",
            "Times one statement may run within one interaction, request or run before it counts as a query per item (Diagnose).", "times", 10, 2, 1000),
        new(MonitorOverhead, PerfCategory.Code, "Monitor overhead", "CPU the monitor itself may use on level Basic; Diagnose may use three times as much.", "%", 1, 0.1, 20),
        new(MinSamples, PerfCategory.Code, "Minimum samples", "Fewest occurrences of a flow before a finding about it is made.", "occurrences", 20, 1, 10_000),
        new(StatementShare, PerfCategory.Database, "Statement share", "Share of all database time one statement takes before it is a top statement.", "%", 10, 1, 100),
        new(SlowStatement, PerfCategory.Database, "Slow statement", "Mean time of a statement above which it is slow.", "ms", 100, 1, 60_000),
        new(TempBytes, PerfCategory.Database, "Temporary files", "Data written to temporary files in the period: sorts and hashes that did not fit in memory.", "MB", 100, 1, 100_000),
        new(CacheHit, PerfCategory.Maintenance, "Cache hit ratio", "Share of reads served from memory below which the database reads too much from disk.", "%", 99, 50, 100),
        new(DeadTuples, PerfCategory.Maintenance, "Dead rows", "Share of dead rows in a table above which vacuum falls behind.", "%", 20, 1, 100),
        new(LoopOverlap, PerfCategory.Background, "Worker overlap", "How much slower pages are in minutes a loop runs before it counts as the cause.", "ms", 200, 10, 60_000),
        new(NotificationLag, PerfCategory.Background, "Notification lag", "95th percentile of a notification waiting and being handled.", "ms", 1000, 10, 600_000),
        new(BroadcastsPerMinute, PerfCategory.Background, "Live updates per minute", "Live updates of one kind per minute to every open page.", "updates", 60, 1, 100_000),
        new(IngestP95, PerfCategory.Agents, "Check result ingest", "95th percentile from receiving check results to the acknowledgement.", "ms", 500, 10, 60_000),
        new(JobDelivery, PerfCategory.Agents, "Job delivery", "95th percentile from the moment a job could be delivered (signed, agent online) to its delivery.", "ms", 10_000, 100, 3_600_000),
        new(ReconnectsPerMinute, PerfCategory.Agents, "Reconnects per minute", "Agent connections per minute above which agents reconnect in a storm.", "connections", 100, 5, 100_000),
        new(AgentPayload, PerfCategory.Agents, "Agent message size", "Average size of a message type above which it is large.", "KB", 256, 1, 16_384),
    ];

    private static readonly Dictionary<string, PerfThresholdInfo> ByKey = All.ToDictionary(t => t.Key, StringComparer.Ordinal);

    public static PerfThresholdInfo? Find(string key) => ByKey.GetValueOrDefault(key);
}
