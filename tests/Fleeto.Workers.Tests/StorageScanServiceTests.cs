using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Storage analysis in the workers (0.6.0): a folder growth check gets a result after every scan, once per scan, measured
/// against a complete scan of the period, and it alerts like any check; a Disk free check in warning asks for a scan of a
/// managed endpoint with an agent that can scan, once per 12 hours; and retention keeps every scan for 30 days, then one a week.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class StorageScanServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private const string ScanningAgent = "0.6.0";
    private readonly WorkersFixture _fixture;

    public StorageScanServiceTests(WorkersFixture fixture) => _fixture = fixture;

    private async Task<Endpoint> ManagedEndpointAsync(Site site, string hostname, string agentVersion = ScanningAgent, EndpointTier tier = EndpointTier.Managed)
    {
        await _fixture.Db.LoadTestLicenseAsync(100_000);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, tier, hostname, EndpointClass.Server);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.AgentVersion, agentVersion));
        return endpoint;
    }

    private async Task<StorageScan> AddScanAsync(Endpoint endpoint, DateTime receivedAt, bool complete = true, string volume = "C:",
        params (string Path, long Gigabytes)[] folders)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var scan = new StorageScan
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, AgentScanId = Guid.NewGuid(), Volume = volume,
            Filesystem = "NTFS", TotalBytes = 1000 * Gb, FreeBytes = 500 * Gb, ReceivedAt = receivedAt, Method = StorageScanMethod.Mft,
            Complete = complete, FoldersJson = StorageRules.SerializeFolders(folders.Select(f => new StorageFolderEntry(f.Path, f.Gigabytes * Gb, 0, 0))),
            FilesJson = "[]"
        };
        db.StorageScans.Add(scan);
        await db.SaveChangesAsync();
        return scan;
    }

    private async Task<List<CheckResult>> ResultsAsync(Endpoint endpoint, CheckDefinition check)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.CheckResults.AsNoTracking().Where(r => r.EndpointId == endpoint.Id && r.CheckDefinitionId == check.Id).ToListAsync();
    }

    [Fact]
    public async Task A_folder_growth_check_gets_a_result_once_per_scan_and_alerts_on_its_thresholds()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await ManagedEndpointAsync(site, "SRV-GROWTH-1");
        var check = await _fixture.CreateCheckAsync(site, CheckType.FolderGrowth, 10, 50, name: "Growth",
            parameters: """{"drive":"*","period_days":"7"}""");
        var now = _fixture.Now;
        await AddScanAsync(endpoint, now.AddDays(-7), true, "C:", (@"C:\", 100), (@"C:\SQL", 20), (@"C:\SQL\Backup", 10));
        await AddScanAsync(endpoint, now, true, "C:", (@"C:\", 160), (@"C:\SQL", 75), (@"C:\SQL\Backup", 62));

        var service = _fixture.StorageScans();
        await service.EvaluateNewScansAsync(CancellationToken.None);
        await service.EvaluateNewScansAsync(CancellationToken.None);

        var result = Assert.Single(await ResultsAsync(endpoint, check));
        Assert.Equal(60, result.Value);
        Assert.Equal("C:", result.Target);
        Assert.Equal(@"+60 GB in 7 days, most in C:\SQL\Backup (+52 GB)", result.Detail);

        await _fixture.CheckEvaluation().EvaluateEndpointAsync(endpoint.Id, CancellationToken.None);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.EndpointId == endpoint.Id && a.CheckDefinitionId == check.Id);
        Assert.Equal(AlertSeverity.Critical, alert.Severity);
        Assert.DoesNotContain("SQL", alert.Title, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_older_complete_scan_or_with_another_drive_there_is_no_result()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await ManagedEndpointAsync(site, "SRV-GROWTH-2");
        var check = await _fixture.CreateCheckAsync(site, CheckType.FolderGrowth, 10, 50, parameters: """{"drive":"d"}""");
        var other = await ManagedEndpointAsync(site, "SRV-GROWTH-3");
        var now = _fixture.Now;
        // An incomplete scan is never a baseline: its sizes are lower bounds.
        await AddScanAsync(endpoint, now.AddDays(-8), false, "D:", (@"D:\", 10));
        await AddScanAsync(endpoint, now, true, "D:", (@"D:\", 90));
        // The check is for D:, so a scan of C: gives it nothing.
        await AddScanAsync(other, now.AddDays(-7), true, "C:", (@"C:\", 10));
        await AddScanAsync(other, now, true, "C:", (@"C:\", 90));

        await _fixture.StorageScans().EvaluateNewScansAsync(CancellationToken.None);

        Assert.Empty(await ResultsAsync(endpoint, check));
        Assert.Empty(await ResultsAsync(other, check));
    }

    [Fact]
    public async Task An_agent_only_endpoint_gets_no_folder_growth_result()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await ManagedEndpointAsync(site, "WS-GROWTH-4", tier: EndpointTier.AgentOnly);
        var check = await _fixture.CreateCheckAsync(site, CheckType.FolderGrowth, 10, 50);
        var now = _fixture.Now;
        await AddScanAsync(endpoint, now.AddDays(-7), true, "C:", (@"C:\", 10));
        await AddScanAsync(endpoint, now, true, "C:", (@"C:\", 90));

        await _fixture.StorageScans().EvaluateNewScansAsync(CancellationToken.None);

        Assert.Empty(await ResultsAsync(endpoint, check));
    }

    [Fact]
    public async Task A_disk_free_check_in_warning_asks_a_capable_managed_endpoint_for_a_scan_once()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var diskFree = await _fixture.CreateCheckAsync(site, CheckType.DiskFree, 15, 5, parameters: """{"drive":"*"}""");
        var low = await ManagedEndpointAsync(site, "SRV-LOW-1");
        var oldAgent = await ManagedEndpointAsync(site, "SRV-LOW-2", agentVersion: "0.5.0");
        var fine = await ManagedEndpointAsync(site, "SRV-FINE");
        var agentOnly = await ManagedEndpointAsync(site, "WS-LOW-3", tier: EndpointTier.AgentOnly);
        var now = _fixture.Now;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            foreach (var (endpoint, status) in new[] { (low, CheckStatus.Warning), (oldAgent, CheckStatus.Critical), (fine, CheckStatus.Ok), (agentOnly, CheckStatus.Critical) })
            {
                db.CheckStates.Add(new CheckState
                {
                    EndpointId = endpoint.Id, ClientId = endpoint.ClientId, CheckDefinitionId = diskFree.Id, Target = "C:", Status = status,
                    Value = 3, LastResultAt = now, UpdatedAt = now
                });
            }

            await db.SaveChangesAsync();
        }

        var service = _fixture.StorageScans();
        await service.RequestScansForDiskFreeAsync(CancellationToken.None);
        await service.RequestScansForDiskFreeAsync(CancellationToken.None);

        await using var verify = _fixture.Db.DbFactory.CreateSystem();
        var ids = new[] { low.Id, oldAgent.Id, fine.Id, agentOnly.Id };
        var request = Assert.Single(await verify.StorageScanRequests.AsNoTracking().Where(r => ids.Contains(r.EndpointId)).ToListAsync());
        Assert.Equal(low.Id, request.EndpointId);
        Assert.Equal(StorageScanReason.DiskFree, request.Reason);
        Assert.Null(request.RequestedByUserId);
        Assert.Equal(now + StorageRules.DiskFreeScanGap, request.ExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Retention_keeps_every_scan_for_30_days_and_then_the_first_of_each_week_for_13_months()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await ManagedEndpointAsync(site, "SRV-RETENTION");
        var now = _fixture.Now;
        var recent = await AddScanAsync(endpoint, now.AddDays(-2), true, "C:", (@"C:\", 1));
        // Two scans in one week, 60 days ago: the first stays.
        var weekStart = now.AddDays(-60).Date.AddDays(-(((int)now.AddDays(-60).DayOfWeek + 6) % 7)).AddHours(12);
        var firstOfWeek = await AddScanAsync(endpoint, weekStart, true, "C:", (@"C:\", 1));
        var laterSameWeek = await AddScanAsync(endpoint, weekStart.AddDays(2), true, "C:", (@"C:\", 1));
        var sameWeekOtherVolume = await AddScanAsync(endpoint, weekStart.AddDays(2), true, "D:", (@"D:\", 1));
        var tooOld = await AddScanAsync(endpoint, now.AddDays(-420), true, "C:", (@"C:\", 1));

        await _fixture.Retention().RunAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var kept = await db.StorageScans.AsNoTracking().Where(s => s.EndpointId == endpoint.Id).Select(s => s.Id).ToListAsync();
        Assert.Equal(new[] { recent.Id, firstOfWeek.Id, sameWeekOtherVolume.Id }.Order(), kept.Order());
        Assert.DoesNotContain(laterSameWeek.Id, kept);
        Assert.DoesNotContain(tooOld.Id, kept);
    }
}
