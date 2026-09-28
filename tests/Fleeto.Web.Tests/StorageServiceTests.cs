using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Storage analysis on the endpoint page (0.6.0): the latest scan of each drive in tree order with its changes, the history of
/// one folder, and "Scan now" for admins and technicians on a managed endpoint with an agent that scans, once per 15 minutes
/// and audited. Another client's endpoint and an agent-only endpoint show nothing.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class StorageServiceTests
{
    private const long Gb = 1024L * 1024 * 1024;
    private readonly WebFixture _fixture;

    public StorageServiceTests(WebFixture fixture) => _fixture = fixture;

    private StorageService Service => _fixture.Services.GetRequiredService<StorageService>();

    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    private async Task<Endpoint> EndpointAsync(EndpointTier tier = EndpointTier.Managed, string agentVersion = "0.6.0")
    {
        var db = _fixture.Database;
        await db.LoadTestLicenseAsync(1000);
        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var endpoint = await db.CreateEndpointAsync(site, tier, "SRV-STORAGE", EndpointClass.Server);
        await using var context = db.DbFactory.CreateSystem();
        await context.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.AgentVersion, agentVersion));
        return endpoint;
    }

    private async Task AddScanAsync(Endpoint endpoint, DateTime receivedAt, string volume, params (string Path, long Gigabytes)[] folders)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.StorageScans.Add(new StorageScan
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, AgentScanId = Guid.NewGuid(), Volume = volume,
            Filesystem = "NTFS", TotalBytes = 1000 * Gb, FreeBytes = 400 * Gb, ReceivedAt = receivedAt, Method = StorageScanMethod.Mft,
            Complete = true, FoldersJson = StorageRules.SerializeFolders(folders.Select(f => new StorageFolderEntry(f.Path, f.Gigabytes * Gb, 1, 1))),
            FilesJson = StorageRules.SerializeFiles([new StorageFileEntry(volume + @"\big.vhdx", 50 * Gb, receivedAt)])
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_latest_scan_of_each_drive_is_shown_in_tree_order_with_its_changes()
    {
        var endpoint = await EndpointAsync();
        await AddScanAsync(endpoint, Now.AddDays(-8), "C:", (@"C:\", 500), (@"C:\Users", 200), (@"C:\Windows", 100));
        await AddScanAsync(endpoint, Now.AddDays(-1), "C:", (@"C:\", 580), (@"C:\Users", 270), (@"C:\Windows", 100));
        await AddScanAsync(endpoint, Now, "C:", (@"C:\", 600), (@"C:\Windows", 110), (@"C:\Users", 280), (@"C:\Users\Public", 150), (@"C:\Data", 5));
        await AddScanAsync(endpoint, Now, "D:", (@"D:\", 10));

        var view = (await Service.GetAsync(WebFixture.Technician(), endpoint.Id))!;

        Assert.True(view.Managed);
        Assert.True(view.CanScan);
        Assert.Equal(["C:", "D:"], view.Volumes.Select(v => v.Volume));
        var detail = view.Selected!;
        Assert.Equal("C:", detail.Summary.Volume);
        Assert.Equal([@"C:\", @"C:\Users", @"C:\Users\Public", @"C:\Windows", @"C:\Data"], detail.Folders.Select(f => f.Path));
        Assert.Equal([0, 1, 2, 1, 1], detail.Folders.Select(f => f.Depth));
        var users = detail.Folders.Single(f => f.Path == @"C:\Users");
        Assert.True(users.HasChildren);
        Assert.Equal("Users", users.Name);
        Assert.Equal(10 * Gb, users.ChangeSincePrevious);
        Assert.Equal(80 * Gb, users.ChangeWeek);
        Assert.Null(users.ChangeMonth);
        Assert.Null(detail.Folders.Single(f => f.Path == @"C:\Users\Public").ChangeSincePrevious);
        Assert.Equal(@"C:\big.vhdx", Assert.Single(detail.Files).Path);

        Assert.Equal("D:", (await Service.GetAsync(WebFixture.Technician(), endpoint.Id, "d:"))!.Selected!.Summary.Volume);

        var history = await Service.FolderHistoryAsync(WebFixture.Technician(), endpoint.Id, "C:", @"C:\Users");
        Assert.Equal([200 * Gb, 270 * Gb, 280 * Gb], history.Select(p => p.SizeBytes));
    }

    [Fact]
    public async Task Another_clients_endpoint_and_an_agent_only_endpoint_show_nothing()
    {
        var endpoint = await EndpointAsync();
        await AddScanAsync(endpoint, Now, "C:", (@"C:\Users\secret", 1));
        var otherClient = await _fixture.Database.CreateClientAsync();
        var outsider = WebFixture.CallerWith(new RestrictedClientScope([otherClient.Id]), FleetoRoles.Technician);

        Assert.Null(await Service.GetAsync(outsider, endpoint.Id));
        Assert.Empty(await Service.FolderHistoryAsync(outsider, endpoint.Id, "C:", @"C:\Users\secret"));
        Assert.False((await Service.RequestScanAsync(outsider, endpoint.Id)).Success);

        var agentOnly = await EndpointAsync(EndpointTier.AgentOnly);
        await AddScanAsync(agentOnly, Now, "C:", (@"C:\", 1));
        var view = (await Service.GetAsync(WebFixture.Technician(), agentOnly.Id))!;
        Assert.False(view.Managed);
        Assert.Empty(view.Volumes);
    }

    [Fact]
    public async Task Scan_now_is_for_admins_and_technicians_on_a_managed_endpoint_with_a_scanning_agent_once_per_15_minutes()
    {
        var endpoint = await EndpointAsync();
        var readOnly = WebFixture.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);
        Assert.False((await Service.RequestScanAsync(readOnly, endpoint.Id)).Success);
        Assert.False((await Service.GetAsync(readOnly, endpoint.Id))!.CanScan);

        var agentOnly = await EndpointAsync(EndpointTier.AgentOnly);
        Assert.Contains("managed", (await Service.RequestScanAsync(WebFixture.Technician(), agentOnly.Id)).Problem);
        var oldAgent = await EndpointAsync(agentVersion: "0.5.0");
        Assert.Contains(StorageRules.MinimumAgentVersion, (await Service.RequestScanAsync(WebFixture.Technician(), oldAgent.Id)).Problem);

        var first = await Service.RequestScanAsync(WebFixture.Technician(), endpoint.Id);
        Assert.True(first.Success);
        Assert.False((await Service.RequestScanAsync(WebFixture.Admin(), endpoint.Id)).Success);
        Assert.NotNull((await Service.GetAsync(WebFixture.Technician(), endpoint.Id))!.RequestedAt);

        _fixture.Database.Time.Advance(StorageRules.RequestGap + TimeSpan.FromSeconds(1));
        Assert.True((await Service.RequestScanAsync(WebFixture.Admin(), endpoint.Id)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var requests = await db.StorageScanRequests.AsNoTracking().Where(r => r.EndpointId == endpoint.Id).ToListAsync();
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r => Assert.Equal(StorageScanReason.Technician, r.Reason));
        Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.StorageScanRequested && a.TargetId == endpoint.Id.ToString()));
    }

    [Fact]
    public async Task A_policy_takes_only_an_offered_scan_interval()
    {
        var policies = _fixture.Services.GetRequiredService<PolicyService>();
        var input = new PolicyInput("Scans " + Guid.NewGuid().ToString("N")[..8], null, 30, 3600, 10, AlertSeverity.Critical, StorageScanIntervalHours: 5);

        Assert.False((await policies.CreateAsync(WebFixture.Admin(), null, input)).Success);
        var created = await policies.CreateAsync(WebFixture.Admin(), null, input with { StorageScanIntervalHours = 168 });
        Assert.True(created.Success);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(168, (await db.Policies.AsNoTracking().SingleAsync(p => p.Id == created.Value)).StorageScanIntervalHours);
    }
}
