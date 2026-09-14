using Fleetify.Core.Entities;
using Fleetify.Workers.Alerts;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Workers.Tests;

/// <summary>
/// Guarantees that alert transitions queue one email per recipient of every enabled channel whose minimum severity the
/// alert meets, that resolutions only notify channels with notify-on-resolve, and that nothing is sent twice.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AlertNotificationTests
{
    private readonly WorkersFixture _fixture;

    public AlertNotificationTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Notifications_respect_minimum_severity_and_notify_on_resolve()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-MAIL");
        var cpu = await _fixture.CreateCheckAsync(site, CheckType.CpuUsage, 80, 90);

        var suffix = Guid.NewGuid().ToString("N")[..8];
        var criticalOnly = $"critical-{suffix}@test.example";
        var everything = $"all-{suffix}@test.example";
        var disabled = $"disabled-{suffix}@test.example";
        var channelIds = new List<Guid>();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var now = _fixture.Now;
            var channels = new[]
            {
                new NotificationChannel { Id = Guid.NewGuid(), Name = "Critical " + suffix, Recipients = criticalOnly, MinimumSeverity = AlertSeverity.Critical, NotifyOnResolve = false, CreatedAt = now, UpdatedAt = now },
                new NotificationChannel { Id = Guid.NewGuid(), Name = "All " + suffix, Recipients = $"{everything}; {everything.ToUpperInvariant()}", MinimumSeverity = AlertSeverity.Warning, NotifyOnResolve = true, CreatedAt = now, UpdatedAt = now },
                new NotificationChannel { Id = Guid.NewGuid(), Name = "Off " + suffix, Recipients = disabled, Enabled = false, CreatedAt = now, UpdatedAt = now }
            };
            db.NotificationChannels.AddRange(channels);
            channelIds.AddRange(channels.Select(c => c.Id));
            await db.SaveChangesAsync();
        }

        try
        {
            var evaluation = _fixture.CheckEvaluation();
            await _fixture.InsertResultAsync(endpoint, cpu, 85);
            await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
            _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
            await _fixture.InsertResultAsync(endpoint, cpu, 86);
            await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
            _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
            await _fixture.InsertResultAsync(endpoint, cpu, 95);
            await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
            _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
            await _fixture.InsertResultAsync(endpoint, cpu, 10);
            await evaluation.EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);

            await using var db = _fixture.Db.DbFactory.CreateSystem();
            var emails = await db.OutboxEmails.AsNoTracking()
                .Where(e => e.ToAddress == criticalOnly || e.ToAddress == everything || e.ToAddress == disabled || e.ToAddress == everything.ToUpperInvariant())
                .ToListAsync();

            Assert.Equal([AlertNotificationService.CategoryEscalated],
                emails.Where(e => e.ToAddress == criticalOnly).Select(e => e.Category).ToArray());
            Assert.Equal([AlertNotificationService.CategoryOpened, AlertNotificationService.CategoryEscalated, AlertNotificationService.CategoryResolved],
                emails.Where(e => e.ToAddress == everything).OrderBy(e => e.CreatedAt).Select(e => e.Category).ToArray());
            Assert.DoesNotContain(emails, e => e.ToAddress == disabled || e.ToAddress == everything.ToUpperInvariant());

            var opened = emails.Single(e => e.ToAddress == everything && e.Category == AlertNotificationService.CategoryOpened);
            Assert.Equal("SRV-MAIL has used 85% CPU on average, above the 80% threshold.", opened.Subject);
            Assert.Contains("SRV-MAIL", opened.TextBody);
            Assert.Contains($"/endpoints/{endpoint.Id:D}", opened.HtmlBody);
            Assert.Equal(0, opened.Attempts);
            Assert.Null(opened.SentAt);
        }
        finally
        {
            await using var db = _fixture.Db.DbFactory.CreateSystem();
            await db.NotificationChannels.Where(c => channelIds.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }
}
