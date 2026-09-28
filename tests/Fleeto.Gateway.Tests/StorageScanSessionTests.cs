using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Sessions;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using StorageScanRequest = Fleeto.Core.Entities.StorageScanRequest;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Storage analysis in the gateway (0.6.0): a scan report is stored once per scan id and acknowledged only after it is stored;
/// an agent-only endpoint's report is acknowledged and dropped (tier enforcement, layer 3), as is a flood over the daily limit;
/// and scan requests reach a live managed agent once, never an agent-only endpoint.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class StorageScanSessionTests
{
    private readonly GatewayFixture _fixture;

    public StorageScanSessionTests(GatewayFixture fixture) => _fixture = fixture;

    private static StorageScanReport Report(string? scanId = null, string? requestId = null)
    {
        var report = new StorageScanReport
        {
            ScanId = scanId ?? Guid.NewGuid().ToString("D"),
            Volume = "C:",
            Filesystem = "NTFS",
            TotalBytes = 500UL << 30,
            FreeBytes = 200UL << 30,
            StartedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            DurationMs = 2500,
            Method = Protocol.Agent.V1.StorageScanMethod.Mft,
            Complete = true,
            FileCount = 1000,
            FolderCount = 100,
            RequestId = requestId ?? string.Empty
        };
        report.Folders.Add(new StorageFolder { Path = @"C:\", SizeBytes = 300UL << 30, FileCount = 1000, FolderCount = 100 });
        report.Folders.Add(new StorageFolder { Path = "C:\\Users\0", SizeBytes = 100UL << 30, FileCount = 500, FolderCount = 50 });
        report.Files.Add(new StorageFile { Path = @"C:\pagefile.sys", SizeBytes = 16UL << 30, ModifiedAt = Timestamp.FromDateTime(DateTime.UtcNow) });
        return report;
    }

    private static List<ServerMessage> Acks(AgentSession session) =>
        GatewayHarness.Drain(session).Where(m => m.BodyCase == ServerMessage.BodyOneofCase.StorageScanAck).ToList();

    private async Task<List<StorageScan>> ScansAsync(Guid endpointId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.StorageScans.AsNoTracking().Where(s => s.EndpointId == endpointId).ToListAsync();
    }

    [Fact]
    public async Task A_report_is_stored_once_and_acknowledged_every_time()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        var report = Report();
        await harness.Manager.HandleAsync(session, new AgentMessage { StorageScan = report }, CancellationToken.None);
        await harness.Manager.HandleAsync(session, new AgentMessage { StorageScan = report }, CancellationToken.None);

        var acks = Acks(session);
        Assert.Equal(2, acks.Count);
        Assert.All(acks, a => Assert.Equal(report.ScanId, a.StorageScanAck.ScanId));
        var scan = Assert.Single(await ScansAsync(endpoint.Id));
        Assert.Equal("C:", scan.Volume);
        Assert.Equal(Core.Entities.StorageScanMethod.Mft, scan.Method);
        Assert.True(scan.Complete);
        Assert.Equal(endpoint.ClientId, scan.ClientId);
        var folders = StorageRules.ParseFolders(scan.FoldersJson);
        Assert.Equal([@"C:\", @"C:\Users"], folders.Select(f => f.Path));
        Assert.Equal(300L << 30, folders[0].SizeBytes);
        Assert.Equal(@"C:\pagefile.sys", Assert.Single(StorageRules.ParseFiles(scan.FilesJson)).Path);
        Assert.Equal(_fixture.Database.Time.GetUtcNow().UtcDateTime, scan.ReceivedAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_report_of_an_agent_only_endpoint_is_acknowledged_and_not_stored()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        await harness.Manager.HandleAsync(session, new AgentMessage { StorageScan = Report() }, CancellationToken.None);

        Assert.Single(Acks(session));
        Assert.Empty(await ScansAsync(endpoint.Id));
    }

    [Fact]
    public async Task A_report_without_a_scan_id_closes_the_session()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);

        await harness.Manager.HandleAsync(session, new AgentMessage { StorageScan = Report(scanId: "not-a-uuid") }, CancellationToken.None);

        Assert.True(session.IsClosing);
        Assert.Empty(await ScansAsync(endpoint.Id));
    }

    [Fact]
    public async Task Too_many_reports_in_a_day_are_acknowledged_and_dropped()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        for (var i = 0; i < StorageRules.MaxScansPerEndpointPerDay + 3; i++)
        {
            await harness.Manager.HandleAsync(session, new AgentMessage { StorageScan = Report() }, CancellationToken.None);
        }

        Assert.Equal(StorageRules.MaxScansPerEndpointPerDay + 3, Acks(session).Count);
        Assert.Equal(StorageRules.MaxScansPerEndpointPerDay, (await ScansAsync(endpoint.Id)).Count);
    }

    private async Task<StorageScanRequest> AddRequestAsync(Endpoint endpoint, TimeSpan? lifetime = null, TimeSpan? age = null)
    {
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var request = new StorageScanRequest
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Reason = StorageScanReason.Technician,
            RequestedByUserId = Guid.NewGuid(), RequestedByName = "Tech", RequestedAt = now - (age ?? TimeSpan.Zero),
            ExpiresAt = now + (lifetime ?? TimeSpan.FromHours(1))
        };
        db.StorageScanRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private static List<Protocol.Agent.V1.StorageScanRequest> RequestMessages(AgentSession session) =>
        GatewayHarness.Drain(session).Where(m => m.BodyCase == ServerMessage.BodyOneofCase.StorageScanRequest).Select(m => m.StorageScanRequest).ToList();

    [Fact]
    public async Task Pending_requests_reach_a_live_managed_agent_as_one_message_once()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        var older = await AddRequestAsync(endpoint, age: TimeSpan.FromMinutes(1));
        var newer = await AddRequestAsync(endpoint);
        await _fixture.Database.Bus.PublishAsync(NotificationChannels.StorageScanRequests, newer.Id.ToString());
        await harness.Manager.DeliverStorageScanRequestsAsync([endpoint.Id], CancellationToken.None);

        Assert.Equal(older.Id.ToString("D"), Assert.Single(RequestMessages(session)).RequestId);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.All(await db.StorageScanRequests.AsNoTracking().Where(r => r.EndpointId == endpoint.Id).ToListAsync(), r => Assert.NotNull(r.DeliveredAt));
    }

    [Fact]
    public async Task A_pending_request_is_delivered_when_the_agent_connects_and_an_expired_one_never()
    {
        using var harness = _fixture.CreateHarness();
        var pendingEndpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var request = await AddRequestAsync(pendingEndpoint);
        using (var session = await harness.OpenAsync(pendingEndpoint))
        {
            Assert.Equal(request.Id.ToString("D"), Assert.Single(RequestMessages(session)).RequestId);
        }

        var expiredEndpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        await AddRequestAsync(expiredEndpoint, lifetime: TimeSpan.FromSeconds(1));
        _fixture.Database.Time.Advance(TimeSpan.FromSeconds(5));
        using var expiredSession = await harness.OpenAsync(expiredEndpoint);
        Assert.Empty(RequestMessages(expiredSession));
    }

    [Fact]
    public async Task An_agent_only_endpoint_never_receives_a_request()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        var request = await AddRequestAsync(endpoint);
        using var session = await harness.OpenAsync(endpoint);
        await harness.Manager.DeliverStorageScanRequestsAsync([endpoint.Id], CancellationToken.None);

        Assert.Empty(RequestMessages(session));
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Null((await db.StorageScanRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).DeliveredAt);
    }
}
