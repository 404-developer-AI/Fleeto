using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.WebSockets;
using System.Security.Cryptography;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Gateway.Data;
using Fleeto.Gateway.Remote;
using Fleeto.Gateway.Sessions;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Remote sessions under load (0.3.0 step 7): many sessions of many endpoints through one relay at the same time, each carrying screen
/// traffic (64 KiB frames to the browser, each acknowledged before the next, as remote control does). Every session pairs, every frame
/// arrives unchanged at its own browser and at no other, the relay keeps the limit of sessions per endpoint when they all arrive at once,
/// and every session ends recorded. The default size runs in CI; FLEETO_RELAY_LOAD_ENDPOINTS and FLEETO_RELAY_LOAD_FRAMES make it larger.
/// </summary>
public sealed partial class RemoteRelayTests
{
    private const int LoadFrameBytes = 64 * 1024;

    /// <summary>An extra endpoint with its watchdog connected to the harness of the scope.</summary>
    private sealed record LoadEndpoint(Endpoint Endpoint, AgentCredential Watchdog, AgentSession Control)
    {
        public AgentIdentity Identity => Watchdog.Identity(Endpoint.Id, AgentComponent.Watchdog);
    }

    private async Task<LoadEndpoint> AddEndpointAsync(RelayScope scope)
    {
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var watchdog = await _fixture.IssueAsync(endpoint, role: AgentComponent.Watchdog);
        Assert.True(await scope.Harness.AllowList.ReloadAsync(CancellationToken.None));
        var control = scope.Harness.NewSession(watchdog.Identity(endpoint.Id, AgentComponent.Watchdog));
        Assert.True(await scope.Harness.Manager.OpenAsync(control, new Hello { AgentVersion = "0.3.0", Hostname = "SRV-LOAD", Component = Component.Watchdog },
            CancellationToken.None));
        return new LoadEndpoint(endpoint, watchdog, control);
    }

