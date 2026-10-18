using System.Globalization;

namespace Fleeto.Core.Performance;

/// <summary>
/// The findings of the performance monitor (0.7.0, PERFORMANCE.md §7): plain, explainable rules over one dataset, no learning and
/// no black box. Every finding states what it means, its evidence, the likely cause, what to change and where to look, with the
/// extra waiting it caused in the period. Ranked by that impact, so a short wait that happens a hundred thousand times outranks a
/// long one that happened twice. Thresholds are settings (<see cref="PerfThresholds"/>).
/// </summary>
public static class PerformanceRules
{
    /// <summary>The route of the monitor's own page; its interactions are never a finding.</summary>
    public const string DashboardRoute = "/settings/performance";

    public const int MaxFindings = 25;

    /// <summary>Tables that are written all day: their dead rows matter first.</summary>
    public static readonly IReadOnlyList<string> HotTables = ["Endpoints", "Alerts", "CheckStates", "Jobs", "SigningRequests", "EndpointComponentStates"];

    /// <summary>Hypertables CLAUDE.md expects, and whether they are expected to be compressed.</summary>
    public static readonly IReadOnlyDictionary<string, bool> ExpectedHypertables = new Dictionary<string, bool>(StringComparer.Ordinal)
    {
        ["CheckResults"] = true,
        ["PerfMinutes"] = false
    };

    private const int CriticalPenalty = 30;
    private const int WarningPenalty = 15;
    private const int InfoPenalty = 5;
    private const int MinOverlapMinutes = 10;
    private const long BigTableRows = 100_000;
    private const long DeadRowsFloor = 10_000;

