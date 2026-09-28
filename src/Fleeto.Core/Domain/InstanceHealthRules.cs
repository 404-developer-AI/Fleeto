using System.Globalization;
using System.Text.Json;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>Host values of the VPS; null where the platform does not offer them (local development on Windows).</summary>
public sealed record HostHealth(
    double? CpuPercent,
    double? Load1,
    int? Cores,
    long? MemoryTotalBytes,
    long? MemoryAvailableBytes,
    long? SwapTotalBytes,
    long? SwapFreeBytes,
    long? DiskTotalBytes,
    long? DiskFreeBytes)
{
    public static readonly HostHealth Unknown = new(null, null, null, null, null, null, null, null, null);
}

/// <summary>A table of the instance database with its size on disk (indexes and TOAST included) and estimated rows.</summary>
public sealed record TableSize(string Name, long Bytes, long Rows);

/// <summary>What the workers know about the instance itself at the time of a sample.</summary>
/// <param name="StaleLoops">Worker tasks that stopped reporting (<c>WorkerHeartbeat</c>).</param>
public sealed record FleetoHealth(
    IReadOnlyList<string> StaleLoops,
    bool BackupConfigured,
    DateTime? LastBackupAt,
    bool LastBackupFailed,
    int OutboxEmails,
    DateTime? OldestOutboxEmail,
    int OutboxWebhooks,
    DateTime? OldestOutboxWebhook,
    int UnprocessedEvents,
    DateTime? OldestUnprocessedEvent,
    int EndpointsOnline,
    int Endpoints)
{
    public static readonly FleetoHealth Empty = new([], false, null, false, 0, null, 0, null, 0, null, 0, 0);
}

/// <summary>The part of a sample that is stored as JSON: the largest tables and the instance facts.</summary>
public sealed record InstanceHealthDetail(IReadOnlyList<TableSize> Tables, FleetoHealth Fleeto)
{
    public static readonly InstanceHealthDetail Empty = new([], FleetoHealth.Empty);
}

/// <summary>A statement from pg_stat_statements: normalized text (literals replaced), how often it ran and how long it took.</summary>
public sealed record SlowQuery(string Query, long Calls, double TotalMs, double MeanMs, long Rows);

/// <summary>The statements that cost the database the most, as the workers last read them; <see cref="Problem"/> says why there are none.</summary>
public sealed record QueryStatistics(DateTime ReadAt, string? Problem, IReadOnlyList<SlowQuery> Queries);

/// <summary>One sample as the rules judge it.</summary>
public sealed record HealthSnapshot(DateTime Time, HostHealth Host, long DatabaseBytes, int Connections, int MaxConnections,
    InstanceHealthDetail Detail);

/// <summary>Used disk space at one time, for the forecast.</summary>
public sealed record DiskPoint(DateTime Time, long UsedBytes);

/// <summary>A problem found in a sample: a stable key, and a title and detail that state the cause and the next step.</summary>
public sealed record HealthFinding(string Key, HealthComponent Component, AlertSeverity Severity, string Title, string Detail);

/// <summary>
/// Rules of instance health (0.6.0, ARCHITECTURE.md §4, Instance health): when the VPS or the instance needs attention, why, and
/// what to do. Fixed thresholds, judged by the workers after every sample. Host values are of the whole VPS: several instances
/// can share it, and every one of them reports the same disk, memory and CPU.
/// </summary>
public static class InstanceHealthRules
{
    public static readonly TimeSpan SampleInterval = TimeSpan.FromMinutes(5);

    /// <summary>Samples are kept this long.</summary>
    public static readonly TimeSpan SampleRetention = TimeSpan.FromDays(30);

    /// <summary>Resolved issues are kept this long.</summary>
    public static readonly TimeSpan ResolvedRetention = TimeSpan.FromDays(90);

    /// <summary>The window of the disk forecast and the trends.</summary>
    public static readonly TimeSpan TrendWindow = TimeSpan.FromDays(7);

    /// <summary>The CPU is judged on its average over this window, so a short peak is not a problem.</summary>
    public static readonly TimeSpan CpuWindow = TimeSpan.FromMinutes(15);

