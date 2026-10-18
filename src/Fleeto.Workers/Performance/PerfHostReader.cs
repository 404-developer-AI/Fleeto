using System.Globalization;
using System.Text.RegularExpressions;
using Fleeto.Core.Entities;
using Fleeto.Workers.Health;

namespace Fleeto.Workers.Performance;

/// <summary>
/// Reads the VPS for the performance monitor every minute (0.7.0, PERFORMANCE.md §4.6). The same files as instance health, which a
/// container shares with the VPS, read in more detail: CPU split into busy, steal and iowait from /proc/stat, the latency and
/// utilization of whole disks from /proc/diskstats, and CPU throttling of the container from its cgroup. Rates are the difference
/// with the previous read, so the first read only sets the baseline. Elsewhere (local development on Windows) cores and disk only.
/// </summary>
public sealed partial class PerfHostReader
{
    private readonly Lock _lock = new();
    private Counters? _previous;

    public async Task<PerfHostMinute> ReadAsync(DateTime minute, CancellationToken cancellationToken)
    {
        var row = new PerfHostMinute { Minute = minute, Cores = Environment.ProcessorCount };
        try
        {
            var root = OperatingSystem.IsLinux() ? "/" : Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";
            var drive = new DriveInfo(root);
            row.DiskTotalBytes = drive.TotalSize;
            row.DiskFreeBytes = drive.AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Unknown disk: the page says so.
        }

        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/stat"))
        {
            return row;
        }

        var memory = HostMetricsReader.ParseMeminfo(await ReadAsync("/proc/meminfo", cancellationToken) ?? string.Empty);
        row.MemoryTotalBytes = memory?.Total;
        row.MemoryAvailableBytes = memory?.Available;
        row.SwapTotalBytes = memory?.SwapTotal;
        row.SwapFreeBytes = memory?.SwapFree;
        row.Load1 = HostMetricsReader.ParseLoad1(await ReadAsync("/proc/loadavg", cancellationToken) ?? string.Empty);

        var now = new Counters(
            Environment.TickCount64,
            ParseCpu(await ReadAsync("/proc/stat", cancellationToken) ?? string.Empty),
            ParseDisks(await ReadAsync("/proc/diskstats", cancellationToken) ?? string.Empty),
            ParseThrottling(await ReadAsync("/sys/fs/cgroup/cpu.stat", cancellationToken)));

        Counters? previous;
        lock (_lock)
        {
            previous = _previous;
            _previous = now;
        }

        if (previous is not null)
        {
            Apply(row, previous, now);
        }

        return row;
    }

    internal static void Apply(PerfHostMinute row, Counters before, Counters after)
    {
        if (before.Cpu is { } b && after.Cpu is { } a && a.Total > b.Total)
        {
            var total = (double)(a.Total - b.Total);
            row.CpuPercent = Percent(1 - (Delta(a.Idle, b.Idle) + Delta(a.Iowait, b.Iowait)) / total);
            row.StealPercent = Percent(Delta(a.Steal, b.Steal) / total);
            row.IowaitPercent = Percent(Delta(a.Iowait, b.Iowait) / total);
        }

        var elapsedMs = after.TickMs - before.TickMs;
        double ios = 0, ioMs = 0, utilization = 0;
        var anyDisk = false;
        foreach (var (name, disk) in after.Disks)
        {
            if (!before.Disks.TryGetValue(name, out var old))
            {
                continue;
            }

            anyDisk = true;
            ios += Delta(disk.Operations, old.Operations);
            ioMs += Delta(disk.OperationMs, old.OperationMs);
            if (elapsedMs > 0)
            {
                utilization = Math.Max(utilization, Delta(disk.BusyMs, old.BusyMs) / elapsedMs);
            }
        }

        if (anyDisk)
        {
            row.DiskLatencyMs = ios > 0 ? Math.Round(ioMs / ios, 2) : 0;
            row.DiskUtilizationPercent = Percent(utilization);
        }

        if (before.Throttling is { } tb && after.Throttling is { } ta && ta.Periods > tb.Periods)
        {
            row.ThrottledPercent = Percent(Delta(ta.Throttled, tb.Throttled) / (double)(ta.Periods - tb.Periods));
        }
    }

    private static double Percent(double share) => Math.Round(Math.Clamp(share, 0, 1) * 100, 1);

    private static double Delta(ulong after, ulong before) => after >= before ? after - before : 0;

    private static async Task<string?> ReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    internal sealed record Counters(long TickMs, CpuCounters? Cpu, IReadOnlyDictionary<string, DiskCounters> Disks, ThrottleCounters? Throttling);

    internal readonly record struct CpuCounters(ulong Total, ulong Idle, ulong Iowait, ulong Steal);

    internal readonly record struct DiskCounters(ulong Operations, ulong OperationMs, ulong BusyMs);

    internal readonly record struct ThrottleCounters(ulong Periods, ulong Throttled);

    /// <summary>The first line of /proc/stat: "cpu user nice system idle iowait irq softirq steal guest guest_nice".</summary>
    internal static CpuCounters? ParseCpu(string procStat)
    {
        var line = procStat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8).Select(Number).ToArray();
        if (values.Length < 4)
        {
            return null;
        }

        // Guest time is already counted in user and nice.
        return new CpuCounters(values.Aggregate(0UL, (sum, v) => sum + v), values[3], values.ElementAtOrDefault(4), values.ElementAtOrDefault(7));
    }

    /// <summary>
    /// Whole disks from /proc/diskstats (sda, vda, xvda, nvme0n1): partitions, loop and RAM devices are left out, so nothing is
    /// counted twice. Fields after the name: reads, merged, sectors, ms reading, writes, merged, sectors, ms writing, in progress,
    /// ms doing I/O.
    /// </summary>
    internal static IReadOnlyDictionary<string, DiskCounters> ParseDisks(string diskstats)
    {
        var disks = new Dictionary<string, DiskCounters>(StringComparer.Ordinal);
        foreach (var line in diskstats.Split('\n'))
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 13 || !WholeDiskPattern().IsMatch(fields[2]))
            {
                continue;
            }

            var reads = Number(fields[3]);
            var readMs = Number(fields[6]);
            var writes = Number(fields[7]);
            var writeMs = Number(fields[10]);
            disks[fields[2]] = new DiskCounters(reads + writes, readMs + writeMs, Number(fields[12]));
        }

        return disks;
    }

    /// <summary>nr_periods and nr_throttled from cpu.stat of cgroup v2; null without a CPU limit or without the file.</summary>
    internal static ThrottleCounters? ParseThrottling(string? cpuStat)
    {
        if (cpuStat is null)
        {
            return null;
        }

        ulong? periods = null, throttled = null;
        foreach (var line in cpuStat.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2)
            {
                continue;
            }

            if (parts[0] == "nr_periods")
            {
                periods = Number(parts[1]);
            }
            else if (parts[0] == "nr_throttled")
            {
                throttled = Number(parts[1]);
            }
        }

        return periods is > 0 && throttled is { } t ? new ThrottleCounters(periods.Value, t) : null;
    }

    private static ulong Number(string value) =>
        ulong.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : 0;

    [GeneratedRegex(@"^(?:[shv]d[a-z]+|xvd[a-z]+|nvme\d+n\d+)$", RegexOptions.CultureInvariant)]
    private static partial Regex WholeDiskPattern();
}
