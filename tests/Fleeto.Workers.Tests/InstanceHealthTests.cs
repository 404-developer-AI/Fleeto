using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Workers.Health;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Instance health in the workers (0.6.0): a sample is stored, a problem opens an issue, a worse one escalates it and one that
/// stays away 15 minutes resolves it, and only the channels an admin chose for instance health are told. The /proc parsers read
/// what Linux writes.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class InstanceHealthTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly WorkersFixture _fixture;

    public InstanceHealthTests(WorkersFixture fixture) => _fixture = fixture;

    private InstanceHealthService Service() =>
        new(_fixture.Db.DbFactory, _fixture.Settings(), new HostMetricsReader(), _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<InstanceHealthService>.Instance);

    private static HostHealth Disk(long totalGb, long freeGb) => HostHealth.Unknown with { DiskTotalBytes = totalGb * Gb, DiskFreeBytes = freeGb * Gb };

    [Fact]
    public async Task A_disk_problem_opens_escalates_and_resolves_and_reaches_only_instance_health_channels()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var health = $"health-{suffix}@example.com";
        var other = $"other-{suffix}@example.com";
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.InstanceHealthIssues.ExecuteDeleteAsync();
            var now = _fixture.Now;
            db.NotificationChannels.Add(new NotificationChannel
            {
                Id = Guid.NewGuid(), Name = "Health " + suffix, Recipients = health, InstanceHealth = true, CreatedAt = now, UpdatedAt = now
            });
            db.NotificationChannels.Add(new NotificationChannel { Id = Guid.NewGuid(), Name = "Alerts " + suffix, Recipients = other, CreatedAt = now, UpdatedAt = now });
            await db.SaveChangesAsync();
        }

        var service = Service();
        var findings = await service.SampleAsync(Disk(100, 15), CancellationToken.None);
        Assert.Contains(findings, f => f is { Key: "disk", Severity: AlertSeverity.Warning });

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var issue = await db.InstanceHealthIssues.SingleAsync(i => i.Key == "disk" && i.ResolvedAt == null);
            Assert.Equal((HealthComponent.Disk, AlertSeverity.Warning), (issue.Component, issue.Severity));
            Assert.StartsWith("The disk of the VPS is 85% full (15 GB free).", issue.Title);
            Assert.Equal(1, await db.OutboxEmails.CountAsync(e => e.ToAddress == health && e.Category == InstanceHealthService.CategoryOpened && e.Subject.Contains("disk")));
            Assert.Equal(0, await db.OutboxEmails.CountAsync(e => e.ToAddress == other));
            Assert.Single(await db.InstanceHealthSamples.Where(s => s.DiskFreeBytes == 15 * Gb).ToListAsync());
        }

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(5));
        await service.SampleAsync(Disk(100, 5), CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(AlertSeverity.Critical, (await db.InstanceHealthIssues.SingleAsync(i => i.Key == "disk" && i.ResolvedAt == null)).Severity);
            Assert.Equal(1, await db.OutboxEmails.CountAsync(e => e.ToAddress == health && e.Category == InstanceHealthService.CategoryEscalated));
        }

        // A healthy value does not resolve at once, so a disk around a threshold does not flap.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(5));
        await service.SampleAsync(Disk(100, 60), CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.True(await db.InstanceHealthIssues.AnyAsync(i => i.Key == "disk" && i.ResolvedAt == null));
        }

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(11));
        await service.SampleAsync(Disk(100, 60), CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.False(await db.InstanceHealthIssues.AnyAsync(i => i.Key == "disk" && i.ResolvedAt == null));
            Assert.Equal(1, await db.OutboxEmails.CountAsync(e => e.ToAddress == health && e.Category == InstanceHealthService.CategoryResolved));
            Assert.Equal(0, await db.OutboxEmails.CountAsync(e => e.ToAddress == other));
        }
    }

    [Fact]
    public async Task Database_values_and_query_statistics_are_read_without_failing()
    {
        var service = Service();
        await service.SampleAsync(HostHealth.Unknown, CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var sample = await db.InstanceHealthSamples.OrderByDescending(s => s.Id).FirstAsync();
            Assert.True(sample.DatabaseBytes > 0);
            Assert.True(sample.DatabaseMaxConnections > 0);
            Assert.Contains(InstanceHealthRules.ParseDetail(sample.DetailJson).Tables, t => t.Name == "CheckResults");
        }

        // Without the extension (local and CI databases) the reason is stored instead of statements.
        var statistics = await service.ReadQueryStatisticsAsync(CancellationToken.None);
        Assert.True(statistics.Problem is not null || statistics.Queries.Count > 0);
        Assert.NotNull(await _fixture.Settings().GetAsync<QueryStatistics>(Infrastructure.Settings.SettingKeys.InstanceHealthQueries));
    }

    [Fact]
    public void The_proc_parsers_read_what_linux_writes()
    {
        var memory = HostMetricsReader.ParseMeminfo("MemTotal:        8023456 kB\nMemFree:          123456 kB\nMemAvailable:    2000000 kB\nSwapTotal:       1048572 kB\nSwapFree:         524286 kB\n");
        Assert.Equal(new HostMetricsReader.Memory(8023456L * 1024, 2000000L * 1024, 1048572L * 1024, 524286L * 1024), memory);
        Assert.Null(HostMetricsReader.ParseMeminfo("garbage"));

        var before = HostMetricsReader.ParseCpu("cpu  100 0 100 700 100 0 0 0 0 0\ncpu0 1 2 3 4\n")!.Value;
        var after = HostMetricsReader.ParseCpu("cpu  250 0 150 750 150 0 0 0 0 0\n")!.Value;
        Assert.Equal(66.7, HostMetricsReader.CpuPercent(before, after));
        Assert.Null(HostMetricsReader.CpuPercent(after, after));
        Assert.Null(HostMetricsReader.ParseCpu("intr 1 2 3"));

        Assert.Equal(0.42, HostMetricsReader.ParseLoad1("0.42 0.50 0.61 1/345 6789\n"));
    }
}
