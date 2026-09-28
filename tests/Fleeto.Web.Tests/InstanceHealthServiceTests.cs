using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Instance health in web (0.6.0): admins only, the tile says when nothing is measured, the page shows the open issue with its
/// component, the diagnostics hold no secrets or personal data and every export is audited, and a channel chooses instance health.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class InstanceHealthServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly WebFixture _fixture;

    public InstanceHealthServiceTests(WebFixture fixture) => _fixture = fixture;

    private InstanceHealthService Health => _fixture.Services.GetRequiredService<InstanceHealthService>();

    [Fact]
    public async Task Admins_see_what_the_workers_measured_and_others_see_nothing()
    {
        var technician = WebFixture.Technician();
        Assert.Null(await Health.GetTileAsync(technician));
        Assert.Null(await Health.GetAsync(technician));
        Assert.False((await Health.ExportDiagnosticsAsync(technician, "copied")).Success);

        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.InstanceHealthIssues.ExecuteDeleteAsync();
            await db.InstanceHealthSamples.ExecuteDeleteAsync();
        }

        Assert.Equal(HealthStatus.Unknown, (await Health.GetTileAsync(WebFixture.Admin()))!.Status);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.InstanceHealthSamples.Add(new InstanceHealthSample
            {
                Time = now.AddMinutes(-2), CpuPercent = 12, Cores = 4, Load1 = 0.4, MemoryTotalBytes = 8 * Gb, MemoryAvailableBytes = 6 * Gb,
                DiskTotalBytes = 100 * Gb, DiskFreeBytes = 12 * Gb, DatabaseBytes = 3 * Gb, DatabaseConnections = 12, DatabaseMaxConnections = 100,
                DetailJson = InstanceHealthRules.SerializeDetail(new InstanceHealthDetail([new TableSize("CheckResults", 2 * Gb, 900_000)],
                    FleetoHealth.Empty with { BackupConfigured = true, LastBackupAt = now.AddHours(-2), Endpoints = 3, EndpointsOnline = 2 }))
            });
            db.InstanceHealthIssues.Add(new InstanceHealthIssue
            {
                Id = Guid.NewGuid(), Key = "disk", Component = HealthComponent.Disk, Severity = AlertSeverity.Warning,
                Title = "The disk of the VPS is 88% full (12 GB free).", Detail = "Lower the retention.", OpenedAt = now.AddHours(-1), UpdatedAt = now
            });
            await db.SaveChangesAsync();
        }

        var tile = (await Health.GetTileAsync(WebFixture.Admin()))!;
        Assert.Equal((HealthStatus.Warning, 1, "The disk of the VPS is 88% full (12 GB free)."), (tile.Status, tile.OpenIssues, tile.Text));

        var view = (await Health.GetAsync(WebFixture.Admin()))!;
        Assert.False(view.Stale);
        Assert.Equal(HealthStatus.Warning, view.Overall);
        var disk = view.Components.Single(c => c.Component == HealthComponent.Disk);
        Assert.Equal(HealthStatus.Warning, disk.Status);
        Assert.StartsWith("88% used · 12 GB free of 100 GB", disk.Value);
        Assert.Equal(HealthStatus.Ok, view.Components.Single(c => c.Component == HealthComponent.Memory).Status);
        Assert.Equal(4, view.Trends.Count);
        Assert.All(view.Trends, t => Assert.Equal(168, t.Points.Count));
    }

    [Fact]
    public async Task The_diagnostics_are_markdown_without_personal_data_and_every_export_is_audited()
    {
        var admin = WebFixture.Admin();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        await _fixture.Database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "LAPTOP-PRIVATE-NAME");

        var result = await Health.ExportDiagnosticsAsync(admin, "downloaded");

        Assert.True(result.Success, result.Problem);
        var report = result.Value!;
        Assert.StartsWith("# Fleeto instance diagnostics", report);
        Assert.Contains("## Components", report);
        Assert.Contains("## Costliest database statements", report);
        Assert.DoesNotContain("LAPTOP-PRIVATE-NAME", report);
        Assert.DoesNotContain(client.Name, report);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var entry = await db.AuditEntries.Where(a => a.Action == AuditActions.InstanceDiagnosticsExported).OrderByDescending(a => a.Id).FirstAsync();
        Assert.Contains("downloaded", entry.DetailsJson);
    }

    [Fact]
    public async Task A_channel_chooses_instance_health()
    {
        var channels = _fixture.Services.GetRequiredService<NotificationChannelService>();
        var input = new NotificationChannelInput("Ops " + Guid.NewGuid().ToString("N")[..6], NotificationChannelType.Email, "ops@example.com",
            WebhookFormat.Generic, null, AlertSeverity.Warning, true, true, true, [], InstanceHealth: true);

        var saved = await channels.SaveAsync(WebFixture.Admin(), null, input);

        Assert.True(saved.Success, saved.Problem);
        Assert.True((await channels.ListAsync(WebFixture.Admin())).Single(c => c.Id == saved.Value!.Id).InstanceHealth);
    }
}
