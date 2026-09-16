using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Data;
using Fleeto.Gateway.Remote;
using Fleeto.Gateway.Sessions;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Testing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Endpoint = Fleeto.Core.Entities.Endpoint;
using RemoteSessionKind = Fleeto.Core.Entities.RemoteSessionKind;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Guarantees of the remote session relay (0.3.0): a valid token pairs the browser with the watchdog of its endpoint, the endpoint's signed
/// key reaches the browser and every later message passes both ways unchanged; the join and the end are recorded and audited. A token opens
/// the relay once; a forged token, a token for another participant or instance, an agent-only endpoint and a missing watchdog are refused
/// and recorded; only the certificate of that endpoint's watchdog can connect the other side; a refusal by the endpoint reaches the browser;
/// revoking the certificate or switching the endpoint to agent-only ends the relay; only the instance's own web origin may open one.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class RemoteRelayTests
{
    private static readonly (byte[] Private, byte[] Public) SigningKey = Ed25519.GenerateKeyPair();
    private const string KeyId = "relaytestkey0001";

    private readonly GatewayFixture _fixture;

    public RemoteRelayTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class RelayScope : IAsyncDisposable
    {
        public required GatewayHarness Harness { get; init; }
        public required RemoteRelay Relay { get; init; }
        public required Endpoint Endpoint { get; init; }
        public required AgentCredential Watchdog { get; init; }
        public required AgentSession Control { get; init; }
        public required RelayTrust Trust { get; init; }

        public AgentIdentity WatchdogIdentity => Watchdog.Identity(Endpoint.Id, AgentComponent.Watchdog);

        public ValueTask DisposeAsync()
        {
            Relay.Dispose();
            Harness.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private async Task<RelayScope> ScopeAsync(EndpointTier tier = EndpointTier.Managed)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(tier);
        var watchdog = await _fixture.IssueAsync(endpoint, role: AgentComponent.Watchdog);
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
        var control = harness.NewSession(watchdog.Identity(endpoint.Id, AgentComponent.Watchdog));
        Assert.True(await harness.Manager.OpenAsync(control, new Hello { AgentVersion = "0.3.0", Hostname = "SRV-01", Component = Component.Watchdog },
            CancellationToken.None));
        var relay = new RemoteRelay(harness.Store, harness.Manager, harness.AllowList,
            new ProxyProtocolMiddleware(Microsoft.Extensions.Options.Options.Create(harness.Options), NullLogger<ProxyProtocolMiddleware>.Instance),
            _fixture.Database.Bus, _fixture.Database.Time, Microsoft.Extensions.Options.Options.Create(harness.Options), NullLogger<RemoteRelay>.Instance);
        var trust = new RelayTrust(_fixture.Database.InstanceId, "https://" + TestDatabase.Fqdn,
            new Dictionary<string, byte[]> { [KeyId] = SigningKey.Public });
        return new RelayScope { Harness = harness, Relay = relay, Endpoint = endpoint, Watchdog = watchdog, Control = control, Trust = trust };
    }

    private sealed record SignedParticipant(Guid ParticipantId, Guid SessionId, byte[] Payload, byte[] Signature);

    private async Task<SignedParticipant> SignedParticipantAsync(Endpoint endpoint, Action<RemoteSessionToken>? edit = null, byte[]? storedPayload = null,
        byte[]? signingKey = null)
    {
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var user = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, displayName: "Tess Tech");
        var session = new RemoteSession
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = RemoteSessionKind.RemoteBackground,
            Component = AgentComponent.Watchdog, StartedByUserId = user.Id, StartedByName = "Tess Tech", CreatedAt = now
        };
        var participantId = Guid.NewGuid();
        var token = new RemoteSessionToken
        {
            ParticipantId = participantId.ToString("D"), SessionId = session.Id.ToString("D"), InstanceId = _fixture.Database.InstanceId.ToString("D"),
            EndpointId = endpoint.Id.ToString("D"), Kind = Protocol.Agent.V1.RemoteSessionKind.RemoteBackground, Component = Component.Watchdog,
            TechnicianId = user.Id.ToString("D"), TechnicianName = "Tess Tech", BrowserPublicKey = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            IssuedAt = Timestamp.FromDateTime(now), ValidUntil = Timestamp.FromDateTime(now.AddSeconds(60)), IdleTimeoutSeconds = 1800
        };
        edit?.Invoke(token);
        var payload = token.ToByteArray();
        var signature = Ed25519.Sign(signingKey ?? SigningKey.Private, SignatureContexts.RemoteSession, payload);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.RemoteSessions.Add(session);
        db.RemoteSessionParticipants.Add(new RemoteSessionParticipant
        {
            Id = participantId, SessionId = session.Id, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, UserId = user.Id, UserName = "Tess Tech",
            BrowserPublicKey = token.BrowserPublicKey.ToByteArray(), State = RemoteParticipantState.Signed, TokenPayload = storedPayload ?? payload,
            TokenSignature = signature, SigningKeyId = KeyId, SignedAt = now, ValidUntil = now.AddSeconds(60), CreatedAt = now
        });
        await db.SaveChangesAsync();
        return new SignedParticipant(participantId, session.Id, payload, signature);
    }

    /// <summary>A browser connected to the relay over an in-memory WebSocket; the relay runs until it returns.</summary>
    private sealed class BrowserClient
    {
        public required WebSocket Socket { get; init; }
        public required Task Relay { get; init; }

        public Task SendHelloAsync(SignedParticipant participant, string keyId = KeyId) =>
            Socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                token = Convert.ToBase64String(participant.Payload), signature = Convert.ToBase64String(participant.Signature), keyId
            })), WebSocketMessageType.Text, true, CancellationToken.None);

        public async Task<JsonElement> ReadTextAsync()
        {
            var (type, data) = await ReadAsync(Socket);
            Assert.Equal(WebSocketMessageType.Text, type);
            return JsonDocument.Parse(data).RootElement;
        }
    }

    private static BrowserClient StartBrowser(RelayScope scope, Guid participantId)
    {
        var (server, client) = SocketPair();
        return new BrowserClient
        {
            Socket = client,
            Relay = Task.Run(() => scope.Relay.RunBrowserAsync(server, participantId, scope.Trust, "198.51.100.7", CancellationToken.None))
        };
    }

    private static async Task<WebSocket> ConnectEndpointAsync(RelayScope scope, Guid participantId, AgentIdentity? identity = null)
    {
        var (server, client) = SocketPair();
        _ = Task.Run(() => scope.Relay.RunEndpointAsync(server, identity ?? scope.WatchdogIdentity, participantId, CancellationToken.None));
        var hello = new RelayEndpointHello
        {
            ParticipantId = participantId.ToString("D"), EndpointPublicKey = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
            Signature = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(64)),
            CertificatePublicKey = ByteString.CopyFrom(scope.Watchdog.Key.ExportSubjectPublicKeyInfo())
        };
        await client.SendAsync(hello.ToByteArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
        return client;
    }

    private static async Task WaitForOfferAsync(RelayScope scope, SignedParticipant participant)
    {
        var offer = await GatewayHarness.ReadUntilAsync(scope.Control, ServerMessage.BodyOneofCase.RemoteSessionOffer);
        Assert.Equal(participant.Payload, offer.RemoteSessionOffer.Session.Payload.ToByteArray());
        Assert.Equal(participant.Signature, offer.RemoteSessionOffer.Session.Signature.ToByteArray());
    }

    private async Task<RemoteSessionParticipant> ReadParticipantAsync(Guid id)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task A_valid_token_pairs_browser_and_watchdog_and_frames_pass_unchanged()
    {
        await using var scope = await ScopeAsync();
        var participant = await SignedParticipantAsync(scope.Endpoint);
        var browser = StartBrowser(scope, participant.ParticipantId);
        await browser.SendHelloAsync(participant);
        await WaitForOfferAsync(scope, participant);

        var endpoint = await ConnectEndpointAsync(scope, participant.ParticipantId);
        var ready = await browser.ReadTextAsync();
        Assert.Equal("ready", ready.GetProperty("type").GetString());
        Assert.Equal(88, ready.GetProperty("signature").GetString()!.Length);
        Assert.Equal(scope.Watchdog.Key.ExportSubjectPublicKeyInfo(), Convert.FromBase64String(ready.GetProperty("certificatePublicKey").GetString()!));

        var toEndpoint = RandomNumberGenerator.GetBytes(70_000);
        await browser.Socket.SendAsync(toEndpoint, WebSocketMessageType.Binary, true, CancellationToken.None);
        var (_, received) = await ReadAsync(endpoint);
        Assert.Equal(toEndpoint, received);
        var toBrowser = RandomNumberGenerator.GetBytes(300);
        await endpoint.SendAsync(toBrowser, WebSocketMessageType.Binary, true, CancellationToken.None);
        Assert.Equal(toBrowser, (await ReadAsync(browser.Socket)).Data);

        var connected = await ReadParticipantAsync(participant.ParticipantId);
        Assert.Equal(RemoteParticipantState.Connected, connected.State);
        Assert.Equal("198.51.100.7", connected.IpAddress);

        await browser.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        await browser.Relay.WaitAsync(TimeSpan.FromSeconds(10));
        var ended = await ReadParticipantAsync(participant.ParticipantId);
        Assert.Equal(RemoteParticipantState.Ended, ended.State);
        Assert.Equal("The technician closed the session.", ended.EndReason);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var session = await db.RemoteSessions.AsNoTracking().SingleAsync(s => s.Id == participant.SessionId);
        Assert.NotNull(session.StartedAt);
        Assert.NotNull(session.EndedAt);
        var actions = await db.AuditEntries.AsNoTracking().Where(a => a.TargetId == participant.SessionId.ToString()).Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.RemoteSessionJoined, actions);
        Assert.Contains(AuditActions.RemoteSessionLeft, actions);
    }

    [Fact]
    public async Task A_token_opens_the_relay_only_once()
    {
        await using var scope = await ScopeAsync();
        var participant = await SignedParticipantAsync(scope.Endpoint);
        var first = StartBrowser(scope, participant.ParticipantId);
        await first.SendHelloAsync(participant);
        await WaitForOfferAsync(scope, participant);

        var second = StartBrowser(scope, participant.ParticipantId);
        await second.SendHelloAsync(participant);
        var refused = await second.ReadTextAsync();
        Assert.Equal("error", refused.GetProperty("type").GetString());
        Assert.Contains("used before", refused.GetProperty("message").GetString());
        await second.Relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(GatewayHarness.Drain(scope.Control), m => m.BodyCase == ServerMessage.BodyOneofCase.RemoteSessionOffer);
    }

    [Fact]
    public async Task Forged_and_foreign_tokens_are_refused_without_touching_the_participant()
    {
        await using var scope = await ScopeAsync();
        var otherKey = Ed25519.GenerateKeyPair();

        var forged = await SignedParticipantAsync(scope.Endpoint, signingKey: otherKey.PrivateKey);
        var anotherInstance = await SignedParticipantAsync(scope.Endpoint, token => token.InstanceId = Guid.NewGuid().ToString("D"));
        var valid = await SignedParticipantAsync(scope.Endpoint);
        var cases = new (SignedParticipant Participant, Guid Path)[]
        {
            (forged, forged.ParticipantId),
            (anotherInstance, anotherInstance.ParticipantId),
            // A valid token presented for another participant's path.
            (valid, forged.ParticipantId)
        };
        foreach (var (participant, path) in cases)
        {
            var browser = StartBrowser(scope, path);
            await browser.SendHelloAsync(participant);
            var refused = await browser.ReadTextAsync();
            Assert.Equal("error", refused.GetProperty("type").GetString());
            await browser.Relay.WaitAsync(TimeSpan.FromSeconds(10));
        }

        Assert.Equal(RemoteParticipantState.Signed, (await ReadParticipantAsync(forged.ParticipantId)).State);
        Assert.Equal(RemoteParticipantState.Signed, (await ReadParticipantAsync(valid.ParticipantId)).State);

        // A signed token that is not the token stored for the participant (a replay of an older one) cannot claim it either.
        var mismatch = await SignedParticipantAsync(scope.Endpoint, storedPayload: RandomNumberGenerator.GetBytes(40));
        var mismatched = StartBrowser(scope, mismatch.ParticipantId);
        await mismatched.SendHelloAsync(mismatch);
        Assert.Equal("error", (await mismatched.ReadTextAsync()).GetProperty("type").GetString());
        Assert.DoesNotContain(GatewayHarness.Drain(scope.Control), m => m.BodyCase == ServerMessage.BodyOneofCase.RemoteSessionOffer);
    }

    [Fact]
    public async Task An_agent_only_endpoint_gets_no_offer_and_the_refusal_is_recorded()
    {
        await using var scope = await ScopeAsync(EndpointTier.AgentOnly);
        var participant = await SignedParticipantAsync(scope.Endpoint);
        var browser = StartBrowser(scope, participant.ParticipantId);
        await browser.SendHelloAsync(participant);

        var refused = await browser.ReadTextAsync();
        Assert.Contains("not managed", refused.GetProperty("message").GetString());
        await browser.Relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.DoesNotContain(GatewayHarness.Drain(scope.Control), m => m.BodyCase == ServerMessage.BodyOneofCase.RemoteSessionOffer);
        var stored = await ReadParticipantAsync(participant.ParticipantId);
        Assert.Equal(RemoteParticipantState.Refused, stored.State);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.RemoteSessionRefused && a.TargetId == participant.SessionId.ToString()));
    }

    [Fact]
    public async Task Only_the_watchdog_of_that_endpoint_can_connect_and_its_refusal_reaches_the_browser()
    {
        await using var scope = await ScopeAsync();
        var participant = await SignedParticipantAsync(scope.Endpoint);
        var browser = StartBrowser(scope, participant.ParticipantId);
        await browser.SendHelloAsync(participant);
        await WaitForOfferAsync(scope, participant);

        var otherEndpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var otherWatchdog = await _fixture.IssueAsync(otherEndpoint, role: AgentComponent.Watchdog);
        var agent = await _fixture.IssueAsync(scope.Endpoint);
        Assert.False(scope.Relay.IsWaitingFor(participant.ParticipantId, otherWatchdog.Identity(otherEndpoint.Id, AgentComponent.Watchdog)));
        Assert.False(scope.Relay.IsWaitingFor(participant.ParticipantId, agent.Identity(scope.Endpoint.Id)));
        Assert.True(scope.Relay.IsWaitingFor(participant.ParticipantId, scope.WatchdogIdentity));

        await scope.Harness.Manager.HandleAsync(scope.Control, new AgentMessage
        {
            RemoteSessionRefused = new RemoteSessionRefused { ParticipantId = participant.ParticipantId.ToString("D"), Error = "The endpoint refused the session: agent-only." }
        }, CancellationToken.None);
        var refused = await browser.ReadTextAsync();
        Assert.Equal("The endpoint refused the session: agent-only.", refused.GetProperty("message").GetString());
        await browser.Relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(RemoteParticipantState.Refused, (await ReadParticipantAsync(participant.ParticipantId)).State);
    }

    [Fact]
    public async Task Revoking_the_certificate_or_leaving_managed_ends_the_relay()
    {
        await using var scope = await ScopeAsync();
        var participant = await SignedParticipantAsync(scope.Endpoint);
        var browser = StartBrowser(scope, participant.ParticipantId);
        await browser.SendHelloAsync(participant);
        await WaitForOfferAsync(scope, participant);
        await ConnectEndpointAsync(scope, participant.ParticipantId);
        Assert.Equal("ready", (await browser.ReadTextAsync()).GetProperty("type").GetString());

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.AgentCertificates.Where(c => c.EndpointId == scope.Endpoint.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(c => c.RevokedAt, DateTime.UtcNow));
        }

        await scope.Harness.AllowList.ReloadAsync(CancellationToken.None, scope.Endpoint.Id);
        await browser.Relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("The endpoint certificate was revoked or the endpoint was deleted.", (await ReadParticipantAsync(participant.ParticipantId)).EndReason);

        // A second session on a managed endpoint ends when the endpoint becomes agent-only.
        await using var managed = await ScopeAsync();
        var second = await SignedParticipantAsync(managed.Endpoint);
        var secondBrowser = StartBrowser(managed, second.ParticipantId);
        await secondBrowser.SendHelloAsync(second);
        await WaitForOfferAsync(managed, second);
        await ConnectEndpointAsync(managed, second.ParticipantId);
        Assert.Equal("ready", (await secondBrowser.ReadTextAsync()).GetProperty("type").GetString());
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == managed.Endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Tier, EndpointTier.AgentOnly));
        }

        await managed.Relay.CloseUnmanagedAsync([managed.Endpoint.Id], CancellationToken.None);
        await secondBrowser.Relay.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("The endpoint is no longer managed.", (await ReadParticipantAsync(second.ParticipantId)).EndReason);
    }

    [Theory]
    [InlineData("https://rmm.test.example", "https://rmm.test.example", true)]
    [InlineData("https://RMM.test.example", "https://rmm.test.example/", true)]
    [InlineData("https://localhost:7100", "https://localhost:7100", true)]
    [InlineData("https://evil.example", "https://rmm.test.example", false)]
    [InlineData("http://rmm.test.example", "https://rmm.test.example", false)]
    [InlineData("https://rmm.test.example:8443", "https://rmm.test.example", false)]
    [InlineData("", "https://rmm.test.example", false)]
    [InlineData("null", "https://rmm.test.example", false)]
    public void Only_the_web_origin_of_the_instance_may_open_a_relay(string origin, string webBaseUrl, bool allowed)
    {
        Assert.Equal(allowed, RemoteRelay.OriginAllowed(origin, webBaseUrl));
    }

    // ---------------------------------------------------------------------------------------------------------------

    private static async Task<(WebSocketMessageType Type, byte[] Data)> ReadAsync(WebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var buffer = new byte[128 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, timeout.Token);
            Assert.NotEqual(WebSocketMessageType.Close, result.MessageType);
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return (result.MessageType, message.ToArray());
            }
        }
    }

    private static (WebSocket Server, WebSocket Client) SocketPair()
    {
        var toServer = new Pipe();
        var toClient = new Pipe();
        var server = WebSocket.CreateFromStream(new DuplexStream(toServer.Reader, toClient.Writer), new WebSocketCreationOptions { IsServer = true });
        var client = WebSocket.CreateFromStream(new DuplexStream(toClient.Reader, toServer.Writer), new WebSocketCreationOptions { IsServer = false });
        return (server, client);
    }

    private sealed class DuplexStream(PipeReader reader, PipeWriter writer) : Stream
    {
        private readonly Stream _read = reader.AsStream();
        private readonly Stream _write = writer.AsStream();

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _write.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => _write.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => _read.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => _read.ReadAsync(buffer, cancellationToken);
        public override void Write(byte[] buffer, int offset, int count) => _write.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) => _write.WriteAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                writer.Complete();
                reader.Complete();
            }

            base.Dispose(disposing);
        }
    }
}
