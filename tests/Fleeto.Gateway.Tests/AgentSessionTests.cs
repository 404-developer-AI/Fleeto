using System.Security.Cryptography;
using Fleeto.Core.Entities;
using Fleeto.Gateway.Tls;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using ProtoTier = Fleeto.Protocol.Agent.V1.Tier;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Guarantees the session rules of the gateway: batches are stored once and acknowledged every time, agent-only endpoints
/// store no results and never receive a managed configuration (tier enforcement, layer 3), renewals must keep the
/// connection key, a live identity cannot be cloned while a dead connection is replaced, and revocation closes a live
/// session at once.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class AgentSessionTests
{
    private readonly GatewayFixture _fixture;

    public AgentSessionTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Hello_marks_the_endpoint_online_and_closing_marks_it_offline()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();

        using var session = await harness.OpenAsync(endpoint);
        var ack = await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.HelloAck);
        Assert.Equal(endpoint.Id.ToString("D"), ack.HelloAck.EndpointId);
        await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.InventoryRequest);
        Assert.True((await ReadEndpointAsync(endpoint.Id)).IsOnline);

        await harness.Manager.CloseAsync(session);

        var stored = await ReadEndpointAsync(endpoint.Id);
        Assert.False(stored.IsOnline);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var kinds = await db.EndpointEvents.Where(e => e.EndpointId == endpoint.Id).OrderBy(e => e.Id).Select(e => e.Kind).ToListAsync();
        Assert.Equal([EndpointEventKind.Connected, EndpointEventKind.Disconnected], kinds);
    }

    [Fact]
    public async Task Batch_ingest_is_idempotent()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        var batch = Batch(sequence: 7, results: 3);
        await harness.Manager.HandleAsync(session, new AgentMessage { CheckResults = batch }, CancellationToken.None);
        await harness.Manager.HandleAsync(session, new AgentMessage { CheckResults = batch }, CancellationToken.None);

        var acks = GatewayHarness.Drain(session).Where(m => m.BodyCase == ServerMessage.BodyOneofCase.BatchAck).ToList();
        Assert.Equal(2, acks.Count);
        Assert.All(acks, a => Assert.Equal(7UL, a.BatchAck.Sequence));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(3, await db.CheckResults.CountAsync(r => r.EndpointId == endpoint.Id));
        Assert.Equal(1, await db.IngestBatches.CountAsync(b => b.EndpointId == endpoint.Id));
        var result = await db.CheckResults.FirstAsync(r => r.EndpointId == endpoint.Id);
        Assert.Equal(endpoint.ClientId, result.ClientId);
        Assert.Contains(endpoint.Id.ToString(), _fixture.Database.Bus.PayloadsFor(Core.Interfaces.NotificationChannels.CheckResults));
    }

    [Fact]
    public async Task Results_of_an_agent_only_endpoint_are_acknowledged_but_not_stored()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        using var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        await harness.Manager.HandleAsync(session, new AgentMessage { CheckResults = Batch(sequence: 1, results: 5) }, CancellationToken.None);

        var ack = await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.BatchAck);
        Assert.Equal(1UL, ack.BatchAck.Sequence);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(0, await db.CheckResults.CountAsync(r => r.EndpointId == endpoint.Id));
        Assert.Equal(0, await db.IngestBatches.CountAsync(b => b.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task Agent_only_endpoint_never_receives_a_managed_configuration()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.AgentOnly);
        await StoreConfigAsync(endpoint, version: 3, ProtoTier.Managed);

        // Neither at Hello ...
        using var session = await harness.OpenAsync(endpoint, configVersion: 0);
        // ... nor on a notification ...
        await _fixture.Database.Bus.PublishAsync(Core.Interfaces.NotificationChannels.EndpointConfig, endpoint.Id.ToString());
        // ... nor on a catch-up.
        await harness.Manager.CatchUpConfigsAsync(CancellationToken.None);

        Assert.DoesNotContain(GatewayHarness.Drain(session), m => m.BodyCase == ServerMessage.BodyOneofCase.Config);
    }

    [Fact]
    public async Task Managed_endpoint_receives_a_newer_configuration_once()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        await StoreConfigAsync(endpoint, version: 2, ProtoTier.Managed);

        // Delivered at Hello ...
        using var session = await harness.OpenAsync(endpoint, configVersion: 1);
        Assert.Contains(GatewayHarness.Drain(session), m => m.BodyCase == ServerMessage.BodyOneofCase.Config);

        // ... and not again for the same version.
        await _fixture.Database.Bus.PublishAsync(Core.Interfaces.NotificationChannels.EndpointConfig, endpoint.Id.ToString());
        Assert.DoesNotContain(GatewayHarness.Drain(session), m => m.BodyCase == ServerMessage.BodyOneofCase.Config);

        await harness.Manager.CloseAsync(session);
        await StoreConfigAsync(endpoint, version: 2, ProtoTier.Managed, replace: true);
        using var next = await harness.OpenAsync(endpoint, configVersion: 1);
        var configs = GatewayHarness.Drain(next).Where(m => m.BodyCase == ServerMessage.BodyOneofCase.Config).ToList();
        var config = Assert.Single(configs);
        Assert.Equal(2UL, AgentConfig.Parser.ParseFrom(config.Config.Payload).Version);
    }

    [Fact]
    public async Task Renewal_with_a_different_key_is_refused_without_a_signing_request()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint);
        using var session = harness.NewSession(credential.Identity(endpoint.Id));
        Assert.True(await harness.Manager.OpenAsync(session, GatewayHarness.Hello(), CancellationToken.None));

        using var otherKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var foreignCsr = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=agent", otherKey, HashAlgorithmName.SHA256)
            .CreateSigningRequest();
        await harness.Manager.HandleAsync(session, new AgentMessage
        {
            RenewCertificate = new RenewCertificateRequest { CsrDer = ByteString.CopyFrom(foreignCsr) }
        }, CancellationToken.None);

        var response = await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.RenewCertificate);
        Assert.True(response.RenewCertificate.CertificateDer.IsEmpty);
        Assert.Contains("same key", response.RenewCertificate.Error);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await db.SigningRequests.AnyAsync(r => r.SubjectId == endpoint.Id));
    }

    [Fact]
    public async Task Renewal_with_the_connection_key_returns_the_new_certificate_and_allows_it()
    {
        await using var signer = new FakeSigner(_fixture);
        using var harness = _fixture.CreateHarness(o => o.SigningTimeoutSeconds = 10);
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint);
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
        using var session = harness.NewSession(credential.Identity(endpoint.Id));
        Assert.True(await harness.Manager.OpenAsync(session, GatewayHarness.Hello(), CancellationToken.None));

        var response = await harness.Manager.RenewAsync(session, credential.Csr(), CancellationToken.None);

        Assert.Equal(string.Empty, response.RenewCertificate.Error);
        using var renewed = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(
            response.RenewCertificate.CertificateDer.ToByteArray());
        Assert.Equal(AllowListDecision.Accepted, harness.AllowList.Authorize(renewed, out _));
    }

    [Fact]
    public async Task Second_connection_is_refused_while_the_live_session_answers_its_ping()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint);
        using var live = harness.NewSession(credential.Identity(endpoint.Id), "192.0.2.1");
        Assert.True(await harness.Manager.OpenAsync(live, GatewayHarness.Hello(), CancellationToken.None));

        // The live agent answers pings.
        var responder = Task.Run(async () =>
        {
            var ping = await GatewayHarness.ReadUntilAsync(live, ServerMessage.BodyOneofCase.Ping);
            await harness.Manager.HandleAsync(live, new AgentMessage { Pong = new Pong { Nonce = ping.Ping.Nonce } }, CancellationToken.None);
        });

        using var clone = harness.NewSession(credential.Identity(endpoint.Id), "203.0.113.9");
        Assert.False(await harness.Manager.OpenAsync(clone, GatewayHarness.Hello(), CancellationToken.None));
        await responder;

        var disconnect = await GatewayHarness.ReadUntilAsync(clone, ServerMessage.BodyOneofCase.Disconnect);
        Assert.Equal(DisconnectCode.DuplicateIdentity, disconnect.Disconnect.Code);
        Assert.False(live.IsClosing);
        Assert.True(harness.Manager.TryGetSession(endpoint.Id, out var registered));
        Assert.Same(live, registered);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var duplicate = await db.EndpointEvents.SingleAsync(e => e.EndpointId == endpoint.Id && e.Kind == EndpointEventKind.DuplicateIdentity);
        Assert.Contains("203.0.113.9", duplicate.Detail);
        Assert.Contains("192.0.2.1", duplicate.Detail);
    }

    [Fact]
    public async Task Silent_previous_session_is_replaced_by_the_new_connection()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint);
        using var dropped = harness.NewSession(credential.Identity(endpoint.Id), "192.0.2.1");
        Assert.True(await harness.Manager.OpenAsync(dropped, GatewayHarness.Hello(), CancellationToken.None));

        using var reconnect = harness.NewSession(credential.Identity(endpoint.Id), "192.0.2.1");
        Assert.True(await harness.Manager.OpenAsync(reconnect, GatewayHarness.Hello(), CancellationToken.None));

        Assert.True(dropped.IsClosing);
        Assert.True(harness.Manager.TryGetSession(endpoint.Id, out var registered));
        Assert.Same(reconnect, registered);

        // The replaced session closing later must not mark the endpoint offline.
        await harness.Manager.CloseAsync(dropped);
        Assert.True((await ReadEndpointAsync(endpoint.Id)).IsOnline);
    }

    [Fact]
    public async Task Revocation_closes_the_live_session_at_once()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var credential = await _fixture.IssueAsync(endpoint);
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
        using var session = harness.NewSession(credential.Identity(endpoint.Id));
        Assert.True(await harness.Manager.OpenAsync(session, GatewayHarness.Hello(), CancellationToken.None));

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.AgentCertificates.Where(c => c.EndpointId == endpoint.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, DateTime.UtcNow));
        }

        await harness.AllowList.ReloadAsync(CancellationToken.None, endpoint.Id);

        var disconnect = await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.Disconnect);
        Assert.Equal(DisconnectCode.Revoked, disconnect.Disconnect.Code);
    }

    [Fact]
    public async Task Inventory_is_stored_with_camel_case_json_and_updates_the_endpoint()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        using var session = await harness.OpenAsync(endpoint);

        var inventory = new Inventory
        {
            Hostname = "SRV-NEW\0",
            Os = new OsInfo { Platform = "windows", Name = "Windows Server 2022 Standard", Version = "10.0.20348", IsServer = true, Architecture = "amd64" },
            Manufacturer = new string('M', 500),
            CpuCores = 8,
            MemoryTotalBytes = 16UL << 30,
            BootTime = Timestamp.FromDateTime(DateTime.UtcNow.AddDays(-2)),
            Disks = { new Disk { Mount = "C:", Filesystem = "NTFS", TotalBytes = 100, FreeBytes = 40 } },
            NetworkInterfaces = { new NetworkInterface { Name = "Ethernet", MacAddress = "00:11:22:33:44:55", IpAddresses = { "192.0.2.5" } } },
            Software = { new SoftwareItem { Name = "Fleeto agent", Version = "0.1.0", Publisher = "Steaan", InstallDate = "20260914" } },
            Services = { new ServiceItem { Name = "Spooler", DisplayName = "Print Spooler", StartType = "automatic", State = "running" } },
            Action1AgentId = "ef17c844-5b7c-4b32-9724-f2716b596639"
        };
        await harness.Manager.HandleAsync(session, new AgentMessage { Inventory = new InventoryReport { Hash = "abc", Inventory = inventory } },
            CancellationToken.None);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var snapshot = await db.InventorySnapshots.SingleAsync(i => i.EndpointId == endpoint.Id);
        Assert.Equal("abc", snapshot.Hash);
        Assert.Equal(200, snapshot.Manufacturer.Length);
        Assert.Contains("\"freeBytes\"", snapshot.DisksJson);
        Assert.Contains("\"macAddress\"", snapshot.NetworkInterfacesJson);
        Assert.Contains("\"installDate\"", snapshot.SoftwareJson);
        Assert.Contains("Print Spooler", snapshot.ServicesJson);
        Assert.Contains("\"startType\"", snapshot.ServicesJson);
        // The Action1 agent of the endpoint (0.4.0): how patch state is matched to this endpoint.
        Assert.Equal("ef17c844-5b7c-4b32-9724-f2716b596639", snapshot.Action1AgentId);
        var stored = await ReadEndpointAsync(endpoint.Id);
        Assert.Equal("SRV-NEW", stored.Hostname);
        Assert.Equal(EndpointClass.Server, stored.DetectedClass);

        // A reconnect with the same inventory hash is not asked for the inventory again.
        await harness.Manager.CloseAsync(session);
        using var reconnect = harness.NewSession(new AgentIdentity(endpoint.Id, "other", "key", DateTime.UtcNow.AddDays(1)));
        Assert.True(await harness.Manager.OpenAsync(reconnect, GatewayHarness.Hello(inventoryHash: "abc"), CancellationToken.None));
        Assert.DoesNotContain(GatewayHarness.Drain(reconnect), m => m.BodyCase == ServerMessage.BodyOneofCase.InventoryRequest);
    }

    private static CheckResultBatch Batch(ulong sequence, int results)
    {
        var batch = new CheckResultBatch { Sequence = sequence };
        for (var i = 0; i < results; i++)
        {
            batch.Results.Add(new Protocol.Agent.V1.CheckResult
            {
                CheckId = Guid.NewGuid().ToString(),
                ConfigVersion = 1,
                CollectedAt = Timestamp.FromDateTime(DateTime.UtcNow),
                Value = i * 1.5,
                Target = "C:",
                Detail = new string('d', 2000)
            });
        }

        return batch;
    }

    private async Task StoreConfigAsync(Endpoint endpoint, long version, ProtoTier tier, bool replace = false)
    {
        if (replace)
        {
            await using var cleanup = _fixture.Database.DbFactory.CreateSystem();
            await cleanup.EndpointConfigs.Where(c => c.EndpointId == endpoint.Id).ExecuteDeleteAsync();
        }

        var config = new AgentConfig
        {
            InstanceId = _fixture.Database.InstanceId.ToString("D"),
            EndpointId = endpoint.Id.ToString("D"),
            Version = (ulong)version,
            IssuedAt = Timestamp.FromDateTime(DateTime.UtcNow),
            Tier = tier,
            HeartbeatIntervalSeconds = 30,
            InventoryIntervalSeconds = 3600
        };
        if (tier == ProtoTier.Managed)
        {
            config.Checks.Add(new CheckSpec { Id = Guid.NewGuid().ToString(), Type = Protocol.Agent.V1.CheckType.CpuUsage, IntervalSeconds = 300 });
        }

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.EndpointConfigs.Add(new EndpointConfig
        {
            EndpointId = endpoint.Id,
            ClientId = endpoint.ClientId,
            Version = version,
            Payload = config.ToByteArray(),
            Signature = RandomNumberGenerator.GetBytes(64),
            KeyId = "test",
            ContentHash = "test",
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    private async Task<Endpoint> ReadEndpointAsync(Guid endpointId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpointId);
    }
}
