using System.Globalization;
using Fleeto.Core.Domain;

namespace Fleeto.Workers.Health;

/// <summary>
/// Reads the host values of instance health (0.6.0). On Linux from /proc, which a container shares with the VPS (no Docker socket
/// needed): memory and swap from /proc/meminfo, CPU from /proc/stat, load from /proc/loadavg. The disk is the file system of the
/// container root, which is the file system of the Docker data directory on the VPS. CPU is the average since the previous read
/// (the sample interval), or over one second on the first read. Elsewhere (local development on Windows) only the disk is read.
/// </summary>
public sealed class HostMetricsReader
{
    private readonly object _lock = new();
    private CpuTimes? _previous;

    public async Task<HostHealth> ReadAsync(CancellationToken cancellationToken)
    {
        var (diskTotal, diskFree) = ReadDisk();
        if (!OperatingSystem.IsLinux() || !File.Exists("/proc/meminfo"))
        {
            return HostHealth.Unknown with { Cores = Environment.ProcessorCount, DiskTotalBytes = diskTotal, DiskFreeBytes = diskFree };
        }

        var memory = ParseMeminfo(await File.ReadAllTextAsync("/proc/meminfo", cancellationToken));
        var load = ParseLoad1(await File.ReadAllTextAsync("/proc/loadavg", cancellationToken));
        var cpu = await ReadCpuAsync(cancellationToken);
        return new HostHealth(cpu, load, Environment.ProcessorCount, memory?.Total, memory?.Available, memory?.SwapTotal, memory?.SwapFree,
            diskTotal, diskFree);
    }

    private async Task<double?> ReadCpuAsync(CancellationToken cancellationToken)
    {
        var now = ParseCpu(await File.ReadAllTextAsync("/proc/stat", cancellationToken));
        if (now is null)
        {
            return null;
        }

        CpuTimes? previous;
        lock (_lock)
        {
            previous = _previous;
        }

        if (previous is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
            previous = now;
            now = ParseCpu(await File.ReadAllTextAsync("/proc/stat", cancellationToken));
        }

        lock (_lock)
        {
            _previous = now;
        }

        return now is null ? null : CpuPercent(previous.Value, now.Value);
    }

    private static (long? Total, long? Free) ReadDisk()
    {
        try
        {
            var root = OperatingSystem.IsLinux() ? "/" : Path.GetPathRoot(AppContext.BaseDirectory) ?? "/";
            var drive = new DriveInfo(root);
            return (drive.TotalSize, drive.AvailableFreeSpace);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return (null, null);
        }
    }

    /// <summary>Total and idle jiffies of all CPUs together.</summary>
    internal readonly record struct CpuTimes(ulong Total, ulong Idle);

    internal sealed record Memory(long Total, long Available, long SwapTotal, long SwapFree);

    /// <summary>The first line of /proc/stat: "cpu user nice system idle iowait irq softirq steal guest guest_nice".</summary>
    internal static CpuTimes? ParseCpu(string procStat)
    {
        var line = procStat.Split('\n').FirstOrDefault(l => l.StartsWith("cpu ", StringComparison.Ordinal));
        if (line is null)
        {
            return null;
        }

        var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Take(8)
            .Select(v => ulong.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        if (values.Length < 4)
        {
            return null;
        }

        // Guest time is already counted in user and nice. Idle includes iowait: waiting for the disk is not CPU work.
        var idle = values[3] + (values.Length > 4 ? values[4] : 0);
        return new CpuTimes(values.Aggregate(0UL, (sum, v) => sum + v), idle);
    }

    internal static double? CpuPercent(CpuTimes before, CpuTimes after)
    {
        if (after.Total <= before.Total)
        {
            return null;
        }

        var total = after.Total - before.Total;
        var idle = after.Idle >= before.Idle ? after.Idle - before.Idle : 0;
        return Math.Round(Math.Clamp((1 - (double)idle / total) * 100, 0, 100), 1);
    }

    /// <summary>MemTotal, MemAvailable, SwapTotal and SwapFree from /proc/meminfo (kB).</summary>
    internal static Memory? ParseMeminfo(string meminfo)
    {
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in meminfo.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var number = line[(colon + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            if (long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var kilobytes))
            {
                values[line[..colon]] = kilobytes * 1024;
            }
        }

        return values.TryGetValue("MemTotal", out var total) && values.TryGetValue("MemAvailable", out var available)
            ? new Memory(total, available, values.GetValueOrDefault("SwapTotal"), values.GetValueOrDefault("SwapFree"))
            : null;
    }

    internal static double? ParseLoad1(string loadavg) =>
        double.TryParse(loadavg.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(), NumberStyles.Float, CultureInfo.InvariantCulture,
            out var load) ? load : null;
}
