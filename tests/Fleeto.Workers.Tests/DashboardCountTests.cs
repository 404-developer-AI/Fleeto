using Fleeto.Core.Entities;
using Fleeto.Workers.Dashboards;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Alerts opened per client, day and severity for the charts of the dashboard (0.8.0): counted again for today and yesterday, the history
/// kept after the alerts themselves are gone, and counts older than a year and a month removed.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class DashboardCountTests
{
    private readonly WorkersFixture _fixture;

    public DashboardCountTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Alerts_are_counted_per_client_day_and_severity_and_the_count_outlives_the_alerts()
    {
        var client = await _fixture.Db.CreateClientAsync();
        var site = await _fixture.Db.CreateSiteAsync(client.Id);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed);
        var now = _fixture.Db.Time.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            foreach (var (severity, opened) in new[]
                     {
                         (AlertSeverity.Critical, now), (AlertSeverity.Warning, now), (AlertSeverity.Warning, now.AddDays(-1)),
                     })
            {
                db.Alerts.Add(new Alert
                {
                    Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Kind = AlertKind.Check, Severity = severity,
                    Title = "Disk free", OpenedAt = opened, UpdatedAt = opened, Target = Guid.NewGuid().ToString()
                });
            }

            db.DashboardDailyCounts.Add(new DashboardDailyCount
            {
                ClientId = client.Id, Day = today.AddDays(-DashboardCountService.KeepDays - 1), Warning = 4, CountedAt = now
            });
            await db.SaveChangesAsync();
        }

        Assert.True(await _fixture.DashboardCounts().CountAsync(CancellationToken.None) >= 2);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var counts = await db.DashboardDailyCounts.Where(c => c.ClientId == client.Id).OrderBy(c => c.Day).ToListAsync();
            Assert.Equal(2, counts.Count);
            Assert.Equal((today.AddDays(-1), 1, 0), (counts[0].Day, counts[0].Warning, counts[0].Critical));
            Assert.Equal((today, 1, 1), (counts[1].Day, counts[1].Warning, counts[1].Critical));
            await db.Alerts.Where(a => a.ClientId == client.Id).ExecuteDeleteAsync();
        }

        await _fixture.DashboardCounts().CountAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(2, await db.DashboardDailyCounts.CountAsync(c => c.ClientId == client.Id));
        }
    }
}