    /// <summary>
    /// The endpoint's side, as the watchdog does it: every offer that arrives over the control session connects the relay for that
    /// participant. Runs until cancelled; returns the sockets by participant.
    /// </summary>
    private static Task AnswerOffersAsync(RelayScope scope, LoadEndpoint endpoint, ConcurrentDictionary<Guid, WebSocket> sockets, CancellationToken cancellationToken) =>
        Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                ServerMessage message;
                try
                {
                    message = await endpoint.Control.Outbox.ReadAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (message.BodyCase != ServerMessage.BodyOneofCase.RemoteSessionOffer)
                {
                    continue;
                }

                var token = RemoteSessionToken.Parser.ParseFrom(message.RemoteSessionOffer.Session.Payload);
                var participantId = Guid.Parse(token.ParticipantId);
                var (server, client) = SocketPair();
                _ = Task.Run(() => scope.Relay.RunEndpointAsync(server, endpoint.Identity, participantId, CancellationToken.None), CancellationToken.None);
                var hello = new RelayEndpointHello
                {
                    ParticipantId = token.ParticipantId, EndpointPublicKey = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(32)),
                    Signature = ByteString.CopyFrom(RandomNumberGenerator.GetBytes(64)),
                    CertificatePublicKey = ByteString.CopyFrom(endpoint.Watchdog.Key.ExportSubjectPublicKeyInfo())
                };
                await client.SendAsync(hello.ToByteArray(), WebSocketMessageType.Binary, true, CancellationToken.None);
                sockets[participantId] = client;
            }
        }, CancellationToken.None);

    private static int LoadSetting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    /// <summary>A screen frame of a session: the participant id, the frame number, then random bytes.</summary>
    private static byte[] LoadFrame(Guid participantId, int number)
    {
        var frame = RandomNumberGenerator.GetBytes(LoadFrameBytes);
        participantId.TryWriteBytes(frame);
        BinaryPrimitives.WriteInt32BigEndian(frame.AsSpan(16), number);
        return frame;
    }

    [Fact]
    public async Task Many_sessions_of_many_endpoints_carry_screen_traffic_through_one_relay_at_the_same_time()
    {
        var endpointCount = LoadSetting("FLEETO_RELAY_LOAD_ENDPOINTS", 25);
        var frames = LoadSetting("FLEETO_RELAY_LOAD_FRAMES", 20);
        var perEndpoint = RemoteSessionRules.MaxSessionsPerEndpoint;
        var warnings = new WarningLogger();
        // The relay's database connections come from a pool the size the gateway has in production, so a peak waits for a connection as it
        // does there, instead of opening as many as the test database allows.
        await using var pool = new NpgsqlDataSourceBuilder(new NpgsqlConnectionStringBuilder(_fixture.Database.ConnectionString)
        {
            MaxPoolSize = FleetoComponent.Gateway.DefaultPoolSize()
        }.ConnectionString).Build();
        await using var scope = await ScopeAsync(logger: warnings, store: new GatewayStore(pool));
        using var stop = new CancellationTokenSource();
        var sockets = new ConcurrentDictionary<Guid, WebSocket>();

        var endpoints = new List<LoadEndpoint>();
        for (var i = 0; i < endpointCount; i++)
        {
            endpoints.Add(await AddEndpointAsync(scope));
        }

        var answering = endpoints.Select(e => AnswerOffersAsync(scope, e, sockets, stop.Token)).ToList();
        var participants = new List<SignedParticipant>();
        foreach (var endpoint in endpoints)
        {
            for (var i = 0; i < perEndpoint; i++)
            {
                participants.Add(await SignedParticipantAsync(endpoint.Endpoint));
            }
        }

        // Every browser at once.
        var setup = Stopwatch.StartNew();
        var browsers = participants.Select(p => (Participant: p, Browser: StartBrowser(scope, p.ParticipantId))).ToList();
        await Task.WhenAll(browsers.Select(b => b.Browser.SendHelloAsync(b.Participant)));
        var ready = await Task.WhenAll(browsers.Select(b => b.Browser.ReadTextAsync()));
        Assert.All(ready, r => Assert.True(r.GetProperty("type").GetString() == "ready", r + " " + string.Join(" | ", warnings.Messages)));
        Assert.Equal(participants.Count, scope.Relay.Count);
        var setupTime = setup.Elapsed;

        // Screen traffic: the endpoint sends a frame, the browser checks it is its own and acknowledges it, then the next frame follows.
        var roundTrips = new ConcurrentBag<double>();
        var traffic = Stopwatch.StartNew();
        await Task.WhenAll(browsers.Select(b => Task.Run(async () =>
        {
            var id = b.Participant.ParticipantId;
            var endpointSocket = sockets[id];
            for (var n = 0; n < frames; n++)
            {
                var frame = LoadFrame(id, n);
                var sent = Stopwatch.GetTimestamp();
                await endpointSocket.SendAsync(frame, WebSocketMessageType.Binary, true, CancellationToken.None);
                var (_, received) = await ReadAsync(b.Browser.Socket);
                Assert.Equal(frame, received);
                Assert.Equal(id, new Guid(received.AsSpan(0, 16)));
                var ack = new byte[20];
                id.TryWriteBytes(ack);
                BinaryPrimitives.WriteInt32BigEndian(ack.AsSpan(16), n);
                await b.Browser.Socket.SendAsync(ack, WebSocketMessageType.Binary, true, CancellationToken.None);
                var (_, acked) = await ReadAsync(endpointSocket);
                Assert.Equal(ack, acked);
                roundTrips.Add(Stopwatch.GetElapsedTime(sent).TotalMilliseconds);
            }
        })));
        var trafficTime = traffic.Elapsed;

        // Every technician closes the window: each relay ends and its participant is recorded as ended.
        await Task.WhenAll(browsers.Select(b => b.Browser.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)));
        await Task.WhenAll(browsers.Select(b => b.Browser.Relay)).WaitAsync(TimeSpan.FromSeconds(60));
        await stop.CancelAsync();
        await Task.WhenAll(answering);
        Assert.Equal(0, scope.Relay.Count);

        var ids = participants.Select(p => p.ParticipantId).ToList();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var states = await db.RemoteSessionParticipants.AsNoTracking().Where(p => ids.Contains(p.Id)).Select(p => p.State).ToListAsync();
            Assert.Equal(ids.Count, states.Count);
            Assert.All(states, state => Assert.Equal(RemoteParticipantState.Ended, state));
        }

        var sorted = roundTrips.Order().ToList();
        double Percentile(double p) => sorted[Math.Min(sorted.Count - 1, (int)Math.Ceiling(p * sorted.Count) - 1)];
        var megabytes = (double)participants.Count * frames * LoadFrameBytes / (1024 * 1024);
        _output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{participants.Count} sessions on {endpointCount} endpoints: paired in {setupTime.TotalMilliseconds:0} ms; " +
            $"{participants.Count * frames} frames ({megabytes:0} MiB) in {trafficTime.TotalMilliseconds:0} ms ({megabytes / trafficTime.TotalSeconds:0} MiB/s); " +
            $"round trip p50 {Percentile(0.50):0.0} ms, p95 {Percentile(0.95):0.0} ms, p99 {Percentile(0.99):0.0} ms"));
        // The relay adds nothing a technician notices on a loaded gateway: a frame and its acknowledgement cross it in well under a second.
        Assert.True(Percentile(0.99) < 1000, $"p99 round trip {Percentile(0.99):0} ms");
    }

    [Fact]
    public async Task Sessions_that_arrive_at_once_for_one_endpoint_never_exceed_its_limit()
    {
        await using var scope = await ScopeAsync();
        using var stop = new CancellationTokenSource();
        var sockets = new ConcurrentDictionary<Guid, WebSocket>();
        var endpoint = await AddEndpointAsync(scope);
        var answering = AnswerOffersAsync(scope, endpoint, sockets, stop.Token);

        var extra = 6;
        var participants = new List<SignedParticipant>();
        for (var i = 0; i < RemoteSessionRules.MaxSessionsPerEndpoint + extra; i++)
        {
            participants.Add(await SignedParticipantAsync(endpoint.Endpoint));
        }

        var browsers = participants.Select(p => (Participant: p, Browser: StartBrowser(scope, p.ParticipantId))).ToList();
        await Task.WhenAll(browsers.Select(b => b.Browser.SendHelloAsync(b.Participant)));
        var answers = await Task.WhenAll(browsers.Select(b => b.Browser.ReadTextAsync()));
        var served = answers.Count(a => a.GetProperty("type").GetString() == "ready");
        var refused = answers.Where(a => a.GetProperty("type").GetString() == "error").ToList();
        Assert.Equal(RemoteSessionRules.MaxSessionsPerEndpoint, served);
        Assert.Equal(extra, refused.Count);
        Assert.All(refused, r => Assert.Contains("most remote sessions", r.GetProperty("message").GetString()));

        await Task.WhenAll(browsers.Select(b => b.Browser.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None)
            .ContinueWith(_ => { }, TaskScheduler.Default)));
        await Task.WhenAll(browsers.Select(b => b.Browser.Relay)).WaitAsync(TimeSpan.FromSeconds(30));
        await stop.CancelAsync();
        await answering;
        Assert.Equal(0, scope.Relay.Count);
    }

    /// <summary>Keeps the warnings of the relay with their exceptions, so a failure under load says what went wrong.</summary>
    private sealed class WarningLogger : ILogger<RemoteRelay>
    {
        public ConcurrentQueue<string> Messages { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                Messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : ": " + exception.GetType().Name + " " + exception.Message));
            }
        }
    }
}
