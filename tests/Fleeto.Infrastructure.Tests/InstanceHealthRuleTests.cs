using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The rules of instance health (0.6.0): which values need attention, how severe, and that every finding states the cause
/// and the next step. A healthy instance has no findings.
/// </summary>
public sealed class InstanceHealthRuleTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private static readonly DateTime Now = new(2026, 9, 28, 12, 0, 0, DateTimeKind.Utc);

    private static readonly FleetoHealth Healthy = new([], true, Now.AddHours(-3), false, 0, null, 0, null, 0, null, 10, 12);

    private static HealthSnapshot Snapshot(HostHealth? host = null, FleetoHealth? fleeto = null, int connections = 10, int maxConnections = 100) =>
        new(Now, host ?? HostHealth.Unknown, 4 * Gb, connections, maxConnections,
            new InstanceHealthDetail([new TableSize("CheckResults", 41 * Gb, 1_000_000), new TableSize("Alerts", Gb, 5_000)], fleeto ?? Healthy));

    private static IReadOnlyList<HealthFinding> Evaluate(HealthSnapshot snapshot, IReadOnlyList<DiskPoint>? disk = null, IReadOnlyList<double>? cpu = null) =>
        InstanceHealthRules.Evaluate(snapshot, disk ?? [], cpu ?? []);

    [Fact]
    public void A_healthy_instance_has_no_findings()
    {
        var host = new HostHealth(20, 0.5, 4, 8 * Gb, 5 * Gb, 2 * Gb, 2 * Gb, 100 * Gb, 60 * Gb);
        Assert.Empty(Evaluate(Snapshot(host), cpu: [20, 25, 30]));
        Assert.Empty(Evaluate(Snapshot()));
    }

    [Theory]
    [InlineData(25, null)]
    [InlineData(19, AlertSeverity.Warning)]
    [InlineData(9, AlertSeverity.Critical)]
    public void The_disk_is_judged_on_how_full_it_is(long freeGb, AlertSeverity? expected)
    {
        var finding = Evaluate(Snapshot(HostHealth.Unknown with { DiskTotalBytes = 100 * Gb, DiskFreeBytes = freeGb * Gb })).SingleOrDefault(f => f.Key == "disk");
        Assert.Equal(expected, finding?.Severity);
        if (finding is not null)
        {
            Assert.Contains("CheckResults 41 GB", finding.Detail);
            Assert.Contains("Settings, Retention", finding.Detail);
        }
    }

    [Fact]
    public void A_disk_that_fills_within_two_weeks_is_a_problem_before_it_is_80_percent_full()
    {
        // 50 GB used, growing 3 GB a day on a 100 GB disk: full in about 17 days, then 11 days.
        List<DiskPoint> Growth(double perDay) => Enumerable.Range(0, 7 * 24)
            .Select(h => new DiskPoint(Now.AddHours(-7 * 24 + h + 1), (long)((50 - perDay * (7 * 24 - h - 1) / 24.0) * Gb))).ToList();

        Assert.Equal(16.7, Math.Round(InstanceHealthRules.DaysUntilFull(Growth(3), 100 * Gb)!.Value, 1));
        var host = HostHealth.Unknown with { DiskTotalBytes = 100 * Gb, DiskFreeBytes = 50 * Gb };
        Assert.DoesNotContain(Evaluate(Snapshot(host), Growth(3)), f => f.Key == "disk");

        var growing = Evaluate(Snapshot(host), Growth(4.5)).Single(f => f.Key == "disk");
        Assert.Equal(AlertSeverity.Warning, growing.Severity);
        Assert.Contains("full in about 11 days", growing.Title);
        Assert.Contains("It grows 4.5 GB a day.", growing.Detail);

        // Too little history, or a disk that shrinks, gives no forecast.
        Assert.Null(InstanceHealthRules.DaysUntilFull(Growth(4.5).TakeLast(6).ToList(), 100 * Gb));
        Assert.Null(InstanceHealthRules.DaysUntilFull(Growth(-1), 100 * Gb));
    }

    [Fact]
    public void Memory_swap_cpu_and_connections_have_their_own_thresholds()
    {
        var lowMemory = HostHealth.Unknown with { MemoryTotalBytes = 8 * Gb, MemoryAvailableBytes = Gb / 4 };
        Assert.Equal(AlertSeverity.Critical, Evaluate(Snapshot(lowMemory)).Single(f => f.Key == "memory").Severity);
        var swapping = HostHealth.Unknown with { MemoryTotalBytes = 8 * Gb, MemoryAvailableBytes = 3 * Gb, SwapTotalBytes = 2 * Gb, SwapFreeBytes = Gb / 2 };
        var swap = Evaluate(Snapshot(swapping)).Single(f => f.Key == "memory");
        Assert.Equal(AlertSeverity.Warning, swap.Severity);
        Assert.Contains("Swap is 75% in use", swap.Detail);

        // The CPU needs a quarter of an hour of samples: one busy sample is not a problem.
        Assert.DoesNotContain(Evaluate(Snapshot(), cpu: [99]), f => f.Key == "cpu");
        Assert.Equal(AlertSeverity.Warning, Evaluate(Snapshot(), cpu: [88, 90, 86]).Single(f => f.Key == "cpu").Severity);
        Assert.Equal(AlertSeverity.Critical, Evaluate(Snapshot(), cpu: [97, 99, 96]).Single(f => f.Key == "cpu").Severity);

        Assert.Equal(AlertSeverity.Warning, Evaluate(Snapshot(connections: 85)).Single(f => f.Key == "database.connections").Severity);
        Assert.Equal(AlertSeverity.Critical, Evaluate(Snapshot(connections: 96)).Single(f => f.Key == "database.connections").Severity);
    }

    [Fact]
    public void Stuck_workers_old_backups_and_waiting_notifications_are_problems()
    {
        var fleeto = Healthy with
        {
            StaleLoops = ["patch-sync"],
            LastBackupAt = Now.AddDays(-3),
            LastBackupFailed = true,
            OutboxEmails = 12,
            OldestOutboxEmail = Now.AddHours(-1),
            OutboxWebhooks = 1,
            OldestOutboxWebhook = Now.AddMinutes(-5),
            UnprocessedEvents = 40,
            OldestUnprocessedEvent = Now.AddMinutes(-20)
        };

        var findings = Evaluate(Snapshot(fleeto: fleeto)).ToDictionary(f => f.Key);

        Assert.Equal(AlertSeverity.Critical, findings["workers"].Severity);
        Assert.Contains("patch-sync", findings["workers"].Title);
        Assert.Equal("The last successful backup is 3 days old.", findings["backups"].Title);
        Assert.Contains("The last backup failed.", findings["backups"].Detail);
        Assert.Equal("12 emails wait longer than 30 minutes to be sent.", findings["notifications.email"].Title);
        Assert.False(findings.ContainsKey("notifications.webhooks"));
        Assert.Equal(AlertSeverity.Warning, findings["workers.events"].Severity);

        Assert.Equal("No backup destination is configured.",
            Evaluate(Snapshot(fleeto: Healthy with { BackupConfigured = false })).Single(f => f.Key == "backups").Title);
    }

    [Fact]
    public void The_stored_detail_survives_the_round_trip_and_damage_reads_as_empty()
    {
        var detail = new InstanceHealthDetail([new TableSize("Alerts", 42, 7)], Healthy with { StaleLoops = ["retention"] });
        var parsed = InstanceHealthRules.ParseDetail(InstanceHealthRules.SerializeDetail(detail));

        Assert.Equal(detail.Tables, parsed.Tables);
        Assert.Equal(["retention"], parsed.Fleeto.StaleLoops);
        Assert.Same(InstanceHealthDetail.Empty, InstanceHealthRules.ParseDetail("{broken"));
    }
}
