using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;
using Npgsql;

namespace Fleeto.Workers.Performance;

/// <summary>
/// The benchmark of the performance monitor (0.7.0 step 3, spec §9, Server tab): what the VPS gives the workers right now. Hashing
/// on one core, writing to the disk of the work directory with fsync, and trivial round trips to the database. On an admin's
/// request only: it costs a few seconds of one core and 64 MB of writes, and its file is removed at once.
/// </summary>
public sealed class PerfBenchmark
{
    public const int CpuMegabytes = 64;
    public const int DiskMegabytes = 64;
    public const int FsyncWrites = 50;
    public const int RoundTrips = 200;

    /// <summary>An fsync slower than this on average means writes wait for slow or shared storage.</summary>
    public const double SlowFsyncMs = 10;

    /// <summary>A trivial round trip to the database in the same Docker network slower than this means the database or the VPS is busy.</summary>
    public const double SlowRoundTripMs = 2;

    private readonly NpgsqlDataSource _dataSource;
    private readonly string _directory;

    public PerfBenchmark(NpgsqlDataSource dataSource, string directory)
    {
        _dataSource = dataSource;
        _directory = directory;
    }

    public async Task<IReadOnlyList<PerfProbeItem>> RunAsync(CancellationToken cancellationToken)
    {
        var items = new List<PerfProbeItem> { Cpu() };
        items.AddRange(await DiskAsync(cancellationToken));
        items.AddRange(await DatabaseAsync(cancellationToken));
        return items;
    }

    private static PerfProbeItem Cpu()
    {
        var buffer = new byte[1024 * 1024];
        RandomNumberGenerator.Fill(buffer);
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var started = Stopwatch.GetTimestamp();
        for (var i = 0; i < CpuMegabytes; i++)
        {
            sha.AppendData(buffer);
        }

        sha.GetHashAndReset();
        var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
        return new PerfProbeItem("CPU, one core", $"SHA-256 at {Rate(CpuMegabytes, seconds)} · {Environment.ProcessorCount} cores",
            null, "Compare with an earlier run: a lower figure on the same VPS means its host is busy (see CPU steal).");
    }

    private async Task<IReadOnlyList<PerfProbeItem>> DiskAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_directory);
        foreach (var leftover in Directory.EnumerateFiles(_directory, "fleeto-benchmark-*.tmp"))
        {
            TryDelete(leftover);
        }

        var path = Path.Combine(_directory, $"fleeto-benchmark-{Guid.NewGuid():N}.tmp");
        try
        {
            var buffer = new byte[1024 * 1024];
            RandomNumberGenerator.Fill(buffer);
            double sequentialSeconds;
            await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.None))
            {
                var started = Stopwatch.GetTimestamp();
                for (var i = 0; i < DiskMegabytes; i++)
                {
                    await file.WriteAsync(buffer, cancellationToken);
                }

                file.Flush(flushToDisk: true);
                sequentialSeconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            }

            var fsyncs = new double[FsyncWrites];
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, 1, FileOptions.None))
            {
                for (var i = 0; i < FsyncWrites; i++)
                {
                    var started = Stopwatch.GetTimestamp();
                    await file.WriteAsync(buffer.AsMemory(0, 4096), cancellationToken);
                    file.Flush(flushToDisk: true);
                    fsyncs[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                }
            }

            var average = fsyncs.Average();
            return
            [
                new PerfProbeItem("Disk, sequential write", $"{Rate(DiskMegabytes, sequentialSeconds)} for {DiskMegabytes} MB with fsync", null),
                new PerfProbeItem("Disk, fsync", $"{PerformanceRules.Ms(average)} on average, {PerformanceRules.Ms(fsyncs.Max())} at most over {FsyncWrites} small writes",
                    average <= SlowFsyncMs, average <= SlowFsyncMs ? null : "Every commit of the database waits for an fsync like this. Slow fsyncs mean network or shared storage: ask the hosting provider, or move to local SSD.")
            ];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [new PerfProbeItem("Disk", $"Could not write to the work directory ({ex.GetType().Name})", false, "The workers need a writable work directory; install.sh creates it.")];
        }
        finally
        {
            TryDelete(path);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the next benchmark, which removes it first.
        }
    }

    private async Task<IReadOnlyList<PerfProbeItem>> DatabaseAsync(CancellationToken cancellationToken)
    {
        // The monitor's own work: not a query per item of the loop that runs it.
        using var scope = PerfContext.Begin(PerfKeys.MonitorSource);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("SELECT 1", connection);
        await command.ExecuteScalarAsync(cancellationToken);
        var times = new double[RoundTrips];
        for (var i = 0; i < RoundTrips; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await command.ExecuteScalarAsync(cancellationToken);
            times[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }

        Array.Sort(times);
        var median = times[RoundTrips / 2];
        var p95 = times[(int)(RoundTrips * 0.95)];
        return
        [
            new PerfProbeItem("Database round trip", $"{PerformanceRules.Ms(median)} median, {PerformanceRules.Ms(p95)} at the 95th percentile over {RoundTrips} trivial queries",
                median <= SlowRoundTripMs, median <= SlowRoundTripMs ? null : "A query that does nothing still waits this long: the database or the VPS is busy. Compare with the CPU and the database tab.")
        ];
    }

    private static string Rate(int megabytes, double seconds) =>
        seconds <= 0 ? "–" : (megabytes / seconds).ToString(megabytes / seconds >= 100 ? "0" : "0.#", CultureInfo.InvariantCulture) + " MB/s";
}
