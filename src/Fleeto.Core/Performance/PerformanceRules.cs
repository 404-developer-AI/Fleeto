using System.Globalization;
using Fleeto.Core.Domain;

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
        NPlusOne(data, findings);
        IdleInTransaction(data, findings);
        Database(data, findings);
        AppStatements(data, findings);
        LocksAndIndexes(data, findings);
        Maintenance(data, findings);
        Background(data, findings);
        Agents(data, findings);
        Frontend(data, findings);
        Network(data, findings);
        Budgets(data, findings);
        Regressions(data, findings);
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
        PerfCategory.Database => data.Series.Any(s => s.Kind is PerfKind.Db or PerfKind.Statement) || data.Statements.Count > 0,
        PerfCategory.Maintenance => data.Database is not null || data.Tables.Count > 0,
        PerfCategory.Background => data.Series.Any(s => s.Kind is PerfKind.WorkerRun or PerfKind.Broadcast or PerfKind.Notification),
        PerfCategory.Agents => data.Series.Any(s => s.Kind is PerfKind.AgentMessage or PerfKind.Connection or PerfKind.JobDispatch),
        PerfCategory.Frontend => data.Series.Any(s => s.Kind == PerfKind.Vital),
        PerfCategory.Network => data.Series.Any(s => s.Kind == PerfKind.Network),
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
        PerfCategory.Frontend or PerfCategory.Network =>
            "No measurements from browsers in this period: nobody used Fleeto, the collector Browser is off, or its share of page loads is 0%.",
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
                    data.Of(PerfKind.NPlusOne).Any(n => n.Key == SourceOf(flow))
                        ? "Load the items in one query (a join or Include, or a WHERE ... IN), or load sections together. The N+1 finding of this page names the repeated statement and its caller."
                        : "Load the items in one query (a join or Include, or a WHERE ... IN), or load sections together. Start Diagnose for an hour: it names the repeated statement and the method that sends it.",
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
            var enough = calls >= minSamples;
            if (enough && errorShare >= settings.Threshold(PerfThresholds.ExternalErrors) || limited > 0)
            {
                // Only a share of enough calls is critical, and only critical reaches instance health: a list fetched every few
                // hours that is refused once fails 100% of its one call, and is simply fetched again at its next attempt.
                findings.Add(new PerfFinding(
                    $"code-external-errors:{host.Key}", PerfCategory.Code, enough && errorShare >= 25 ? PerfSeverity.Critical : PerfSeverity.Warning,
                    limited > 0 ? $"{host.Key} answered {Count(limited)} calls with 429 Too Many Requests" : $"{Pct(errorShare)} of the calls to {host.Key} failed",
                    "Failed and rate-limited calls are retried with backoff, so the work behind them waits.",
                    [new("Calls", Count(calls)), new("Failed", Count(errors)), new("Rate limited (429)", Count(limited)),
                        new("95th percentile", Ms(Merge(host).P95))],
                    limited > 0 ? "More calls than the external product allows." : "The external product is unavailable or refuses the calls.",
                    limited > 0
                        ? RateLimitAction(host.Key)
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
            if (runtime.Minutes > 0 && overhead > OverheadAllowance(runtime, settings.Threshold(PerfThresholds.MonitorOverhead)))
            {
                findings.Add(new PerfFinding(
                    $"code-monitor-overhead:{runtime.Component}", PerfCategory.Code, PerfSeverity.Warning,
                    $"The performance monitor cost {Pct(overhead)} of the CPU of {runtime.Component}",
                    "The monitor is meant to cost less than its threshold on level Basic, and three times that in Diagnose; this is more.",
                    [new("Monitor time", Ms(runtime.OverheadMs)), new("Minutes", runtime.Minutes.ToString(CultureInfo.InvariantCulture)),
                        new("Of which in Diagnose", runtime.DiagnoseMinutes.ToString(CultureInfo.InvariantCulture)),
                        new("Allowed", Pct(OverheadAllowance(runtime, settings.Threshold(PerfThresholds.MonitorOverhead))))],
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
                var types = component.GroupBy(s => s.Key)
                    .Select(g => (Type: g.Key, Count: g.Sum(s => s.Count), Throwers: g.Where(s => s.SubKey.Length > 0).OrderByDescending(s => s.Count).ToList()))
                    .OrderByDescending(t => t.Count).Take(5).ToList();
                var evidence = types.Select(t => new PerfEvidence(t.Type, Count(t.Count))).ToList();
                var refs = new List<PerfCodeRef> { new("component", component.Key) };
                if (types[0].Throwers.Count > 0)
                {
                    // Diagnose names the class that threw and the kind of work it happened in (spec §5, Runtime).
                    evidence.Add(new PerfEvidence($"Thrown by ({types[0].Type})",
                        string.Join("; ", types[0].Throwers.Take(3).Select(t => $"{t.SubKey}: {Count(t.Count)}"))));
                    refs.AddRange(types[0].Throwers.Take(3).Select(t => new PerfCodeRef("method", ThrowerOf(t.SubKey))).DistinctBy(r => r.Value));
                }

                findings.Add(new PerfFinding(
                    $"code-exceptions:{component.Key}", PerfCategory.Code, PerfSeverity.Info,
                    $"{component.Key} threw {Num(total / (double)minutes, "0")} exceptions a minute",
                    "An exception is expensive: the runtime captures a stack trace for each one. Many per minute cost CPU even when they are handled.",
                    evidence,
                    "Exceptions used for control flow, or a failure that repeats.",
                    types[0].Throwers.Count > 0
                        ? "Look at the class that throws the most frequent type, and check for the condition beforehand instead."
                        : "Find where the most frequent type is thrown and check for it beforehand instead. Diagnose names the class that throws it.",
                    refs,
                    null));
            }
        }
    }

    /// <summary>
    /// Diagnose: one unit of work (an interaction, request, run or message) that sends the same statement more often than the
    /// threshold, the classic query per item of a list. Needs a few occurrences, or one that is far over the threshold.
    /// </summary>
    private static void NPlusOne(PerformanceDataset data, List<PerfFinding> findings)
    {
        var threshold = data.Settings.Threshold(PerfThresholds.NPlusOne);
        foreach (var group in data.Of(PerfKind.NPlusOne).Where(s => !IsMonitorSource(s.Key)).GroupBy(s => (s.Key, s.SubKey)))
        {
            var units = group.Sum(s => s.Count);
            var repeats = group.Sum(s => s.Sum);
            var most = group.Max(s => s.Max);
            var dbMs = group.Sum(s => s.DbMs);
            if (units < 3 && most < 3 * threshold)
            {
                continue;
            }

            var mean = units == 0 ? 0 : repeats / units;
            var (source, hash) = group.Key;
            var fingerprint = data.Fingerprint(hash);
            var refs = new List<PerfCodeRef> { SourceRef(source), new("statement", hash, fingerprint?.Sql) };
            refs.AddRange((fingerprint?.Callers ?? []).Select(c => new PerfCodeRef("method", c)));
            findings.Add(new PerfFinding(
                $"code-n-plus-one:{source}:{hash}", PerfCategory.Code, mean >= 10 * threshold ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"{Describe(source)} ran one statement {Num(mean, "0")} times per {UnitOf(source)} (N+1), {Count(units)} times in the period",
                "The same statement was sent again and again within one unit of work: a query per item of a list instead of one query for all of them. Each repeat is a round trip to the database.",
                [
                    new("Occurrences", Count(units)), new("Repeats per occurrence", Num(mean, "0.#")), new("Most repeats", Num(most, "0")),
                    new("Database time of the repeats", Ms(dbMs)),
                    new("Caller", fingerprint is { Callers.Count: > 0 } ? string.Join(", ", fingerprint.Callers) : "unknown")
                ],
                "A loop that queries per item, or a lazy load per row.",
                "Load the items in one query: a join or Include, a WHERE ... IN with the ids, or one query before the loop. Then check here that the finding is gone.",
                refs,
                mean > 1 ? dbMs * (1 - 1 / mean) : null));
        }
    }

    /// <summary>Sessions the workers saw idle inside a transaction at the last snapshot: they hold locks and connections.</summary>
    private static void IdleInTransaction(PerformanceDataset data, List<PerfFinding> findings)
    {
        if (data.Database?.Sessions is not { IdleInTransaction: > 0, LongestIdleInTransactionSeconds: >= 60 } sessions)
        {
            return;
        }

        findings.Add(new PerfFinding(
            "code-idle-in-transaction", PerfCategory.Code, sessions.LongestIdleInTransactionSeconds >= 600 ? PerfSeverity.Critical : PerfSeverity.Warning,
            $"{sessions.IdleInTransaction} database sessions sat idle in a transaction, the longest {Ms(sessions.LongestIdleInTransactionSeconds * 1000)}",
            "A transaction that is open while the code does something else holds its locks and its connection, and stops vacuum from removing dead rows.",
            [new("Idle in transaction", sessions.IdleInTransaction.ToString(CultureInfo.InvariantCulture)),
                new("Longest", Ms(sessions.LongestIdleInTransactionSeconds * 1000)), new("Seen at", "the last database snapshot")],
            "Code that opens a transaction and then waits: for an external call, a user, or a long computation.",
            "Keep transactions around database work only; make external calls before or after them.",
            [new("component", "database")],
            null));
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

    private static readonly string CrowdSecHost = new Uri(IpFeeds.CrowdSecContentUrl).Host;

    /// <summary>What to check for a host that answered 429: the budget of the connector that calls it, never another one's.</summary>
    private static string RateLimitAction(string host) =>
        host.EndsWith("action1.com", StringComparison.OrdinalIgnoreCase)
            ? "Check the request budget of the connector (Action1: 20 a minute for the instance) and what calls it most."
            : string.Equals(host, CrowdSecHost, StringComparison.OrdinalIgnoreCase)
                ? "Fleeto fetches the CrowdSec blocklist every 6 hours, and an hour after a failure; the list before stays in use. If it " +
                  "keeps happening, check in the CrowdSec Console whether something else pulls the same integration."
                : $"Check in Settings, Performance what calls {host} and how often; a refused call is tried again at its next attempt.";

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
            var perScan = (table.SequentialRowsRead ?? 0) / (double)table.SequentialScans!.Value;
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

    /// <summary>
    /// Diagnose: the statements that cost the components the most time, as they measured them, with the methods that sent them.
    /// Unlike the statement statistics of PostgreSQL these name the page, loop or message and the calling method.
    /// </summary>
    private static void AppStatements(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        var statements = data.Of(PerfKind.Statement).Where(s => !IsMonitorSource(s.SubKey)).GroupBy(s => s.Key)
            .Select(g => (Hash: g.Key, Series: g.ToList(), Merged: Merge(g))).ToList();
        var total = statements.Sum(s => s.Merged.Sum);
        if (total <= 0)
        {
            return;
        }

        var shareLimit = settings.Threshold(PerfThresholds.StatementShare) / 100;
        var slow = settings.Threshold(PerfThresholds.SlowStatement);
        // A statement run once or twice (a cold first query, a short Diagnose) is not a pattern: it needs the minimum samples.
        var minSamples = settings.Threshold(PerfThresholds.MinSamples);
        foreach (var (hash, series, merged) in statements.OrderByDescending(s => s.Merged.Sum).Take(5)
                     .Where(s => s.Merged.Sum / total >= shareLimit && s.Merged.Count >= minSamples))
        {
            var share = merged.Sum / total;
            var fingerprint = data.Fingerprint(hash);
            var sources = series.GroupBy(s => s.SubKey).Select(g => (Source: g.Key, Ms: g.Sum(s => s.Sum), Calls: g.Sum(s => s.Count)))
                .OrderByDescending(s => s.Ms).Take(3).ToList();
            var refs = new List<PerfCodeRef> { new("statement", hash, fingerprint?.Sql) };
            refs.AddRange((fingerprint?.Callers ?? []).Select(c => new PerfCodeRef("method", c)));
            refs.AddRange(sources.Select(s => SourceRef(s.Source)).Where(r => r.Kind != "component"));
            findings.Add(new PerfFinding(
                $"database-app-statement:{hash}", PerfCategory.Database, merged.Mean > slow || share >= 0.3 ? PerfSeverity.Warning : PerfSeverity.Info,
                $"One statement took {Pct(share * 100)} of the measured database time ({Ms(merged.Sum)} over {Count(merged.Count)} calls)",
                "Measured by the components in Diagnose: this statement cost more time than any other, and the evidence names who sent it.",
                [
                    new("Calls", Count(merged.Count)), new("Total time", Ms(merged.Sum)), new("Mean per call", Ms(merged.Mean)), new("95th percentile", Ms(merged.P95)),
                    new("Share of measured database time", Pct(share * 100)), new("Failed", Count(merged.Errors)),
                    new("Sent by", string.Join("; ", sources.Select(s => $"{s.Source}: {Count(s.Calls)} calls, {Ms(s.Ms)}"))),
                    new("Caller", fingerprint is { Callers.Count: > 0 } ? string.Join(", ", fingerprint.Callers) : "unknown")
                ],
                merged.Mean > slow ? "Each call is slow: it reads many rows or misses an index." : "Each call is fast, but it runs very often.",
                merged.Mean > slow
                    ? "Use Plan on the Database tab to see how it runs; add the index it needs, or select fewer rows."
                    : "Call it less often from the method named: cache the result, batch the calls, or load it once per page.",
                refs,
                merged.Sum));
        }
    }

    /// <summary>Sessions waiting for a lock at the last snapshot, and indexes that duplicate another one.</summary>
    private static void LocksAndIndexes(PerformanceDataset data, List<PerfFinding> findings)
    {
        if (data.Database?.Sessions is { LockWaiting: > 0, LongestLockWaitSeconds: >= 5 } sessions)
        {
            findings.Add(new PerfFinding(
                "database-lock-waits", PerfCategory.Database, sessions.LongestLockWaitSeconds >= 60 ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"{sessions.LockWaiting} database sessions waited for a lock, the longest {Ms(sessions.LongestLockWaitSeconds * 1000)}",
                "A session that waits for a lock does nothing until the session holding it commits; the page or loop behind it waits as long.",
                [new("Waiting", sessions.LockWaiting.ToString(CultureInfo.InvariantCulture)), new("Longest wait", Ms(sessions.LongestLockWaitSeconds * 1000)),
                    new("Longest transaction", Ms(sessions.LongestTransactionSeconds * 1000)), new("Seen at", "the last database snapshot")],
                "A long transaction that updates rows others need, or rows locked in a different order by two pieces of code.",
                "Keep transactions that update hot rows short, and update rows in the same order everywhere. The loops that overlap with pages are a good first suspect.",
                [new("component", "database")],
                null));
        }

        foreach (var index in data.Indexes.Where(i => i is { DuplicateOf: not null, Primary: false }))
        {
            findings.Add(new PerfFinding(
                $"database-duplicate-index:{index.Name}", PerfCategory.Database, PerfSeverity.Info,
                $"Index {index.Name} on {index.Table} duplicates {index.DuplicateOf}",
                "Two indexes on the same columns in the same order cost every write twice and take space, while queries use only one of them.",
                [new("Table", index.Table), new("Columns", index.Columns), new("Size", Bytes(index.Bytes)), new("Scans", Count(index.Scans))],
                "An index added in a migration next to one that already existed, often one created for a foreign key.",
                "Drop one of the two in a migration (keep a unique one), after checking that no query needs it for its sort order.",
                [new("index", index.Name), new("table", index.Table)],
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
    /// Does a loop slow pages down? Compares page interactions in minutes the loop ran with minutes it did not, for every loop that
    /// ran in a minute with interactions. <see cref="PerfOverlap.Judged"/> says whether there were enough minutes on both sides and
    /// enough interactions to judge, so a quiet period does not count as evidence. The Background tab shows the same comparison.
    /// </summary>
    public static IReadOnlyList<PerfOverlap> Overlaps(PerformanceDataset data)
    {
        var minSamples = data.Settings.Threshold(PerfThresholds.MinSamples);
        var busy = data.Minutes.Where(m => m.Interactions > 0).ToList();
        var overlaps = new List<PerfOverlap>();
        foreach (var loop in busy.SelectMany(m => m.Loops).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var with = busy.Where(m => m.Loops.Contains(loop)).ToList();
            var without = busy.Where(m => !m.Loops.Contains(loop)).ToList();
            var withHistogram = with.Aggregate((int[]?)null, (total, m) => PerfHistogram.Add(total, m.InteractionHistogram));
            var withoutHistogram = without.Aggregate((int[]?)null, (total, m) => PerfHistogram.Add(total, m.InteractionHistogram));
            var withCount = PerfHistogram.Total(withHistogram);
            var withoutCount = PerfHistogram.Total(withoutHistogram);
            overlaps.Add(new PerfOverlap(loop, with.Count, without.Count, withCount, withoutCount, PerfHistogram.Percentile(withHistogram, 0.95),
                PerfHistogram.Percentile(withoutHistogram, 0.95), PerfHistogram.Mean(withHistogram), PerfHistogram.Mean(withoutHistogram),
                with.Count >= MinOverlapMinutes && without.Count >= MinOverlapMinutes && withCount >= minSamples && withoutCount >= minSamples));
        }

        return overlaps;
    }

    private static void Overlap(PerformanceDataset data, List<PerfFinding> findings)
    {
        var gap = data.Settings.Threshold(PerfThresholds.LoopOverlap);
        foreach (var overlap in Overlaps(data).Where(o => o.Judged))
        {
            var p95With = overlap.P95With ?? 0;
            var p95Without = overlap.P95Without ?? 0;
            if (p95With - p95Without < gap || p95With < 1.5 * p95Without)
            {
                continue;
            }

            var meanGap = (overlap.MeanWith ?? 0) - (overlap.MeanWithout ?? 0);
            findings.Add(new PerfFinding(
                $"background-loop-overlap:{overlap.Loop}", PerfCategory.Background, PerfSeverity.Warning,
                $"Pages are slower while loop {overlap.Loop} runs: 95th percentile {Ms(p95With)} against {Ms(p95Without)}",
                "In the minutes this loop ran, page interactions were clearly slower than in the minutes it did not, so it competes with users for the database or CPU.",
                [new("Minutes with the loop", overlap.MinutesWith.ToString(CultureInfo.InvariantCulture)),
                    new("Minutes without", overlap.MinutesWithout.ToString(CultureInfo.InvariantCulture)),
                    new("95th percentile with", Ms(p95With)), new("95th percentile without", Ms(p95Without)),
                    new("Interactions with the loop", Count(overlap.InteractionsWith))],
                "The loop does heavy database work or holds locks the pages need.",
                "Make the loop work in smaller batches with short transactions, or move its heavy part to a quieter time.",
                [new("loop", overlap.Loop)],
                meanGap > 0 ? meanGap * overlap.InteractionsWith : null));
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
    // Frontend and network (0.7.0 step 3): measured by the browsers of signed-in users, for a share of page loads.
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Fewest measurements of a page before a Web Vital of it is judged: browsers report for a share of page loads only.</summary>
    private const int MinVitalSamples = 10;

    /// <summary>A regression must also be this large in absolute terms (a layout shift of 0.02 counts as 20).</summary>
    private const double MinRegressionGap = 20;

    /// <summary>The Web Vitals of one page over the period, from the browsers that reported.</summary>
    public sealed record PageVitals(string Route, long Views, PerfSeries? Lcp, PerfSeries? Fcp, PerfSeries? Ttfb, PerfSeries? Inp, PerfSeries? Cls,
        PerfSeries? LongTasks)
    {
        /// <summary>The 75th percentile of a vital: the figure Google judges Core Web Vitals on.</summary>
        public static double? P75(PerfSeries? vital) => vital is null ? null : PerfHistogram.Percentile(vital.Histogram, 0.75, vital.Max);

        /// <summary>The layout shift score, which is stored times <see cref="PerfVitals.ClsScale"/>.</summary>
        public double? ClsP75 => P75(Cls) is { } scaled ? scaled / PerfVitals.ClsScale : null;

        /// <summary>Time per page view the browser was blocked by tasks over 50 ms.</summary>
        public double? LongTaskMsPerView => Views <= 0 || LongTasks is null ? null : LongTasks.Sum / Views;
    }

    /// <summary>The pages with Web Vitals in the period, the most viewed first; the monitor's own page left out.</summary>
    public static IReadOnlyList<PageVitals> Vitals(PerformanceDataset data)
    {
        static PerfSeries? Of(IGrouping<string, PerfSeries> page, string metric) =>
            page.Where(s => s.SubKey == metric).ToList() is { Count: > 0 } list ? Merge(list) with { SubKey = metric } : null;

        return data.Of(PerfKind.Vital)
            .Where(s => !string.Equals(s.Key, DashboardRoute, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .Select(page => new PageVitals(page.Key, page.Where(s => s.SubKey == PerfVitals.View).Sum(s => s.Count), Of(page, PerfVitals.Lcp),
                Of(page, PerfVitals.Fcp), Of(page, PerfVitals.Ttfb), Of(page, PerfVitals.Inp), Of(page, PerfVitals.Cls), Of(page, PerfVitals.LongTask)))
            .OrderByDescending(p => p.Views)
            .ThenBy(p => p.Route, StringComparer.Ordinal)
            .ToList();
    }

    private static void Frontend(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        var minSamples = Math.Min(settings.Threshold(PerfThresholds.MinSamples), MinVitalSamples);
        var lcpLimit = settings.Threshold(PerfThresholds.Lcp);
        var inpLimit = settings.Threshold(PerfThresholds.Inp);
        var clsLimit = settings.Threshold(PerfThresholds.Cls);
        var longTaskLimit = settings.Threshold(PerfThresholds.LongTasks);

        foreach (var page in Vitals(data))
        {
            if (page.Lcp is { } lcp && lcp.Count >= minSamples && PageVitals.P75(lcp) is { } lcpP75 && lcpP75 > lcpLimit)
            {
                findings.Add(new PerfFinding(
                    $"frontend-lcp:{page.Route}", PerfCategory.Frontend, lcpP75 > 1.6 * lcpLimit ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"Page {page.Route} shows its main content after {Ms(lcpP75)} (75th percentile of {Count(lcp.Count)} loads)",
                    "Largest contentful paint is the time from opening the page until its largest element is shown. One in four users waited longer than this.",
                    VitalEvidence(page),
                    PageVitals.P75(page.Ttfb) is { } ttfbP75 && ttfbP75 > lcpP75 / 2
                        ? $"The server answered the page late (time to first byte {Ms(ttfbP75)}): the first render of the page waits for its data."
                        : "The browser itself: large scripts or styles to load before the page can show, or content that appears only after a second render.",
                    "Load the data of the page after its first render with a placeholder, or make its first query cheaper; keep heavy components out of the first view.",
                    [new("page", page.Route)],
                    PerfHistogram.TimeAbove(lcp.Histogram, lcpLimit, lcp.Max)));
            }

            if (page.Inp is { } inp && inp.Count >= minSamples && PageVitals.P75(inp) is { } inpP75 && inpP75 > inpLimit)
            {
                var server = data.Of(PerfKind.Interaction).Where(s => string.Equals(s.Key, page.Route, StringComparison.OrdinalIgnoreCase) && s.SubKey == "event")
                    .Select(s => s.P95).FirstOrDefault();
                findings.Add(new PerfFinding(
                    $"frontend-inp:{page.Route}", PerfCategory.Frontend, inpP75 > 2.5 * inpLimit ? PerfSeverity.Critical : PerfSeverity.Warning,
                    $"Page {page.Route} responds to clicks after {Ms(inpP75)} (75th percentile of {Count(inp.Count)} views)",
                    "Interaction to next paint is the time from a click or key until the browser shows its result. On a Blazor page it holds the round trip to the server and the render that comes back.",
                    VitalEvidence(page),
                    server is { } serverP95 && serverP95 > inpLimit
                        ? $"The server handles events of this page slowly ({Ms(serverP95)} at the 95th percentile): see its finding under Backend code."
                        : "The browser: a large render of the answer (a long table), or scripts that block the main thread.",
                    "Render less per event (page or virtualize long lists), and make the work behind the event cheaper.",
                    [new("page", page.Route)],
                    PerfHistogram.TimeAbove(inp.Histogram, inpLimit, inp.Max)));
            }

            if (page.Cls is { } cls && cls.Count >= minSamples && page.ClsP75 is { } clsP75 && clsP75 > clsLimit)
            {
                findings.Add(new PerfFinding(
                    $"frontend-cls:{page.Route}", PerfCategory.Frontend, clsP75 > 2.5 * clsLimit ? PerfSeverity.Warning : PerfSeverity.Info,
                    $"The content of page {page.Route} moves while it is used: layout shift {Num(clsP75, "0.###")} (75th percentile)",
                    "Cumulative layout shift adds up how far content jumps without the user asking, for example when a list appears above what the user was about to click.",
                    VitalEvidence(page),
                    "Content that appears after the first render without reserved space: a loading state replaced by a taller table, a banner or a chart.",
                    "Give loading states the height of what replaces them, and add banners below the content instead of above it.",
                    [new("page", page.Route)],
                    null));
            }

            if (page.Views >= minSamples && page.LongTaskMsPerView is { } blocked && blocked > longTaskLimit)
            {
                var longTasks = page.LongTasks!;
                findings.Add(new PerfFinding(
                    $"frontend-long-tasks:{page.Route}", PerfCategory.Frontend, blocked > 4 * longTaskLimit ? PerfSeverity.Warning : PerfSeverity.Info,
                    $"Page {page.Route} keeps the browser busy {Ms(blocked)} per view in long tasks",
                    "A task over 50 ms blocks the browser: clicks and keys wait until it ends.",
                    VitalEvidence(page),
                    "A large render of the page in the browser (many rows or charts at once), or a script doing a lot of work.",
                    "Render long lists in pages or virtualized, and draw charts only when they are shown.",
                    [new("page", page.Route)],
                    longTasks.Sum - 50d * longTasks.Count is > 0 and var extra ? extra : null));
            }
        }
    }

    private static List<PerfEvidence> VitalEvidence(PageVitals page) =>
    [
        new("Views measured", Count(page.Views)),
        new("Largest contentful paint (p75)", Ms(PageVitals.P75(page.Lcp))),
        new("First contentful paint (p75)", Ms(PageVitals.P75(page.Fcp))),
        new("Time to first byte (p75)", Ms(PageVitals.P75(page.Ttfb))),
        new("Interaction to next paint (p75)", Ms(PageVitals.P75(page.Inp))),
        new("Layout shift (p75)", page.ClsP75 is { } cls ? Num(cls, "0.###") : "–"),
        new("Long tasks per view", Ms(page.LongTaskMsPerView))
    ];

    /// <summary>The network measurements of the period by key (<c>circuit</c>, <c>connect</c>), added up over the components.</summary>
    public static IReadOnlyDictionary<string, PerfSeries> NetworkSeries(PerformanceDataset data) =>
        data.Of(PerfKind.Network).GroupBy(s => s.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => Merge(g), StringComparer.Ordinal);

    private static void Network(PerformanceDataset data, List<PerfFinding> findings)
    {
        var settings = data.Settings;
        var minSamples = Math.Min(settings.Threshold(PerfThresholds.MinSamples), MinVitalSamples);
        var network = NetworkSeries(data);

        var roundTripLimit = settings.Threshold(PerfThresholds.CircuitRoundTrip);
        if (network.GetValueOrDefault(PerfVitals.CircuitRoundTrip) is { } circuit && circuit.Count >= minSamples && circuit.P95 is { } p95 && p95 > roundTripLimit)
        {
            var events = data.Of(PerfKind.Interaction).Where(s => s.SubKey == "event").Sum(s => s.Count);
            findings.Add(new PerfFinding(
                "network-circuit-round-trip", PerfCategory.Network, p95 > 3 * roundTripLimit ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"A message on the live connection takes {Ms(p95)} to the server and back (95th percentile)",
                "Every click on a page travels to the server and its answer back, so this time comes on top of every interaction. The browser measures it every 5 seconds with a message the server answers at once.",
                [new("Measurements", Count(circuit.Count)), new("Median", Ms(circuit.P50)), new("95th percentile", Ms(p95)), new("Longest", Ms(circuit.Max)),
                    new("Page events in the period", Count(events))],
                circuit.P50 is { } p50 && p50 > roundTripLimit
                    ? "Most users are far from the VPS or on a slow link: the network, not Fleeto."
                    : "Some users are on slow or busy links (VPN, mobile), or the VPS answered late now and then: compare with its CPU.",
                "When the median is high, host the instance closer to its users. When only the tail is high, look at the VPS CPU and the thread pool queue at those times.",
                [new("component", "network")],
                null));
        }

        var connectLimit = settings.Threshold(PerfThresholds.Connect);
        if (network.GetValueOrDefault(PerfVitals.Connect) is { } connect && connect.Count >= minSamples && connect.P95 is { } connectP95 && connectP95 > connectLimit)
        {
            findings.Add(new PerfFinding(
                "network-connect", PerfCategory.Network, PerfSeverity.Warning,
                $"Opening a connection to Fleeto takes {Ms(connectP95)} (95th percentile of {Count(connect.Count)} page loads)",
                "DNS, TCP and TLS before the first byte of a page. It is paid once per page load from the address bar, not per click.",
                [new("Page loads", Count(connect.Count)), new("Median", Ms(connect.P50)), new("95th percentile", Ms(connectP95))],
                "A slow DNS server, a long distance to the VPS, or TLS without session resumption.",
                "Run the edge check on the Server tab: it measures DNS, TCP and TLS from the VPS itself and shows the HTTP version and keep-alive.",
                [new("component", "network")],
                null));
        }
    }

    /// <summary>Every budget of the settings against the period.</summary>
    public static IReadOnlyList<PerfBudgetResult> BudgetResults(PerformanceDataset data) =>
        PerfBudgets.Evaluate(data.Settings.Budgets ?? [], data.Series);

    private static void Budgets(PerformanceDataset data, List<PerfFinding> findings)
    {
        foreach (var result in BudgetResults(data).Where(r => !r.Met))
        {
            var budget = result.Budget;
            var what = $"{PerfBudgets.WordOf(budget.Kind)} {budget.Key}{(budget.SubKey.Length > 0 ? " " + budget.SubKey : "")}";
            findings.Add(new PerfFinding(
                $"budget:{PerfBudgets.WordOf(budget.Kind)}:{budget.Key}:{budget.SubKey}", CategoryOf(budget.Kind),
                result.P95 > 2 * budget.P95Ms ? PerfSeverity.Critical : PerfSeverity.Warning,
                $"Over budget: {what} ran {Ms(result.P95)} at the 95th percentile against a budget of {Ms(budget.P95Ms)}",
                "An admin set this budget in the Settings tab: the 95th percentile this flow should stay under.",
                [new("Budget", Ms(budget.P95Ms)), new("95th percentile", Ms(result.P95)), new("Occurrences", Count(result.Count))],
                "See the other findings of this flow for where its time goes.",
                "Make the flow faster, or raise the budget when the target was too strict.",
                [new(CodeRefKind(budget.Kind), budget.Key), new("budget", budget.ToLine())],
                result.TimeAboveMs));
        }
    }

    private static void Regressions(PerformanceDataset data, List<PerfFinding> findings)
    {
        var limit = data.Settings.Threshold(PerfThresholds.Regression) / 100;
        foreach (var comparison in data.Comparisons)
        {
            var version = comparison.Kind == PerfComparisonKind.Version;
            foreach (var row in comparison.Rows.Where(r => r.Change >= limit && r.NowP95 - r.BeforeP95 >= MinRegressionGap))
            {
                if (row.Kind is PerfKind.Interaction or PerfKind.Vital && string.Equals(row.Key, DashboardRoute, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var against = version ? $"version {comparison.Before}" : "the previous period";
                findings.Add(new PerfFinding(
                    $"regression:{comparison.Kind}:{row.Kind}:{row.Key}:{row.SubKey}", CategoryOf(row.Kind),
                    row.Change >= 2 * limit ? PerfSeverity.Warning : PerfSeverity.Info,
                    $"{Describe(row)} is {Pct(row.Change * 100)} slower than in {against}: {Value(row, row.NowP95)} against {Value(row, row.BeforeP95)} at the 95th percentile",
                    version
                        ? $"Since version {comparison.Now} started, this flow got slower than it was on version {comparison.Before}."
                        : "This flow got slower than in the period of equal length just before this one.",
                    [new(version ? $"Version {comparison.Before}" : "Previous period", $"{Value(row, row.BeforeP95)} over {Count(row.BeforeCount)}"),
                        new(version ? $"Version {comparison.Now}" : "This period", $"{Value(row, row.NowP95)} over {Count(row.NowCount)}")],
                    version ? "A change in the new version, or more data than before." : "More load than before, more data, or a change made in between (see the markers).",
                    version
                        ? "Look at what the new version changed in this flow; the other findings of it say where its time goes now."
                        : "Compare with the markers and the timeline; when the load grew, the other findings of this flow say where its time goes.",
                    [new(CodeRefKind(row.Kind), row.Key)],
                    null));
            }
        }
    }

    /// <summary>A flow of a comparison in words: "Page /clients loading", "GET 2xx /api/v1/endpoints".</summary>
    public static string Describe(PerfComparisonRow row) => row.Kind switch
    {
        PerfKind.Interaction => $"Page {row.Key} {(row.SubKey == "load" ? "loading" : "events")}",
        PerfKind.Http => $"{row.SubKey} {row.Key}",
        PerfKind.WorkerRun => $"Loop {row.Key}",
        PerfKind.AgentMessage => $"Agent message {row.Key}",
        PerfKind.Vital => $"{VitalName(row.SubKey)} of page {row.Key}",
        _ => row.Key
    };

    /// <summary>A value of a comparison row: milliseconds, or the score of a layout shift.</summary>
    public static string Value(PerfComparisonRow row, double? value) =>
        row.Kind == PerfKind.Vital && row.SubKey == PerfVitals.Cls ? value is { } v ? Num(v / PerfVitals.ClsScale, "0.###") : "–" : Ms(value);

    public static string VitalName(string metric) => metric switch
    {
        PerfVitals.Lcp => "Largest contentful paint",
        PerfVitals.Fcp => "First contentful paint",
        PerfVitals.Ttfb => "Time to first byte",
        PerfVitals.Inp => "Interaction to next paint",
        PerfVitals.Cls => "Layout shift",
        PerfVitals.LongTask => "Long tasks",
        _ => metric
    };

    private static PerfCategory CategoryOf(PerfKind kind) => kind switch
    {
        PerfKind.WorkerRun => PerfCategory.Background,
        PerfKind.AgentMessage => PerfCategory.Agents,
        PerfKind.Vital => PerfCategory.Frontend,
        _ => PerfCategory.Code
    };

    private static string CodeRefKind(PerfKind kind) => kind switch
    {
        PerfKind.Http => "api",
        PerfKind.WorkerRun => "loop",
        PerfKind.AgentMessage => "message",
        _ => "page"
    };

    // ---------------------------------------------------------------------------------------------------------------
    // Formatting: culture-invariant, "1.5 s", never "1,5 s".
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>The time users and integrations waited for pages and requests in the period.</summary>
    private static double UserFacingTime(PerformanceDataset data) =>
        data.Series.Where(s => s.Kind is PerfKind.Interaction or PerfKind.Http && s.Key != DashboardRoute).Sum(s => s.Sum);

    /// <summary>The CPU the monitor may use in a component: the threshold for Basic minutes, three times it for Diagnose minutes.</summary>
    private static double OverheadAllowance(PerfRuntimeSummary runtime, double threshold) =>
        runtime.Minutes <= 0 ? threshold : threshold * (runtime.Minutes + 2d * Math.Min(runtime.DiagnoseMinutes, runtime.Minutes)) / runtime.Minutes;

    private static bool IsMonitorSource(string source) => source is PerfKeys.MonitorSource or PerfKeys.DashboardSource;

    /// <summary>The class in an exception sub key of Diagnose (<c>Fleeto.Web.Services.ClientService in interaction</c>).</summary>
    private static string ThrowerOf(string subKey) => subKey.Split(" in ", 2)[0];

    /// <summary>The source the database work of a flow counts under; the monitor's own page counts under its own.</summary>
    public static string SourceOf(PerfSeries flow) => flow.Kind switch
    {
        PerfKind.Interaction when string.Equals(flow.Key, DashboardRoute, StringComparison.OrdinalIgnoreCase) => PerfKeys.DashboardSource,
        PerfKind.Interaction => PerfKeys.InteractionSource(flow.Key),
        PerfKind.Http => PerfKeys.ApiSource(flow.Key),
        PerfKind.WorkerRun => PerfKeys.WorkerSource(flow.Key),
        PerfKind.AgentMessage => PerfKeys.GatewaySource(flow.Key),
        PerfKind.Signing => PerfKeys.SignerSource(flow.Key),
        _ => flow.Key
    };

    /// <summary>Where to look for a source: the page, route, loop, message type or signing request kind.</summary>
    public static PerfCodeRef SourceRef(string source)
    {
        var colon = source.IndexOf(':');
        if (colon <= 0)
        {
            return new PerfCodeRef("component", source);
        }

        var value = source[(colon + 1)..];
        return source[..colon] switch
        {
            "interaction" => new PerfCodeRef("page", value),
            "api" => new PerfCodeRef("api", value),
            "worker" => new PerfCodeRef("loop", value),
            "gateway" => new PerfCodeRef("message", value),
            "signer" => new PerfCodeRef("signing", value),
            _ => new PerfCodeRef("component", source)
        };
    }

    /// <summary>A source in words: "Page /clients", "Loop patch-sync".</summary>
    public static string Describe(string source)
    {
        var reference = SourceRef(source);
        return reference.Kind switch
        {
            "page" => $"Page {reference.Value}",
            "api" => $"Route {reference.Value}",
            "loop" => $"Loop {reference.Value}",
            "message" => $"Agent message {reference.Value}",
            "signing" => $"Signing {reference.Value}",
            _ => $"Work outside a measured scope ({source})"
        };
    }

    private static string UnitOf(string source) => SourceRef(source).Kind switch
    {
        "page" => "interaction",
        "api" => "request",
        "loop" => "run",
        "message" => "message",
        "signing" => "request",
        _ => "unit of work"
    };

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