    /// <summary>Without a sample for this long the workers are not measuring: web shows that instead of old values.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(15);

    public const double DiskWarningUsed = 0.80;
    public const double DiskCriticalUsed = 0.90;
    public const double DiskWarningDays = 14;
    public const double DiskCriticalDays = 3;
    public const double MemoryWarningAvailable = 0.10;
    public const double MemoryCriticalAvailable = 0.05;
    public const double SwapWarningUsed = 0.50;
    public const double CpuWarning = 85;
    public const double CpuCritical = 95;
    public const double ConnectionsWarning = 0.80;
    public const double ConnectionsCritical = 0.95;
    public static readonly TimeSpan BackupWarningAge = TimeSpan.FromHours(36);
    public static readonly TimeSpan OutboxWarningAge = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan EventsWarningAge = TimeSpan.FromMinutes(10);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string SerializeDetail(InstanceHealthDetail detail) => JsonSerializer.Serialize(detail, Json);

    public static InstanceHealthDetail ParseDetail(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return InstanceHealthDetail.Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<InstanceHealthDetail>(json, Json) ?? InstanceHealthDetail.Empty;
        }
        catch (JsonException)
        {
            return InstanceHealthDetail.Empty;
        }
    }

    public static HealthSnapshot ToSnapshot(InstanceHealthSample sample) => new(sample.Time,
        new HostHealth(sample.CpuPercent, sample.Load1, sample.Cores, sample.MemoryTotalBytes, sample.MemoryAvailableBytes, sample.SwapTotalBytes,
            sample.SwapFreeBytes, sample.DiskTotalBytes, sample.DiskFreeBytes),
        sample.DatabaseBytes, sample.DatabaseConnections, sample.DatabaseMaxConnections, ParseDetail(sample.DetailJson));

    /// <summary>
    /// Days until the disk is full at the growth of the last week: a least-squares line through the used space. Null without at
    /// least a day of samples, or when the disk does not grow.
    /// </summary>
    public static double? DaysUntilFull(IReadOnlyList<DiskPoint> points, long totalBytes)
    {
        if (points.Count < 12 || totalBytes <= 0)
        {
            return null;
        }

        var first = points.Min(p => p.Time);
        var last = points.Max(p => p.Time);
        if (last - first < TimeSpan.FromDays(1))
        {
            return null;
        }

        var xs = points.Select(p => (p.Time - first).TotalDays).ToList();
        var ys = points.Select(p => (double)p.UsedBytes).ToList();
        var meanX = xs.Average();
        var meanY = ys.Average();
        var denominator = xs.Sum(x => (x - meanX) * (x - meanX));
        if (denominator <= 0)
        {
            return null;
        }

        var slope = xs.Zip(ys, (x, y) => (x - meanX) * (y - meanY)).Sum() / denominator; // bytes a day
        if (slope <= 0)
        {
            return null;
        }

        var used = points.OrderBy(p => p.Time).Last().UsedBytes;
        return Math.Max(0, (totalBytes - used) / slope);
    }

    /// <summary>Bytes the disk grows a day at the trend of <see cref="DaysUntilFull"/>; null when it does not grow.</summary>
    public static double? GrowthPerDay(IReadOnlyList<DiskPoint> points, long totalBytes)
    {
        if (DaysUntilFull(points, totalBytes) is not { } days || points.Count == 0)
        {
            return null;
        }

        var used = points.OrderBy(p => p.Time).Last().UsedBytes;
        return days <= 0 ? null : (totalBytes - used) / days;
    }

    /// <param name="now">The latest sample.</param>
    /// <param name="disk">Used disk space over <see cref="TrendWindow"/>, for the forecast.</param>
    /// <param name="recentCpu">CPU percentages of the samples within <see cref="CpuWindow"/>, the latest included.</param>
    public static IReadOnlyList<HealthFinding> Evaluate(HealthSnapshot now, IReadOnlyList<DiskPoint> disk, IReadOnlyList<double> recentCpu)
    {
        var findings = new List<HealthFinding>();
        var host = now.Host;
        var tables = LargestTables(now.Detail.Tables);

        if (host is { DiskTotalBytes: > 0 and var total, DiskFreeBytes: { } free })
        {
            var used = 1 - (double)free / total;
            var days = DaysUntilFull(disk, total);
            var severity = used >= DiskCriticalUsed || days <= DiskCriticalDays ? AlertSeverity.Critical
                : used >= DiskWarningUsed || days <= DiskWarningDays ? AlertSeverity.Warning
                : (AlertSeverity?)null;
            if (severity is { } s)
            {
                var forecast = days is { } d ? $" At the growth of the last week it is full in about {DaysText(d)}." : string.Empty;
                var growth = GrowthPerDay(disk, total) is { } perDay ? $" It grows {ByteSize.Format((long)perDay)} a day." : string.Empty;
                findings.Add(new HealthFinding("disk", HealthComponent.Disk, s,
                    $"The disk of the VPS is {Percent(used)} full ({ByteSize.Format(free)} free).{forecast}",
                    $"{growth.TrimStart()} Largest tables: {tables}. Lower the retention in Settings, Retention, remove what else fills the disk, or add disk space. Other instances on this VPS use the same disk.".TrimStart()));
            }
        }

        if (host is { MemoryTotalBytes: > 0 and var memoryTotal, MemoryAvailableBytes: { } available })
        {
            var availableShare = (double)available / memoryTotal;
            var swapUsed = host is { SwapTotalBytes: > 0 and var swapTotal, SwapFreeBytes: { } swapFree } ? 1 - (double)swapFree / swapTotal : 0;
            var severity = availableShare < MemoryCriticalAvailable ? AlertSeverity.Critical
                : availableShare < MemoryWarningAvailable || swapUsed >= SwapWarningUsed ? AlertSeverity.Warning
                : (AlertSeverity?)null;
            if (severity is { } s)
            {
                var swap = swapUsed > 0 ? $" Swap is {Percent(swapUsed)} in use, which slows everything down." : string.Empty;
                findings.Add(new HealthFinding("memory", HealthComponent.Memory, s,
                    $"The VPS has {Percent(availableShare)} of its memory available ({ByteSize.Format(available)} of {ByteSize.Format(memoryTotal)}).",
                    $"{swap.TrimStart()} Run 'docker stats' on the VPS to see which containers use it, and add memory when the instances need more. Several instances on one VPS share it.".TrimStart()));
            }
        }

        if (recentCpu.Count >= 3)
        {
            var average = recentCpu.Average();
            var severity = average >= CpuCritical ? AlertSeverity.Critical : average >= CpuWarning ? AlertSeverity.Warning : (AlertSeverity?)null;
            if (severity is { } s)
            {
                var load = host is { Load1: { } l, Cores: { } cores } ? $" Load average {l.ToString("0.0", CultureInfo.InvariantCulture)} on {cores} cores." : string.Empty;
                findings.Add(new HealthFinding("cpu", HealthComponent.Cpu, s,
                    $"The CPU of the VPS has been {average.ToString("0", CultureInfo.InvariantCulture)}% busy on average for {(int)CpuWindow.TotalMinutes} minutes.",
                    $"{load.TrimStart()} Run 'docker stats' on the VPS to see which container uses it. When it stays high, add CPU or move an instance to another VPS.".TrimStart()));
            }
        }

        if (now.MaxConnections > 0)
        {
            var share = (double)now.Connections / now.MaxConnections;
            var severity = share >= ConnectionsCritical ? AlertSeverity.Critical : share >= ConnectionsWarning ? AlertSeverity.Warning : (AlertSeverity?)null;
            if (severity is { } s)
            {
                findings.Add(new HealthFinding("database.connections", HealthComponent.Database, s,
                    $"PostgreSQL uses {now.Connections} of its {now.MaxConnections} connections.",
                    "When all are in use, web, gateway and workers cannot reach the database. Copy the diagnostics on the Instance health page to find the component that holds them, and restart the instance if the number keeps growing."));
            }
        }

        var fleeto = now.Detail.Fleeto;
        if (fleeto.StaleLoops.Count > 0)
        {
            findings.Add(new HealthFinding("workers", HealthComponent.Workers, AlertSeverity.Critical,
                $"Worker task {string.Join(", ", fleeto.StaleLoops.Order(StringComparer.Ordinal))} has not reported for more than 2 minutes.",
                "Checks, alerts, notifications or integrations may be late. The workers container restarts itself when a task stays stuck; copy the diagnostics on the Instance health page if it happens again."));
        }

        if (fleeto.OldestUnprocessedEvent is { } oldestEvent && now.Time - oldestEvent > EventsWarningAge)
        {
            findings.Add(new HealthFinding("workers.events", HealthComponent.Workers, AlertSeverity.Warning,
                $"{fleeto.UnprocessedEvents} endpoint events wait longer than {(int)EventsWarningAge.TotalMinutes} minutes to be processed.",
                "Offline alerts and endpoint status are late. Check that the workers container runs, and copy the diagnostics on the Instance health page if the number keeps growing."));
        }

        if (!fleeto.BackupConfigured)
        {
            findings.Add(new HealthFinding("backups", HealthComponent.Backups, AlertSeverity.Warning,
                "No backup destination is configured.",
                "Without backups a lost VPS loses every client, endpoint and setting of this instance. Configure a destination in Settings, Backups."));
        }
        else if (fleeto.LastBackupAt is not { } lastBackup || now.Time - lastBackup > BackupWarningAge)
        {
            findings.Add(new HealthFinding("backups", HealthComponent.Backups, AlertSeverity.Warning,
                fleeto.LastBackupAt is { } at
                    ? $"The last successful backup is {DaysText((now.Time - at).TotalDays)} old."
                    : "No backup has succeeded yet.",
                fleeto.LastBackupFailed
                    ? "The last backup failed. Open Settings, Backups for its error and test the destination."
                    : "Open Settings, Backups to see when the next one runs and test the destination."));
        }

        if (fleeto.OldestOutboxEmail is { } oldestEmail && now.Time - oldestEmail > OutboxWarningAge)
        {
            findings.Add(new HealthFinding("notifications.email", HealthComponent.Notifications, AlertSeverity.Warning,
                $"{fleeto.OutboxEmails} emails wait longer than {(int)OutboxWarningAge.TotalMinutes} minutes to be sent.",
                "Alert emails do not reach their recipients. Send a test email in Settings, Email to see the error of the mail server."));
        }

        if (fleeto.OldestOutboxWebhook is { } oldestWebhook && now.Time - oldestWebhook > OutboxWarningAge)
        {
            findings.Add(new HealthFinding("notifications.webhooks", HealthComponent.Notifications, AlertSeverity.Warning,
                $"{fleeto.OutboxWebhooks} webhook notifications wait longer than {(int)OutboxWarningAge.TotalMinutes} minutes to be delivered.",
                "Slack, Teams or webhook channels do not get their alerts. Send a test message from Settings, Notification channels to see the error."));
        }

        return findings;
    }

    /// <summary>The worst severity per component; components without a finding are healthy.</summary>
    public static IReadOnlyDictionary<HealthComponent, AlertSeverity> Worst(IEnumerable<(HealthComponent Component, AlertSeverity Severity)> issues) =>
        issues.GroupBy(i => i.Component).ToDictionary(g => g.Key, g => g.Max(i => i.Severity));

    private static string LargestTables(IReadOnlyList<TableSize> tables) =>
        tables.Count == 0 ? "not measured" : string.Join(", ", tables.OrderByDescending(t => t.Bytes).Take(3).Select(t => $"{t.Name} {ByteSize.Format(t.Bytes)}"));

    private static string Percent(double share) => (share * 100).ToString("0", CultureInfo.InvariantCulture) + "%";

    private static string DaysText(double days) => days switch
    {
        < 1 => "less than a day",
        < 1.5 => "1 day",
        _ => $"{Math.Round(days).ToString(CultureInfo.InvariantCulture)} days"
    };
}