    public static IReadOnlyList<PerfFinding> Evaluate(PerformanceDataset data)
    {
        var findings = new List<PerfFinding>();
        Hosting(data, findings);
        Code(data, findings);
        Database(data, findings);
        Maintenance(data, findings);
        Background(data, findings);
        Agents(data, findings);
        return findings
            .OrderByDescending(f => f.ImpactMs ?? -1)
            .ThenByDescending(f => f.Severity)
            .ThenBy(f => f.Id, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>A score per measured category: 100 minus a penalty per finding, or no data when the category had none.</summary>
    public static IReadOnlyList<PerfCategoryScore> Score(PerformanceDataset data, IReadOnlyList<PerfFinding> findings)
    {
        var scores = new List<PerfCategoryScore>();
        foreach (var info in PerfCategories.All)
        {
            var own = findings.Where(f => f.Category == info.Category).ToList();
            if (!info.Measured || !HasData(data, info.Category))
            {
                scores.Add(new PerfCategoryScore(info, null, info.Measured ? NoDataText(info.Category) : "Not measured in this version.", own.Count));
                continue;
            }

            var penalty = own.Sum(f => f.Severity switch
            {
                PerfSeverity.Critical => CriticalPenalty,
                PerfSeverity.Warning => WarningPenalty,
                _ => InfoPenalty
            });
            var worst = own.OrderByDescending(f => f.Severity).ThenByDescending(f => f.ImpactMs ?? -1).FirstOrDefault();
            scores.Add(new PerfCategoryScore(info, Math.Max(0, 100 - penalty), worst?.Title ?? "No bottleneck found in this period.", own.Count));
        }

        return scores;
    }

    public static bool HasData(PerformanceDataset data, PerfCategory category) => category switch
    {
        PerfCategory.Hosting => data.Host is { Minutes: > 0 } || data.Runtime.Count > 0,
        PerfCategory.Code => data.Series.Any(s => s.Kind is PerfKind.Interaction or PerfKind.Http or PerfKind.External) || data.Runtime.Count > 0,
        PerfCategory.Database => data.Series.Any(s => s.Kind == PerfKind.Db) || data.Statements.Count > 0,
        PerfCategory.Maintenance => data.Database is not null || data.Tables.Count > 0,
        PerfCategory.Background => data.Series.Any(s => s.Kind is PerfKind.WorkerRun or PerfKind.Broadcast or PerfKind.Notification),
        PerfCategory.Agents => data.Series.Any(s => s.Kind is PerfKind.AgentMessage or PerfKind.Connection or PerfKind.JobDispatch),
        _ => false
    };

    private static string NoDataText(PerfCategory category) => category switch
    {
        PerfCategory.Hosting => "No VPS or runtime figures in this period. Check that the workers run and the collectors VPS and Runtime are on.",
        PerfCategory.Code => "No page interactions or requests in this period.",
        PerfCategory.Database => "No database commands or statement statistics in this period.",
        PerfCategory.Maintenance => "No database snapshot in this period. The workers read one every 15 minutes.",
        PerfCategory.Background => "No worker runs or live updates in this period.",
        PerfCategory.Agents => "No agent traffic in this period: no agents connected, or the collector Agent channel is off.",
        _ => "No data in this period."
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Hosting
    // ---------------------------------------------------------------------------------------------------------------

    private static void Hosting(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        if (data.Host is not { Minutes: > 0 } host)
        {
            return;
        }

        var userTime = UserFacingTime(data);
        var fleetoCpu = data.Runtime.Sum(r => r.CpuPercentAverage ?? 0);

        if (host.CpuAverage is { } cpu && cpu >= settings.Threshold(PerfThresholds.HostCpuWarning))
        {
            var critical = host.CpuP95 is >= 95;
            var others = Math.Max(0, cpu - fleetoCpu);
            findings.Add(new PerfFinding(
                "hosting-cpu-high", PerfCategory.Hosting, critical ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"The VPS CPU averaged {Pct(cpu)} (95th percentile {Pct(host.CpuP95)})",
                "The processors of the VPS were busy most of the period, so every request, page and loop waited for CPU time.",
                [
                    new("Average CPU", Pct(cpu)),
                    new("95th percentile", Pct(host.CpuP95)),
                    new("Cores", host.Cores?.ToString(CultureInfo.InvariantCulture) ?? "–"),
                    new("Of which this instance", Pct(fleetoCpu)),
                    new("Other processes on the VPS", Pct(others))
                ],
                others > fleetoCpu
                    ? "Most of the CPU went to processes outside this instance: other instances on the VPS, the database or the system."
                    : "This instance itself uses most of the CPU. The busiest component is in the runtime table.",
                others > fleetoCpu
                    ? "Move instances to another VPS or give this one more cores. Changing the code of this instance helps little."
                    : "Look at the findings under Backend code and Background for the work that costs most; otherwise give the VPS more cores.",
                [new("component", "VPS")],
                userTime * Math.Clamp((cpu - settings.Threshold(PerfThresholds.HostCpuWarning)) / 100, 0, 1)));
        }

        if (host.StealAverage is { } steal && steal >= settings.Threshold(PerfThresholds.HostStealWarning))
        {
            findings.Add(new PerfFinding(
                "hosting-cpu-steal", PerfCategory.Hosting,
                steal >= 2 * settings.Threshold(PerfThresholds.HostStealWarning) ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"The hypervisor withheld {Pct(steal)} of the CPU (CPU steal)",
                "CPU steal is time the VPS wanted to run but the hypervisor gave to other virtual machines on the same physical host.",
                [new("Average steal", Pct(steal)), new("Highest minute", Pct(host.StealMax))],
                "A noisy neighbour at the VPS provider, or a VPS plan with shared CPU that is oversold.",
                "Ask the provider to move the VPS, or switch to a plan with dedicated CPU. Code changes do not help here.",
                [new("component", "VPS")],
                userTime * Math.Clamp(steal / 100, 0, 1)));
        }

        if (host is { Load1Max: { } load, Cores: > 0 and var cores } && load > 2 * cores)
        {
            findings.Add(new PerfFinding(
                "hosting-load", PerfCategory.Hosting, PerfSeverity.Warning,
                $"The load of the VPS reached {Num(load, "0.0")} on {cores} cores",
                "The load is the number of processes that are running or waiting for CPU or disk. Above twice the cores they queue.",
                [new("Highest load", Num(load, "0.0")), new("Cores", cores.ToString(CultureInfo.InvariantCulture)), new("Average iowait", Pct(host.IowaitAverage))],
                host.IowaitAverage is > 10 ? "Processes wait for the disk (high iowait)." : "More work than the cores can handle at peak times.",
                "Spread heavy worker loops in time, or give the VPS more cores. With high iowait, look at disk latency first.",
                [new("component", "VPS")],
                null));
        }

        if (host is { MemoryTotalBytes: > 0 and var total, MemoryAvailableMinBytes: { } available })
        {
            var share = 100d * available / total;
            var warning = settings.Threshold(PerfThresholds.HostMemoryWarning);
            if (share < warning)
            {
                var swapUsed = host is { SwapTotalBytes: > 0 and var swapTotal, SwapFreeMinBytes: { } swapFree } ? swapTotal - swapFree : 0;
                findings.Add(new PerfFinding(
                    "hosting-memory-low", PerfCategory.Hosting, share < warning / 2 ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"The VPS had only {Pct(share)} of its memory available",
                    "With little memory left the database cache shrinks and the system starts swapping, which makes everything slower.",
                    [new("Lowest available", Bytes(available)), new("Total", Bytes(total)), new("Swap in use", Bytes(swapUsed))],
                    "The instances, the database and the system together need more memory than the VPS has.",
                    "Give the VPS more memory, or move an instance away. The runtime table shows the working set per component.",
                    [new("component", "VPS")],
                    null));
            }
        }

        if (host.DiskLatencyAverageMs is { } latency && latency >= settings.Threshold(PerfThresholds.HostDiskLatency))
        {
            findings.Add(new PerfFinding(
                "hosting-disk-latency", PerfCategory.Hosting, latency >= 5 * settings.Threshold(PerfThresholds.HostDiskLatency) ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"A disk operation took {Ms(latency)} on average",
                "Every database write and every read that misses the cache waits for the disk.",
                [new("Average latency", Ms(latency)), new("Highest utilization", Pct(host.DiskUtilizationMaxPercent)), new("Average iowait", Pct(host.IowaitAverage))],
                "A slow or shared disk at the VPS provider, or more disk work than it can handle.",
                "Check the disk plan of the VPS (SSD or NVMe), and the cache hit ratio and temporary files under Database maintenance.",
                [new("component", "VPS")],
                null));
        }

        if (host.ThrottledPercent is > 10 and var throttled)
        {
            findings.Add(new PerfFinding(
                "hosting-throttled", PerfCategory.Hosting, PerfSeverity.Warning,
                $"The container was throttled {Pct(throttled)} of the time",
                "A CPU limit on the container made its processes wait although the VPS may have had CPU to spare.",
                [new("Throttled", Pct(throttled))],
                "A CPU limit in the Compose file of the instance.",
                "Raise or remove the CPU limit of the workers container in the Compose file.",
                [new("component", "workers")],
                null));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Backend code
    // ---------------------------------------------------------------------------------------------------------------

    private static void Code(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        var minSamples = settings.Threshold(PerfThresholds.MinSamples);
        var dbShareLimit = settings.Threshold(PerfThresholds.DatabaseShare) / 100;

        foreach (var flow in data.Of(PerfKind.Interaction).Where(s => s.Count >= minSamples && s.Key != DashboardRoute))
        {
            var slow = settings.Threshold(PerfThresholds.SlowInteraction);
            var what = flow.SubKey == "load" ? "loads" : "events";
            if (flow.P95 is { } p95 && p95 > slow)
            {
                findings.Add(new PerfFinding(
                    $"code-slow-interaction:{flow.Key}:{flow.SubKey}", PerfCategory.Code, p95 > 3 * slow ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"Page {flow.Key} {what}: 95th percentile {Ms(p95)} over {Count(flow.Count)} {what}",
                    flow.SubKey == "load"
                        ? "Loading this page took longer than the threshold for one in twenty users. The time runs from the start of the page's load until its data is shown."
                        : "Clicks and other events on this page took longer than the threshold for one in twenty. The time runs from the event until it was handled and rendered.",
                    FlowEvidence(flow),
                    Cause(flow, dbShareLimit),
                    Action(flow, dbShareLimit),
                    [new("page", flow.Key)],
                    PerfHistogram.TimeAbove(flow.Histogram, slow, flow.Max)));
            }
            else if (flow.DbShare >= dbShareLimit && flow.P95 is { } p && p > slow / 2)
            {
                findings.Add(new PerfFinding(
                    $"code-database-bound:{flow.Key}:{flow.SubKey}", PerfCategory.Code, PerfSeverity.Info,
                    $"Page {flow.Key} {what} spend {Pct(flow.DbShare * 100)} of their time in the database",
                    "The page is not slow yet, but almost all of its time is database work, so it gets slower as the tables grow.",
                    FlowEvidence(flow),
                    "The queries of the page, not its rendering.",
                    "Look at the queries of the page's service: an index, fewer columns, or one query instead of several.",
                    [new("page", flow.Key)],
                    null));
            }

            var maxQueries = settings.Threshold(PerfThresholds.QueriesPerInteraction);
            if (flow.DbPerCall > maxQueries)
            {
                findings.Add(new PerfFinding(
                    $"code-many-queries:{flow.Key}:{flow.SubKey}", PerfCategory.Code, flow.DbPerCall > 5 * maxQueries ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"Page {flow.Key} sends {Num(flow.DbPerCall, "0.#")} database commands per {(flow.SubKey == "load" ? "load" : "event")}",
                    "Each command is a round trip to the database. Many of them per interaction usually means a query in a loop (N+1).",
                    FlowEvidence(flow),
                    "A query per item of a list, or a separate query per section of the page.",
                    "Load the items in one query (a join or Include, or a WHERE ... IN), or load sections together. Diagnose (0.7.0 step 2) will name the repeated statement.",
                    [new("page", flow.Key)],
                    flow.DbMs * (flow.DbPerCall - maxQueries) / flow.DbPerCall));
            }
        }

        foreach (var flow in data.Of(PerfKind.Http).Where(s => s.Count >= minSamples))
        {
            var slow = settings.Threshold(PerfThresholds.SlowRequest);
            if (flow.P95 is { } p95 && p95 > slow)
            {
                findings.Add(new PerfFinding(
                    $"code-slow-route:{flow.Key}:{flow.SubKey}", PerfCategory.Code, p95 > 3 * slow ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"{flow.SubKey} {flow.Key}: 95th percentile {Ms(p95)} over {Count(flow.Count)} requests",
                    "One in twenty requests on this route took longer than the threshold.",
                    FlowEvidence(flow),
                    Cause(flow, dbShareLimit),
                    Action(flow, dbShareLimit),
                    [new("api", flow.Key)],
                    PerfHistogram.TimeAbove(flow.Histogram, slow, flow.Max)));
            }

            var largeBytes = settings.Threshold(PerfThresholds.LargeResponse) * 1024;
            if (flow.Count > 0 && flow.Bytes / (double)flow.Count > largeBytes && !IsDownload(flow.Key))
            {
                findings.Add(new PerfFinding(
                    $"code-large-response:{flow.Key}", PerfCategory.Code, PerfSeverity.Info,
                    $"{flow.Key} answers {Bytes((long)(flow.Bytes / (double)flow.Count))} per request on average",
                    "Large responses take time to build, to send and to read for the integration that asked.",
                    [new("Requests", Count(flow.Count)), new("Average size", Bytes((long)(flow.Bytes / (double)flow.Count))), new("Total", Bytes(flow.Bytes))],
                    "A page size that is large, or fields the caller does not need.",
                    "Check the default page size and the fields of the route in API.md; keyset pages keep each response bounded.",
                    [new("api", flow.Key)],
                    null));
            }
        }

        foreach (var host in data.Of(PerfKind.External).GroupBy(s => s.Key))
        {
            var calls = host.Sum(s => s.Count);
            var errors = host.Sum(s => s.Errors);
            var limited = host.Where(s => s.SubKey == "429").Sum(s => s.Count);
            var errorShare = calls == 0 ? 0 : 100d * errors / calls;
            if (calls >= minSamples && errorShare >= settings.Threshold(PerfThresholds.ExternalErrors) || limited > 0)
            {
                findings.Add(new PerfFinding(
                    $"code-external-errors:{host.Key}", PerfCategory.Code, errorShare >= 25 ? PerfSeverity.Critical : PerfSeverity.Warning,
                    limited > 0 ? $"{host.Key} answered {Count(limited)} calls with 429 Too Many Requests" : $"{Pct(errorShare)} of the calls to {host.Key} failed",
                    "Failed and rate-limited calls are retried with backoff, so the work behind them waits.",
                    [new("Calls", Count(calls)), new("Failed", Count(errors)), new("Rate limited (429)", Count(limited)),
                        new("95th percentile", Ms(Merge(host).P95))],
                    limited > 0 ? "More calls than the external product allows." : "The external product is unavailable or refuses the calls.",
                    limited > 0
                        ? "Check the request budget of the connector (Action1: 20 a minute for the instance) and what calls it most."
                        : "Check the integration in Settings, Integrations, and the workers log for the reason.",
                    [new("host", host.Key)],
                    null));
            }
        }

        foreach (var runtime in data.Runtime)
        {
            if (runtime.DbPoolTimeouts > 0)
            {
                findings.Add(new PerfFinding(
                    $"code-db-pool-timeouts:{runtime.Component}", PerfCategory.Code, PerfSeverity.Critical,
                    $"{runtime.Component} waited in vain for a database connection {Count(runtime.DbPoolTimeouts)} times",
                    "Every connection of the pool was busy until the wait timed out, so the work behind it failed.",
                    [new("Timeouts", Count(runtime.DbPoolTimeouts)), new("Busy at peak", runtime.DbPoolBusyMax.ToString(CultureInfo.InvariantCulture)),
                        new("Pool size", runtime.DbPoolMax > 0 ? runtime.DbPoolMax.ToString(CultureInfo.InvariantCulture) : "–")],
                    "Long transactions or slow queries hold connections, or more parallel work than the pool allows.",
                    "Find the slow queries under Database queries; keep transactions short. Raise the pool size only after that.",
                    [new("component", runtime.Component)],
                    null));
            }
            else if (runtime.DbPoolMax > 0 && runtime.DbPoolBusyMax >= runtime.DbPoolMax)
            {
                findings.Add(new PerfFinding(
                    $"code-db-pool-full:{runtime.Component}", PerfCategory.Code, PerfSeverity.Warning,
                    $"Every database connection of {runtime.Component} was busy at its peak",
                    "When the pool is full, new work waits for a connection before its first query.",
                    [new("Busy at peak", runtime.DbPoolBusyMax.ToString(CultureInfo.InvariantCulture)), new("Pool size", runtime.DbPoolMax.ToString(CultureInfo.InvariantCulture))],
                    "Slow queries, long transactions, or bursts of parallel work.",
                    "Find the slow queries under Database queries and the loops that run at the same time under Background.",
                    [new("component", runtime.Component)],
                    null));
            }

            if (runtime.ThreadPoolQueueMax > settings.Threshold(PerfThresholds.ThreadPoolQueue))
            {
                findings.Add(new PerfFinding(
                    $"code-thread-pool:{runtime.Component}", PerfCategory.Code, PerfSeverity.Warning,
                    $"Up to {Count(runtime.ThreadPoolQueueMax)} work items waited for a thread in {runtime.Component}",
                    "Work queued for the .NET thread pool waits until a thread is free, which delays every request and event.",
                    [new("Queue at peak", Count(runtime.ThreadPoolQueueMax)), new("Threads at peak", runtime.ThreadPoolThreadsMax.ToString(CultureInfo.InvariantCulture))],
                    "Blocking calls on async paths (.Result, .Wait(), synchronous I/O) or CPU-heavy work on the thread pool.",
                    "Look for synchronous waits in the hot paths of the component and make them async.",
                    [new("component", runtime.Component)],
                    null));
            }

            if (runtime.GcPausePercent > settings.Threshold(PerfThresholds.GcPause))
            {
                findings.Add(new PerfFinding(
                    $"code-gc-pause:{runtime.Component}", PerfCategory.Code,
                    runtime.GcPausePercent > 2 * settings.Threshold(PerfThresholds.GcPause) ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"{runtime.Component} was paused for garbage collection {Pct(runtime.GcPausePercent)} of the time",
                    "While the garbage collector pauses, the whole process stands still.",
                    [new("Pause share", Pct(runtime.GcPausePercent)), new("Allocated", Bytes(runtime.AllocatedBytes)), new("Largest heap", Bytes(runtime.HeapMaxBytes))],
                    "Many or large allocations: big lists loaded into memory, strings built in loops, large buffers.",
                    "Load less per query (projections, paging), reuse buffers, and stream large data instead of loading it whole.",
                    [new("component", runtime.Component)],
                    null));
            }

            var overhead = runtime.Cores > 0 ? runtime.OverheadCpuPercent / runtime.Cores : runtime.OverheadCpuPercent;
            if (runtime.Minutes > 0 && overhead > settings.Threshold(PerfThresholds.MonitorOverhead))
            {
                findings.Add(new PerfFinding(
                    $"code-monitor-overhead:{runtime.Component}", PerfCategory.Code, PerfSeverity.Warning,
                    $"The performance monitor cost {Pct(overhead)} of the CPU of {runtime.Component}",
                    "The monitor is meant to cost less than 1 % on level Basic; this is more.",
                    [new("Monitor time", Ms(runtime.OverheadMs)), new("Minutes", runtime.Minutes.ToString(CultureInfo.InvariantCulture))],
                    "Many distinct keys, or a collector that does more work than it should.",
                    "Switch off collectors one by one in the Settings tab to find it, and report it.",
                    [new("component", runtime.Component)],
                    null));
            }
        }

        foreach (var component in data.Of(PerfKind.Exception).GroupBy(s => s.Component))
        {
            var minutes = data.Runtime.FirstOrDefault(r => r.Component == component.Key)?.Minutes ?? Math.Max(1, (int)data.Length.TotalMinutes);
            var total = component.Sum(s => s.Count);
            if (minutes > 0 && total / (double)minutes > 60)
            {
                var top = component.OrderByDescending(s => s.Count).Take(5).ToList();
                findings.Add(new PerfFinding(
                    $"code-exceptions:{component.Key}", PerfCategory.Code, PerfSeverity.Info,
                    $"{component.Key} threw {Num(total / (double)minutes, "0")} exceptions a minute",
                    "An exception is expensive: the runtime captures a stack trace for each one. Many per minute cost CPU even when they are handled.",
                    top.Select(s => new PerfEvidence(s.Key, Count(s.Count))).ToList(),
                    "Exceptions used for control flow, or a failure that repeats.",
                    "Find where the most frequent type is thrown and check for it beforehand instead.",
                    [new("component", component.Key)],
                    null));
            }
        }
    }

    private static List<PerfEvidence> FlowEvidence(PerfSeries flow) =>
    [
        new("Count", Count(flow.Count)),
        new("Median", Ms(flow.P50)),
        new("95th percentile", Ms(flow.P95)),
        new("99th percentile", Ms(flow.P99)),
        new("Database share", Pct(flow.DbShare * 100)),
        new("Database commands per call", Num(flow.DbPerCall, "0.#")),
        new("External share", Pct(flow.ExternalShare * 100)),
        new("Errors", Count(flow.Errors))
    ];

    private static string Cause(PerfSeries flow, double dbShareLimit) =>
        flow.DbShare >= dbShareLimit ? $"Database work: {Pct(flow.DbShare * 100)} of the time went to {Num(flow.DbPerCall, "0.#")} commands per call."
        : flow.ExternalShare >= 0.5 ? $"A call to an external product: {Pct(flow.ExternalShare * 100)} of the time."
        : "Our own code or rendering: most of the time was neither database nor external calls.";

    private static string Action(PerfSeries flow, double dbShareLimit) =>
        flow.DbShare >= dbShareLimit ? "Check the queries behind it under Database queries: a missing index, a query per item, or more rows than shown."
        : flow.ExternalShare >= 0.5 ? "Move the external call to the workers, or cache its answer."
        : "Look for work in loops, large lists rendered at once, or synchronous waits in the code behind it.";

    private static bool IsDownload(string route) =>
        route.Contains("download", StringComparison.OrdinalIgnoreCase) || route.Contains("export", StringComparison.OrdinalIgnoreCase) ||
        route.Contains("releases", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------------------------------------------------------
    // Database queries
    // ---------------------------------------------------------------------------------------------------------------

    private static void Database(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        var total = data.Statements.Sum(s => s.TotalMs);
        var shareLimit = settings.Threshold(PerfThresholds.StatementShare) / 100;
        var slow = settings.Threshold(PerfThresholds.SlowStatement);
        var minSamples = settings.Threshold(PerfThresholds.MinSamples);
        var reported = new HashSet<long>();

        if (total > 0)
        {
            foreach (var statement in data.Statements.OrderByDescending(s => s.TotalMs).Take(5).Where(s => s.TotalMs / total >= shareLimit))
            {
                reported.Add(statement.QueryId);
                var share = statement.TotalMs / total;
                findings.Add(new PerfFinding(
                    $"database-top-statement:{statement.QueryId.ToString(CultureInfo.InvariantCulture)}", PerfCategory.Database,
                    statement.MeanMs > slow || share >= 0.3 ? PerfSeverity.Warning : PerfSeverity.Info,
                    $"One statement took {Pct(share * 100)} of all database time ({Ms(statement.TotalMs)} over {Count(statement.Calls)} calls)",
                    "This statement costs the database more than any other. Making it cheaper, or calling it less often, helps everything that shares the database.",
                    StatementEvidence(statement, share),
                    statement.MeanMs > slow ? "Each call is slow: it reads many rows or misses an index." : "Each call is fast, but it runs very often.",
                    statement.MeanMs > slow
                        ? "Check its plan for sequential scans on large tables and add the index it needs, or select fewer rows."
                        : "Find the caller and call it less often: cache the result, batch the calls, or load it once per page.",
                    [new("query", statement.QueryId.ToString(CultureInfo.InvariantCulture), statement.Query)],
                    statement.TotalMs));
            }
        }

        foreach (var statement in data.Statements.Where(s => !reported.Contains(s.QueryId) && s.Calls >= minSamples && s.MeanMs > slow)
                     .OrderByDescending(s => (s.MeanMs - slow) * s.Calls).Take(10))
        {
            findings.Add(new PerfFinding(
                $"database-slow-statement:{statement.QueryId.ToString(CultureInfo.InvariantCulture)}", PerfCategory.Database, PerfSeverity.Warning,
                $"A statement took {Ms(statement.MeanMs)} per call over {Count(statement.Calls)} calls",
                "Each call of this statement is slower than the threshold.",
                StatementEvidence(statement, total > 0 ? statement.TotalMs / total : 0),
                "It reads many rows, misses an index, or waits for locks.",
                "Check its plan for sequential scans on large tables and add the index it needs, or select fewer rows.",
                [new("query", statement.QueryId.ToString(CultureInfo.InvariantCulture), statement.Query)],
                (statement.MeanMs - slow) * statement.Calls));
        }

        foreach (var table in data.Tables.Where(t => t is { SequentialScans: >= 100, Rows: >= BigTableRows }))
        {
            var perScan = table.SequentialRowsRead / (double)table.SequentialScans!.Value;
            if (perScan < 10_000)
            {
                continue;
            }

            findings.Add(new PerfFinding(
                $"database-seq-scan:{table.Name}", PerfCategory.Database, PerfSeverity.Warning,
                $"Table {table.Name} was read in full {Count(table.SequentialScans!.Value)} times",
                "A sequential scan reads every row of the table. On a large table that costs time and pushes useful data out of the cache.",
                [new("Sequential scans", Count(table.SequentialScans!.Value)), new("Rows read per scan", Count((long)perScan)),
                    new("Index scans", table.IndexScans is { } idx ? Count(idx) : "–"), new("Rows", Count(table.Rows)), new("Size", Bytes(table.Bytes))],
                "A query filters on a column without an index, or the index cannot be used for its condition.",
                "Find the statements on this table under the top statements, check which column they filter on and add an index for it in a migration.",
                [new("table", table.Name)],
                null));
        }

        if (data.Database is { Baseline: true, TempBytes: { } temp } database && temp > settings.Threshold(PerfThresholds.TempBytes) * 1024 * 1024)
        {
            findings.Add(new PerfFinding(
                "database-temp-files", PerfCategory.Database, PerfSeverity.Warning,
                $"The database wrote {Bytes(temp)} to temporary files",
                "A sort or hash that does not fit in work_mem spills to disk, which is much slower than memory.",
                [new("Temporary files", database.TempFiles is { } files ? Count(files) : "–"), new("Written", Bytes(temp))],
                "A query that sorts or groups many rows, often a report or an export without a limit.",
                "Find the statement with the most temporary blocks in the statement table and make it sort fewer rows, or add an index in its sort order.",
                [new("component", "database")],
                null));
        }
    }

    private static List<PerfEvidence> StatementEvidence(PerfStatementDelta statement, double share) =>
    [
        new("Calls", Count(statement.Calls)),
        new("Total time", Ms(statement.TotalMs)),
        new("Mean per call", Ms(statement.MeanMs)),
        new("Share of database time", Pct(share * 100)),
        new("Rows", Count(statement.Rows)),
        new("Blocks read from disk", Count(statement.SharedBlocksRead)),
        new("Temporary blocks written", Count(statement.TempBlocksWritten))
    ];

    // ---------------------------------------------------------------------------------------------------------------
    // Database maintenance
    // ---------------------------------------------------------------------------------------------------------------

    private static void Maintenance(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        if (data.StatementsProblem is { } problem)
        {
            findings.Add(new PerfFinding(
                "maintenance-statistics-missing", PerfCategory.Maintenance, PerfSeverity.Warning,
                "Statement statistics could not be read",
                "Without pg_stat_statements the page cannot say which queries cost the database most.",
                [new("Reason", problem)],
                "The extension is not installed, or the workers role lacks pg_monitor.",
                "Run install.sh for this instance again: it installs pg_stat_statements and grants pg_monitor to the workers.",
                [new("setting", "pg_stat_statements")],
                null));
        }

        if (data.Database is { Baseline: true, CacheHitRatio: { } hit } && hit * 100 < settings.Threshold(PerfThresholds.CacheHit))
        {
            var limit = settings.Threshold(PerfThresholds.CacheHit);
            findings.Add(new PerfFinding(
                "maintenance-cache-hit", PerfCategory.Maintenance, hit * 100 < limit - 4 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"Only {Pct(hit * 100)} of the reads came from the database cache",
                "A read that misses the cache goes to the disk, which is many times slower.",
                [new("Cache hit ratio", Pct(hit * 100)), new("Threshold", Pct(limit))],
                "The tables that are read often no longer fit in shared_buffers, or large scans push them out.",
                "Fix the sequential scans first; if the hot data is simply larger than the cache, give the database more memory.",
                [new("setting", "shared_buffers")],
                null));
        }

        if (data.Database is { WraparoundAge: var age } && age > 500_000_000)
        {
            findings.Add(new PerfFinding(
                "maintenance-wraparound", PerfCategory.Maintenance, age > 1_000_000_000 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"The oldest transaction id is {Count(age)} transactions old",
                "PostgreSQL must freeze old rows before 2 billion transactions or it stops accepting writes. Autovacuum does this on its own when it keeps up.",
                [new("Age", Count(age))],
                "Autovacuum cannot finish on a large table, or a long transaction holds it back.",
                "Check the tables with the most dead rows and the last autovacuum below; end long-running transactions.",
                [new("setting", "autovacuum")],
                null));
        }

        var deadLimit = settings.Threshold(PerfThresholds.DeadTuples) / 100;
        foreach (var table in data.Tables.Where(t => t.DeadRows >= DeadRowsFloor))
        {
            var share = table.DeadRows / (double)Math.Max(1, table.LiveRows + table.DeadRows);
            if (share < deadLimit)
            {
                continue;
            }

            var hot = HotTables.Contains(table.Name);
            findings.Add(new PerfFinding(
                $"maintenance-dead-rows:{table.Name}", PerfCategory.Maintenance, hot ? PerfSeverity.Warning : PerfSeverity.Info,
                $"{Pct(share * 100)} of the rows of {table.Name} are dead",
                "Dead rows are left behind by updates and deletes until vacuum removes them. Until then every scan reads them too.",
                [new("Dead rows", Count(table.DeadRows)), new("Live rows", Count(table.LiveRows)),
                    new("Last vacuum", table.LastVacuum is { } at ? at.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture) : "never")],
                hot ? "A table that is updated all day, faster than autovacuum keeps up with." : "Many updates or deletes since the last vacuum.",
                hot
                    ? "Lower the autovacuum scale factor of this table in a migration (ALTER TABLE ... SET (autovacuum_vacuum_scale_factor = 0.02)), or update it less often."
                    : "Usually autovacuum catches up by itself; check again in a day.",
                [new("table", table.Name)],
                null));
        }

        if (data.Tables.Count > 0)
        {
            var anyHypertable = data.Tables.Any(t => t.IsHypertable);
            foreach (var (name, compressed) in ExpectedHypertables)
            {
                if (data.Tables.FirstOrDefault(t => t.Name == name) is not { } table || !anyHypertable)
                {
                    continue;
                }

                if (!table.IsHypertable || !table.RetentionPolicy || (compressed && !table.Compressed))
                {
                    findings.Add(new PerfFinding(
                        $"maintenance-hypertable:{name}", PerfCategory.Maintenance, PerfSeverity.Warning,
                        !table.IsHypertable ? $"{name} is not a hypertable" : $"{name} has no {(table.RetentionPolicy ? "compression" : "retention")} policy",
                        "Time-series tables are TimescaleDB hypertables with retention (and compression where they are large), so old rows are dropped by chunk instead of deleted row by row.",
                        [new("Hypertable", table.IsHypertable ? "yes" : "no"), new("Compression", table.Compressed ? "yes" : "no"),
                            new("Retention policy", table.RetentionPolicy ? "yes" : "no"), new("Size", Bytes(table.Bytes))],
                        "The migration that sets it up ran before TimescaleDB was installed.",
                        "Run install.sh for the instance again; migration DatabaseRules and PerformanceMonitor set the hypertables up when the extension exists.",
                        [new("table", name)],
                        null));
                }
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Background & realtime
    // ---------------------------------------------------------------------------------------------------------------

    private static void Background(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        var minSamples = settings.Threshold(PerfThresholds.MinSamples);

        foreach (var loop in data.Of(PerfKind.WorkerRun))
        {
            if (int.TryParse(loop.SubKey, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) && seconds > 0 &&
                loop.P95 is { } p95 && p95 > seconds * 1000d && loop.Count >= 3)
            {
                findings.Add(new PerfFinding(
                    $"background-loop-slow:{loop.Key}", PerfCategory.Background, PerfSeverity.Warning,
                    $"Loop {loop.Key} runs {Ms(p95)} (95th percentile), longer than its interval of {Ms(seconds * 1000d)}",
                    "A loop that takes longer than its interval is always running, so its work falls behind and it competes with everything else.",
                    LoopEvidence(loop),
                    loop.DbShare >= 0.5 ? "Its database work." : loop.ExternalShare >= 0.5 ? "Calls to an external product." : "The work it does per run.",
                    "Make one run do less (smaller batches, only what changed), or lengthen its interval.",
                    [new("loop", loop.Key)],
                    loop.Sum - loop.Count * seconds * 1000d is > 0 and var extra ? extra : null));
            }

            if (loop.Errors > 0)
            {
                findings.Add(new PerfFinding(
                    $"background-loop-failures:{loop.Key}", PerfCategory.Background, loop.ErrorShare > 0.1 ? PerfSeverity.Warning : PerfSeverity.Info,
                    $"Loop {loop.Key} failed {Count(loop.Errors)} of {Count(loop.Count)} runs",
                    "A failed run is retried with backoff, so its work waits.",
                    LoopEvidence(loop),
                    "An exception in the loop; the workers log names it.",
                    "Read the workers log for the loop name and fix the cause.",
                    [new("loop", loop.Key)],
                    null));
            }
        }

        Overlap(data, findings);

        var minutes = Math.Max(1, data.Length.TotalMinutes);
        foreach (var broadcast in data.Of(PerfKind.Broadcast))
        {
            var perMinute = broadcast.Count / minutes;
            if (perMinute > settings.Threshold(PerfThresholds.BroadcastsPerMinute) && broadcast.Mean >= 5)
            {
                findings.Add(new PerfFinding(
                    $"background-broadcast:{broadcast.Key}", PerfCategory.Background, PerfSeverity.Warning,
                    $"Live update {broadcast.Key} went out {Num(perMinute, "0")} times a minute to {Num(broadcast.Mean, "0")} pages on average",
                    "Every live update makes the open pages that listen to it reload their data, so a storm of them multiplies by the number of open pages.",
                    [new("Updates", Count(broadcast.Count)), new("Per minute", Num(perMinute, "0.#")), new("Pages reached on average", Num(broadcast.Mean, "0.#")),
                        new("Busiest minute", Count(broadcast.PeakCount))],
                    "A notification sent per row instead of per batch, or pages that listen to more than they show.",
                    "Send one notification per batch, and let pages debounce and filter on the id they show.",
                    [new("component", "LiveUpdates")],
                    null));
            }
        }

        foreach (var channel in data.Of(PerfKind.Notification).Where(s => s.Count >= minSamples))
        {
            if (channel.P95 is { } p95 && p95 > settings.Threshold(PerfThresholds.NotificationLag))
            {
                findings.Add(new PerfFinding(
                    $"background-notification-lag:{channel.Component}:{channel.Key}", PerfCategory.Background, PerfSeverity.Warning,
                    $"Notifications on {channel.Key} in {channel.Component} waited and ran {Ms(p95)} (95th percentile)",
                    "A notification wakes up the work behind it; when its handler is slow, everything after it in the queue waits.",
                    [new("Notifications", Count(channel.Count)), new("95th percentile", Ms(p95)), new("Largest", Ms(channel.Max))],
                    "A slow handler, or a burst of notifications.",
                    "Keep the handler short: wake a loop instead of doing the work in the handler.",
                    [new("component", channel.Component), new("channel", channel.Key)],
                    null));
            }
        }
    }

    /// <summary>
    /// Does a loop slow pages down? Compares page interactions in minutes the loop ran with minutes it did not. Needs enough minutes
    /// on both sides and an absolute gap in the 95th percentile, not just a ratio, so a quiet period does not count as evidence.
    /// </summary>
    private static void Overlap(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        var gap = settings.Threshold(PerfThresholds.LoopOverlap);
        var minSamples = settings.Threshold(PerfThresholds.MinSamples);
        var busy = data.Minutes.Where(m => m.Interactions > 0).ToList();
        foreach (var loop in busy.SelectMany(m => m.Loops).Distinct(StringComparer.Ordinal))
        {
            var with = busy.Where(m => m.Loops.Contains(loop)).ToList();
            var without = busy.Where(m => !m.Loops.Contains(loop)).ToList();
            if (with.Count < MinOverlapMinutes || without.Count < MinOverlapMinutes)
            {
                continue;
            }

            var withHistogram = with.Aggregate((int[]?)null, (total, m) => PerfHistogram.Add(total, m.InteractionHistogram));
            var withoutHistogram = without.Aggregate((int[]?)null, (total, m) => PerfHistogram.Add(total, m.InteractionHistogram));
            var withCount = PerfHistogram.Total(withHistogram);
            if (withCount < minSamples || PerfHistogram.Total(withoutHistogram) < minSamples)
            {
                continue;
            }

            var p95With = PerfHistogram.Percentile(withHistogram, 0.95) ?? 0;
            var p95Without = PerfHistogram.Percentile(withoutHistogram, 0.95) ?? 0;
            if (p95With - p95Without < gap || p95With < 1.5 * p95Without)
            {
                continue;
            }

            var meanGap = (PerfHistogram.Mean(withHistogram) ?? 0) - (PerfHistogram.Mean(withoutHistogram) ?? 0);
            findings.Add(new PerfFinding(
                $"background-loop-overlap:{loop}", PerfCategory.Background, PerfSeverity.Warning,
                $"Pages are slower while loop {loop} runs: 95th percentile {Ms(p95With)} against {Ms(p95Without)}",
                "In the minutes this loop ran, page interactions were clearly slower than in the minutes it did not, so it competes with users for the database or CPU.",
                [new("Minutes with the loop", with.Count.ToString(CultureInfo.InvariantCulture)), new("Minutes without", without.Count.ToString(CultureInfo.InvariantCulture)),
                    new("95th percentile with", Ms(p95With)), new("95th percentile without", Ms(p95Without)), new("Interactions with the loop", Count(withCount))],
                "The loop does heavy database work or holds locks the pages need.",
                "Make the loop work in smaller batches with short transactions, or move its heavy part to a quieter time.",
                [new("loop", loop)],
                meanGap > 0 ? meanGap * withCount : null));
        }
    }

    private static List<PerfEvidence> LoopEvidence(PerfSeries loop) =>
    [
        new("Runs", Count(loop.Count)),
        new("Median", Ms(loop.P50)),
        new("95th percentile", Ms(loop.P95)),
        new("Longest", Ms(loop.Max)),
        new("Database share", Pct(loop.DbShare * 100)),
        new("External share", Pct(loop.ExternalShare * 100)),
        new("Failed", Count(loop.Errors))
    ];

    // ---------------------------------------------------------------------------------------------------------------
    // Agent channel
    // ---------------------------------------------------------------------------------------------------------------

    private static void Agents(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        var minSamples = settings.Threshold(PerfThresholds.MinSamples);
        var ingest = settings.Threshold(PerfThresholds.IngestP95);

        foreach (var message in data.Of(PerfKind.AgentMessage).Where(s => s.Count >= minSamples))
        {
            var isIngest = message.Key == "CheckResults";
            var limit = isIngest ? ingest : 2 * ingest;
            if (message.P95 is { } p95 && p95 > limit)
            {
                findings.Add(new PerfFinding(
                    $"agents-slow-message:{message.Key}:{message.SubKey}", PerfCategory.Agents,
                    isIngest ? (p95 > 4 * ingest ? PerfSeverity.Critical : PerfSeverity.Warning) : PerfSeverity.Info,
                    $"{message.Key} from the {message.SubKey} took {Ms(p95)} to store and acknowledge (95th percentile)",
                    isIngest
                        ? "The agent keeps check results on its disk until the gateway acknowledges them; a slow acknowledgement makes its buffer grow and delays alerts."
                        : "The gateway handles messages of one connection in order, so a slow message delays the next one of that agent.",
                    [new("Messages", Count(message.Count)), new("Median", Ms(message.P50)), new("95th percentile", Ms(p95)),
                        new("Database share", Pct(message.DbShare * 100)), new("Average size", Bytes(message.Count == 0 ? 0 : message.Bytes / message.Count))],
                    message.DbShare >= 0.5 ? "The database write behind it." : "Work in the gateway itself, or a busy thread pool.",
                    "Look at its handler under src/Fleeto.Gateway/Sessions and the store method it calls under src/Fleeto.Gateway/Data.",
                    [new("message", message.Key)],
                    null));
            }

            if (message.Count > 0 && message.Bytes / (double)message.Count > settings.Threshold(PerfThresholds.AgentPayload) * 1024)
            {
                findings.Add(new PerfFinding(
                    $"agents-payload:{message.Key}:{message.SubKey}", PerfCategory.Agents, PerfSeverity.Info,
                    $"{message.Key} from the {message.SubKey} is {Bytes(message.Bytes / message.Count)} on average",
                    "Large messages cost bandwidth on the agent's link and memory in the gateway while they are parsed.",
                    [new("Messages", Count(message.Count)), new("Average size", Bytes(message.Bytes / message.Count)), new("Total", Bytes(message.Bytes))],
                    "The agent sends more than changed, or more often than needed.",
                    "Send only what changed since the last message, or split it.",
                    [new("message", message.Key)],
                    null));
            }
        }

        foreach (var job in data.Of(PerfKind.JobDispatch).Where(s => s.Count >= 3))
        {
            if (job.P95 is { } p95 && p95 > settings.Threshold(PerfThresholds.JobDelivery))
            {
                findings.Add(new PerfFinding(
                    $"agents-job-delivery:{job.Key}", PerfCategory.Agents, PerfSeverity.Warning,
                    $"{job.Key} jobs reached their online agent after {Ms(p95)} (95th percentile)",
                    "The time from a signed job being ready, or the agent coming online, until the gateway sent it.",
                    [new("Jobs", Count(job.Count)), new("Median", Ms(job.P50)), new("95th percentile", Ms(p95)), new("Longest", Ms(job.Max))],
                    "The signer is slow to sign, or the gateway missed the notification and found the job on its next pass.",
                    "Compare with the signing figures of the signer; check that the gateway receives fleeto_jobs.",
                    [new("job", job.Key)],
                    null));
            }
        }

        var accepted = data.Of(PerfKind.Connection).Where(s => s.Key == "accepted").ToList();
        var peak = accepted.Sum(s => s.PeakCount);
        if (peak > settings.Threshold(PerfThresholds.ReconnectsPerMinute))
        {
            var refused = data.Of(PerfKind.Connection).Where(s => s.Key == "refused").Sum(s => s.Count);
            findings.Add(new PerfFinding(
                "agents-reconnect-storm", PerfCategory.Agents, PerfSeverity.Warning,
                $"Up to {Count(peak)} agent connections were accepted in one minute",
                "When many agents connect at once, each one re-sends its buffered data and gets its jobs and configuration again, which floods the gateway and the database.",
                [new("Busiest minute", Count(peak)), new("Accepted in the period", Count(accepted.Sum(s => s.Count))), new("Refused", Count(refused))],
                "A restart of the gateway or the host proxy, or a network problem that dropped many connections together.",
                "Check the gateway log for the time of the busiest minute; agents spread their reconnects with jitter, which should keep the peak low.",
                [new("component", "gateway")],
                null));
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Formatting: culture-invariant, "1.5 s", never "1,5 s".
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The time users and integrations waited for pages and requests in the period.</summary>
    private static double UserFacingTime(PerformanceDataset data) =>
        data.Series.Where(s => s.Kind is PerfKind.Interaction or PerfKind.Http && s.Key != DashboardRoute).Sum(s => s.Sum);

    private static PerfSeries Merge(IEnumerable<PerfSeries> series)
    {
        var list = series.ToList();
        var histogram = list.Aggregate((int[]?)null, (total, s) => PerfHistogram.Add(total, s.Histogram)) ?? [];
        var first = list[0];
        return first with
        {
            SubKey = string.Empty,
            Count = list.Sum(s => s.Count),
            Errors = list.Sum(s => s.Errors),
            Sum = list.Sum(s => s.Sum),
            Max = list.Max(s => s.Max),
            Histogram = histogram,
            Bytes = list.Sum(s => s.Bytes),
            DbCount = list.Sum(s => s.DbCount),
            DbMs = list.Sum(s => s.DbMs),
            ExternalMs = list.Sum(s => s.ExternalMs),
            PeakCount = list.Sum(s => s.PeakCount)
        };
    }

    public static string Ms(double? milliseconds)
    {
        if (milliseconds is not { } ms || double.IsNaN(ms))
        {
            return "–";
        }

        return ms switch
        {
            < 1 => ms.ToString("0.##", CultureInfo.InvariantCulture) + " ms",
            < 1000 => ms.ToString("0", CultureInfo.InvariantCulture) + " ms",
            < 60_000 => (ms / 1000).ToString("0.#", CultureInfo.InvariantCulture) + " s",
            < 3_600_000 => (ms / 60_000).ToString("0.#", CultureInfo.InvariantCulture) + " min",
            _ => (ms / 3_600_000).ToString("0.#", CultureInfo.InvariantCulture) + " h"
        };
    }

    public static string Pct(double? percent) => percent switch
    {
        null or double.NaN => "–",
        > 0 and < 0.1 => "<0.1%",
        { } p => p.ToString(p < 10 ? "0.#" : "0", CultureInfo.InvariantCulture) + "%"
    };

    public static string Count(long value) => value.ToString("#,0", CultureInfo.InvariantCulture);

    public static string Num(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);

    public static string Bytes(long bytes) => bytes switch
    {
        < 1024 => bytes.ToString(CultureInfo.InvariantCulture) + " B",
        < 1024 * 1024 => (bytes / 1024d).ToString("0.#", CultureInfo.InvariantCulture) + " KB",
        < 1024L * 1024 * 1024 => (bytes / 1024d / 1024).ToString("0.#", CultureInfo.InvariantCulture) + " MB",
        _ => (bytes / 1024d / 1024 / 1024).ToString("0.##", CultureInfo.InvariantCulture) + " GB"
    };
}
