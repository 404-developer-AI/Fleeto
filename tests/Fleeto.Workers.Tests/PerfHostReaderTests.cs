using Fleeto.Core.Entities;
using Fleeto.Workers.Performance;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The VPS as the performance monitor reads it (0.7.0): CPU split into busy, steal and iowait, whole disks only, and CPU throttling
/// of the container, each as the difference between two reads of what Linux writes.
/// </summary>
public sealed class PerfHostReaderTests
{
    private const string StatBefore = "cpu  1000 0 500 8000 200 0 0 300 0 0\ncpu0 500 0 250 4000 100 0 0 150 0 0\n";
    private const string StatAfter = "cpu  1600 0 700 8800 400 0 0 500 0 0\ncpu0 800 0 350 4400 200 0 0 250 0 0\n";

    private const string DisksBefore = """
           8       0 sda 1000 0 0 2000 500 0 0 1000 0 1500 3000 0 0 0 0
           8       1 sda1 900 0 0 1800 400 0 0 900 0 1300 2700 0 0 0 0
           7       0 loop0 10 0 0 5 0 0 0 0 0 5 5 0 0 0 0
         259       0 nvme0n1 100 0 0 100 100 0 0 100 0 100 200 0 0 0 0
         259       1 nvme0n1p1 100 0 0 100 100 0 0 100 0 100 200 0 0 0 0
        """;

    private const string DisksAfter = """
           8       0 sda 1100 0 0 2400 600 0 0 1600 0 2100 4000 0 0 0 0
           8       1 sda1 990 0 0 2100 480 0 0 1400 0 1800 3500 0 0 0 0
           7       0 loop0 20 0 0 9 0 0 0 0 0 9 9 0 0 0 0
         259       0 nvme0n1 100 0 0 100 100 0 0 100 0 100 200 0 0 0 0
         259       1 nvme0n1p1 100 0 0 100 100 0 0 100 0 100 200 0 0 0 0
        """;

    [Fact]
    public void Cpu_is_split_into_busy_steal_and_iowait()
    {
        var before = PerfHostReader.ParseCpu(StatBefore)!.Value;
        var after = PerfHostReader.ParseCpu(StatAfter)!.Value;
        var row = new PerfHostMinute();
        PerfHostReader.Apply(row, new PerfHostReader.Counters(0, before, new Dictionary<string, PerfHostReader.DiskCounters>(), null),
            new PerfHostReader.Counters(60_000, after, new Dictionary<string, PerfHostReader.DiskCounters>(), null));
        // Total went up by 2000: 600 user, 200 system, 800 idle, 200 iowait, 200 steal.
        Assert.Equal(50, row.CpuPercent);
        Assert.Equal(10, row.StealPercent);
        Assert.Equal(10, row.IowaitPercent);
        Assert.Null(row.DiskLatencyMs);
    }

    [Fact]
    public void Only_whole_disks_count_for_latency_and_utilization()
    {
        var before = PerfHostReader.ParseDisks(DisksBefore);
        Assert.Equal(new[] { "nvme0n1", "sda" }, before.Keys.Order());
        var row = new PerfHostMinute();
        PerfHostReader.Apply(row, new PerfHostReader.Counters(0, null, before, null),
            new PerfHostReader.Counters(60_000, null, PerfHostReader.ParseDisks(DisksAfter), null));
        // sda: 200 operations took 400 + 600 ms; busy 600 ms of 60 s.
        Assert.Equal(5, row.DiskLatencyMs);
        Assert.Equal(1, row.DiskUtilizationPercent);
        Assert.Null(row.CpuPercent);
    }

    [Fact]
    public void Throttling_is_the_share_of_periods_the_container_was_throttled_and_absent_without_a_limit()
    {
        var before = PerfHostReader.ParseThrottling("usage_usec 1\nnr_periods 1000\nnr_throttled 10\nthrottled_usec 5\n");
        var after = PerfHostReader.ParseThrottling("usage_usec 2\nnr_periods 1200\nnr_throttled 60\nthrottled_usec 9\n");
        var row = new PerfHostMinute();
        PerfHostReader.Apply(row, new PerfHostReader.Counters(0, null, new Dictionary<string, PerfHostReader.DiskCounters>(), before),
            new PerfHostReader.Counters(60_000, null, new Dictionary<string, PerfHostReader.DiskCounters>(), after));
        Assert.Equal(25, row.ThrottledPercent);
        Assert.Null(PerfHostReader.ParseThrottling("usage_usec 1\nnr_periods 0\nnr_throttled 0\n"));
        Assert.Null(PerfHostReader.ParseThrottling(null));
    }

    [Fact]
    public async Task The_first_read_only_sets_the_baseline()
    {
        var reader = new PerfHostReader();
        var row = await reader.ReadAsync(new DateTime(2026, 10, 18, 10, 0, 0, DateTimeKind.Utc), CancellationToken.None);
        Assert.Null(row.CpuPercent);
        Assert.Equal(Environment.ProcessorCount, row.Cores);
    }
}
