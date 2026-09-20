using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Gateway.Data;
using Fleeto.Gateway.Http;
using Fleeto.Gateway.Sessions;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.Extensions.Options;
using Npgsql;
using RemoteSessionKind = Fleeto.Core.Entities.RemoteSessionKind;

namespace Fleeto.Gateway.Remote;

/// <summary>
/// The remote session relay (0.3.0, ARCHITECTURE.md §4 Remote session). The browser opens <c>/relay/v1/sessions/&lt;participant&gt;</c> through
/// the host proxy and presents its signed token; the gateway verifies it, claims the participant once in the database, checks the tier and
/// asks the serving service on the endpoint to connect <c>/v1/relay/&lt;participant&gt;</c> with its client certificate. Once both sides
/// are there, the endpoint's signed key goes to the browser and every later message is passed on unread: the gateway never holds a
/// session key. A revoked certificate or an endpoint that is no longer managed ends its relays at once.
/// </summary>
public sealed class RemoteRelay : BackgroundService
{
    public const string BrowserPathPrefix = "/relay/v1/sessions/";
    public const string EndpointPathPrefix = "/v1/relay/";

    /// <summary>Largest relayed message: an encrypted frame of at most 1 MiB plus header and tag.</summary>
    public const int MaxMessageBytes = 1024 * 1024 + 256;

    private const int MaxHelloBytes = 16 * 1024;
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan TrustReload = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TierCheckInterval = TimeSpan.FromMinutes(1);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly GatewayStore _store;
    private readonly AgentSessionManager _sessions;
    private readonly CertificateAllowList _allowList;
    private readonly ProxyProtocolMiddleware _proxy;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;
    private readonly ILogger<RemoteRelay> _logger;
    private readonly ConcurrentDictionary<Guid, Pairing> _pairings = new();

    // Relay slots by endpoint, and in all, guarded by the lock on _slots: a slot is held from the verified token until the relay ends.
    private readonly Dictionary<Guid, int> _slots = new();
    private int _slotsTotal;
    private readonly PartitionedRateLimiter<string> _connectLimiter;
    private readonly IDisposable _statusSubscription;
    private volatile RelayTrust? _trust;
    private volatile bool _ready;

    public RemoteRelay(GatewayStore store, AgentSessionManager sessions, CertificateAllowList allowList, ProxyProtocolMiddleware proxy,
        INotificationBus bus, TimeProvider time, IOptions<GatewayOptions> options, ILogger<RemoteRelay> logger)
    {
        _store = store;
        _sessions = sessions;
        _allowList = allowList;
        _proxy = proxy;
        _bus = bus;
        _time = time;
        _options = options.Value;
        _logger = logger;
        _connectLimiter = PartitionedRateLimiter.Create<string, string>(address => RateLimitPartition.GetFixedWindowLimiter(address,
            _ => new FixedWindowRateLimiterOptions { PermitLimit = _options.RelayConnectsPerMinutePerAddress, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        _allowList.Reloaded += OnAllowListReloaded;
        _sessions.RemoteSessionRefused += OnRefused;
        _statusSubscription = _bus.Subscribe(NotificationChannels.EndpointStatus, OnEndpointStatusAsync);
    }

    public int Count => _pairings.Count;

    // ---------------------------------------------------------------------------------------------------------------
    // Browser side
    // ---------------------------------------------------------------------------------------------------------------

    public async Task HandleBrowserAsync(HttpContext context, Guid participantId)
    {
        var trust = _trust;
        if (!_ready || trust is null)
        {
            context.Response.Headers.RetryAfter = "10";
            await Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "Relay starting", "The relay is not ready yet. Try again in a moment.");
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Problems.WriteAsync(context, StatusCodes.Status400BadRequest, "WebSocket required", "This address only accepts a remote session from Fleeto.");
            return;
        }

        // A browser always sends Origin with a WebSocket; only the instance's own web UI may open a relay.
        if (!OriginAllowed(context.Request.Headers.Origin.ToString(), trust.WebBaseUrl))
        {
            await Problems.WriteAsync(context, StatusCodes.Status403Forbidden, "Not allowed", "Open the remote session from the Fleeto web UI.");
            return;
        }

        var address = BrowserAddress(context);
        using var lease = _connectLimiter.AttemptAcquire(address ?? "unknown");
        if (!lease.IsAcquired)
        {
            context.Response.Headers.RetryAfter = "60";
            await Problems.WriteAsync(context, StatusCodes.Status429TooManyRequests, "Too many sessions",
                "Too many remote sessions were opened from this address. Wait a minute and try again.");
            return;
        }

        using var browser = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            DangerousEnableCompression = false,
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            KeepAliveTimeout = TimeSpan.FromSeconds(60)
        });
        await RunBrowserAsync(browser, participantId, trust, address, context.RequestAborted);
    }

    internal async Task RunBrowserAsync(WebSocket browser, Guid participantId, RelayTrust trust, string? address, CancellationToken aborted)
    {
        var hello = await ReceiveHelloAsync(browser, aborted);
        // The time of the check is when the token arrived, not when the browser connected: waiting for the hello never extends a token.
        var now = _time.GetUtcNow().UtcDateTime;
        if (hello is null || !TryVerifyToken(hello, trust, participantId, now, out var token, out var payload))
        {
            await FailAsync(browser, "The session token is not valid. Close this window and open the session again.");
            return;
        }

        // The limits are taken as a slot before anything else, atomically: sessions that arrive at the same moment could all pass a count
        // of the running ones and exceed the limit together (found by the load test of 0.3.0 step 7).
        if (!Guid.TryParse(token.EndpointId, out var endpointId) || !TryTakeSlot(endpointId))
        {
            await FailAsync(browser, "This endpoint already has the most remote sessions it serves at a time. Close one and try again.");
            return;
        }

        try
        {
            await RunClaimedAsync(browser, participantId, endpointId, hello, payload, address, now, aborted);
        }
        finally
        {
            ReleaseSlot(endpointId);
        }
    }

    /// <summary>Takes a relay slot for an endpoint when it has fewer than its limit and the relay fewer than its own.</summary>
    private bool TryTakeSlot(Guid endpointId)
    {
        lock (_slots)
        {
            var taken = _slots.GetValueOrDefault(endpointId);
            if (taken >= RemoteSessionRules.MaxSessionsPerEndpoint || _slotsTotal >= _options.MaxRelays)
            {
                return false;
            }

            _slots[endpointId] = taken + 1;
            _slotsTotal++;
            return true;
        }
    }

    private void ReleaseSlot(Guid endpointId)
    {
        lock (_slots)
        {
            var taken = _slots.GetValueOrDefault(endpointId) - 1;
            if (taken <= 0)
            {
                _slots.Remove(endpointId);
            }
            else
            {
                _slots[endpointId] = taken;
            }

            _slotsTotal--;
        }
    }

    private async Task RunClaimedAsync(WebSocket browser, Guid participantId, Guid endpointId, BrowserHello hello, byte[] payload, string? address,
        DateTime now, CancellationToken aborted)
    {
        ClaimedParticipant? participant;
        try
        {
            participant = await _store.ClaimParticipantAsync(participantId, payload, address, now, aborted);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Remote session {ParticipantId}: the database is unavailable", participantId);
            await FailAsync(browser, "The relay cannot reach its database. Try again in a minute.");
            return;
        }

        if (participant is null)
        {
            await FailAsync(browser, "This session token was used before or has expired. Open the session again.");
            return;
        }

        if (participant.EndpointId != endpointId)
        {
            // Claimed, so it is ended as refused, never left waiting.
            await RefuseAsync(browser, participant, address, "This session token was used before or has expired. Open the session again.");
            return;
        }

        // Tier enforcement, layer 3 (the gateway): never relay to an endpoint whose stored tier is agent-only.
        if (participant.Tier != EndpointTier.Managed)
        {
            await RefuseAsync(browser, participant, address, "The endpoint is not managed. Switch it to managed before opening a remote session.");
            return;
        }

        // Remote background is served by the watchdog, remote control (0.3.0 step 3) by the agent: the offer goes over that service's session.
        AgentSession? control;
        switch (participant.Kind, participant.Component)
        {
            case (RemoteSessionKind.RemoteBackground, AgentComponent.Watchdog):
                _sessions.TryGetWatchdogSession(participant.EndpointId, out control);
                break;
            case (RemoteSessionKind.RemoteControl, AgentComponent.Agent):
                _sessions.TryGetSession(participant.EndpointId, out control);
                break;
            default:
                await RefuseAsync(browser, participant, address, "This kind of remote session is not available yet.");
                return;
        }

        if (control is null || control.IsClosing || control.Tier != EndpointTier.Managed)
        {
            await RefuseAsync(browser, participant, address, participant.Component == AgentComponent.Watchdog
                ? "The watchdog of this endpoint is not connected. Remote background needs the Fleeto watchdog to be online. Try again when it is."
                : "The agent of this endpoint is not connected. Remote control needs the endpoint to be online. Try again when it is.");
            return;
        }

        var pairing = new Pairing(participant, browser, address);
        if (!_pairings.TryAdd(participantId, pairing))
        {
            await RefuseAsync(browser, participant, address, "This session token was used before. Open the session again.");
            return;
        }

        try
        {
            await ServeAsync(pairing, control, hello, aborted);
        }
        finally
        {
            _pairings.TryRemove(KeyValuePair.Create(participantId, pairing));
            pairing.Dispose();
        }
    }

    private async Task ServeAsync(Pairing pairing, AgentSession control, BrowserHello hello, CancellationToken aborted)
    {
        var participant = pairing.Participant;
        if (!control.Send(new ServerMessage
            {
                RemoteSessionOffer = new RemoteSessionOffer
                {
                    Session = new SignedRemoteSession
                    {
                        Payload = ByteString.CopyFrom(Convert.FromBase64String(hello.Token)),
                        Signature = ByteString.CopyFrom(Convert.FromBase64String(hello.Signature)),
                        KeyId = hello.KeyId
                    }
                }
            }))
        {
            await RefuseAsync(pairing.Browser, participant, pairing.Address,
                $"The {(participant.Component == AgentComponent.Watchdog ? "watchdog" : "agent")} of this endpoint disconnected. Try again in a moment.");
            return;
        }

        using var wait = CancellationTokenSource.CreateLinkedTokenSource(aborted, pairing.Closed);
        wait.CancelAfter(RemoteSessionRules.EndpointConnectTimeout);
        EndpointArrival arrival;
        try
        {
            arrival = await pairing.Arrival.Task.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            // An endpoint that arrives in the same moment is released instead of waiting for a browser that gave up.
            if (!pairing.Arrival.TrySetCanceled() && pairing.Arrival.Task.IsCompletedSuccessfully)
            {
                pairing.Arrival.Task.Result.Done.TrySetResult();
            }

            var reason = pairing.CloseReason ?? (aborted.IsCancellationRequested
                ? "The browser closed the session before the endpoint connected."
                : "The endpoint did not connect in time. Check that the watchdog runs Fleeto 0.3.0 or later and can reach the gateway.");
            await RefuseAsync(pairing.Browser, participant, pairing.Address, reason);
            return;
        }
        catch (RemoteRefusedException refused)
        {
            await RefuseAsync(pairing.Browser, participant, pairing.Address, refused.Message);
            return;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        try
        {
            await _store.MarkParticipantConnectedAsync(participant, pairing.Address, now, CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // Without the audit entry the session does not run.
            _logger.LogWarning(ex, "Remote session {ParticipantId}: could not record the join", participant.ParticipantId);
            pairing.Close("The relay cannot reach its database.");
            arrival.Done.TrySetResult();
            await FailAsync(pairing.Browser, "The relay cannot reach its database. Try again in a minute.");
            return;
        }

        _logger.LogInformation("Remote session {ParticipantId}: {Technician} connected to endpoint {EndpointId} ({Kind})", participant.ParticipantId,
            participant.UserName, participant.EndpointId, participant.Kind);
        string endReason;
        try
        {
            await SendTextAsync(pairing.Browser, new
            {
                type = "ready", endpointPublicKey = Convert.ToBase64String(arrival.Hello.EndpointPublicKey.Span),
                signature = Convert.ToBase64String(arrival.Hello.Signature.Span), certificatePublicKey = Convert.ToBase64String(arrival.Hello.CertificatePublicKey.Span)
            }, aborted);
            endReason = await PipeAsync(pairing, arrival.Socket, aborted);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException)
        {
            endReason = pairing.CloseReason ?? "The connection was lost.";
        }
        finally
        {
            arrival.Done.TrySetResult();
        }

        try
        {
            await _store.EndParticipantAsync(participant, connected: true, endReason, pairing.Address, _time.GetUtcNow().UtcDateTime, CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            // The workers end stale participants; the gateway resets them at its next start.
            _logger.LogWarning(ex, "Remote session {ParticipantId}: could not record the end", participant.ParticipantId);
        }

        _logger.LogInformation("Remote session {ParticipantId}: ended ({Reason})", participant.ParticipantId, endReason);
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Endpoint side
    // ---------------------------------------------------------------------------------------------------------------

    public async Task HandleEndpointAsync(HttpContext context, Guid participantId)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Problems.WriteAsync(context, StatusCodes.Status400BadRequest, "WebSocket required", "This address only accepts a Fleeto service.");
            return;
        }

        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
        if (_allowList.Authorize(certificate, out var identity) != AllowListDecision.Accepted || identity is null)
        {
            await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "Certificate not accepted", "Connect with a valid agent or watchdog certificate.");
            return;
        }

        // The same answer for an unknown participant and another endpoint's: nothing to learn from probing.
        if (!IsWaitingFor(participantId, identity))
        {
            await Problems.WriteAsync(context, StatusCodes.Status404NotFound, "No session", "No remote session waits for this endpoint.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            DangerousEnableCompression = false,
            KeepAliveInterval = TimeSpan.FromSeconds(20),
            KeepAliveTimeout = TimeSpan.FromSeconds(60)
        });
        await RunEndpointAsync(socket, identity, participantId, context.RequestAborted);
    }

    /// <summary>True when a browser waits for exactly this endpoint and service to connect the participant.</summary>
    internal bool IsWaitingFor(Guid participantId, AgentIdentity identity) =>
        _pairings.TryGetValue(participantId, out var pairing) && pairing.Participant.EndpointId == identity.EndpointId &&
        pairing.Participant.Component == identity.Role && !pairing.Arrival.Task.IsCompleted;

    /// <summary>Reads the endpoint's hello, hands the socket to the waiting browser side and keeps it open until that side is done.</summary>
    internal async Task RunEndpointAsync(WebSocket socket, AgentIdentity identity, Guid participantId, CancellationToken aborted)
    {
        if (!IsWaitingFor(participantId, identity) || !_pairings.TryGetValue(participantId, out var pairing))
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "no session");
            return;
        }

        var hello = await ReceiveEndpointHelloAsync(socket, participantId, aborted);
        if (hello is null)
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "invalid hello");
            return;
        }

        var arrival = new EndpointArrival(socket, identity, hello);
        if (!pairing.Arrival.TrySetResult(arrival))
        {
            await CloseAsync(socket, WebSocketCloseStatus.PolicyViolation, "already connected");
            return;
        }

        // Only the endpoint that won is the identity revocation is checked against.
        pairing.Identity = identity;

        // A revocation between the allow list check and now still ends the session.
        if (!_allowList.IsStillAllowed(identity.Fingerprint, identity.EndpointId))
        {
            pairing.Close("The endpoint certificate was revoked or the endpoint was deleted.");
        }

        await arrival.Done.Task.WaitAsync(aborted).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    /// <summary>The service refused the offer (bad token, agent-only configuration, too many sessions): tell the waiting browser why.</summary>
    private void OnRefused(AgentSession session, RemoteSessionRefused refused)
    {
        if (!Guid.TryParse(refused.ParticipantId, out var participantId) || !_pairings.TryGetValue(participantId, out var pairing) ||
            pairing.Participant.EndpointId != session.EndpointId || pairing.Participant.Component != session.Component)
        {
            return;
        }

        var message = DbText.Clean(refused.Error, 500);
        pairing.Arrival.TrySetException(new RemoteRefusedException(string.IsNullOrWhiteSpace(message) ? "The endpoint refused the session." : message));
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Relaying
    // ---------------------------------------------------------------------------------------------------------------

    /// <summary>Passes messages both ways, unread, until either side closes or the relay is closed. Returns why it ended.</summary>
    private async Task<string> PipeAsync(Pairing pairing, WebSocket endpoint, CancellationToken aborted)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(aborted, pairing.Closed);
        var toEndpoint = CopyAsync(pairing.Browser, endpoint, linked.Token);
        var toBrowser = CopyAsync(endpoint, pairing.Browser, linked.Token);
        var first = await Task.WhenAny(toEndpoint, toBrowser);
        var reason = pairing.CloseReason ?? (first == toEndpoint ? "The technician closed the session." : "The endpoint closed the session.");
        if (first.IsFaulted && first.Exception?.InnerException is RelayProtocolException protocol)
        {
            reason = protocol.Message;
        }

        await linked.CancelAsync();
        await Task.WhenAll(CloseAsync(pairing.Browser, WebSocketCloseStatus.NormalClosure, "session ended"),
            CloseAsync(endpoint, WebSocketCloseStatus.NormalClosure, "session ended"));
        await Task.WhenAll(toEndpoint.ContinueWith(_ => { }, TaskScheduler.Default), toBrowser.ContinueWith(_ => { }, TaskScheduler.Default));
        return reason;
    }

    private static async Task CopyAsync(WebSocket from, WebSocket to, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var messageBytes = 0;
            while (true)
            {
                var result = await from.ReceiveAsync(buffer.AsMemory(), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    throw new RelayProtocolException("A side sent a message the relay does not pass on; the session was closed.");
                }

                messageBytes += result.Count;
                if (messageBytes > MaxMessageBytes)
                {
                    throw new RelayProtocolException("A side sent a message larger than a session frame; the session was closed.");
                }

                await to.SendAsync(buffer.AsMemory(0, result.Count), WebSocketMessageType.Binary, result.EndOfMessage, cancellationToken);
                if (result.EndOfMessage)
                {
                    messageBytes = 0;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Tokens and hellos
    // ---------------------------------------------------------------------------------------------------------------

    internal sealed record BrowserHello(string Token, string Signature, string KeyId);

    private static async Task<BrowserHello?> ReceiveHelloAsync(WebSocket socket, CancellationToken aborted)
    {
        var text = await ReceiveSmallAsync(socket, WebSocketMessageType.Text, MaxHelloBytes, aborted);
        if (text is null)
        {
            return null;
        }

        try
        {
            var hello = JsonSerializer.Deserialize<BrowserHello>(text, Json);
            return hello is { Token.Length: > 0, Signature.Length: > 0, KeyId.Length: > 0 } ? hello : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<RelayEndpointHello?> ReceiveEndpointHelloAsync(WebSocket socket, Guid participantId, CancellationToken aborted)
    {
        var data = await ReceiveSmallAsync(socket, WebSocketMessageType.Binary, 4096, aborted);
        if (data is null)
        {
            return null;
        }

        try
        {
            var hello = RelayEndpointHello.Parser.ParseFrom(data);
            return Guid.TryParse(hello.ParticipantId, out var id) && id == participantId && RemoteSessionRules.IsValidPublicKey(hello.EndpointPublicKey.Span) &&
                   hello.Signature.Length == 64 && hello.CertificatePublicKey.Length is > 0 and <= 512
                ? hello
                : null;
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> ReceiveSmallAsync(WebSocket socket, WebSocketMessageType type, int maxBytes, CancellationToken aborted)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(aborted);
        timeout.CancelAfter(HelloTimeout);
        var buffer = new byte[maxBytes];
        var count = 0;
        try
        {
            while (true)
            {
                if (count == buffer.Length)
                {
                    return null;
                }

                var result = await socket.ReceiveAsync(buffer.AsMemory(count), timeout.Token);
                if (result.MessageType != type)
                {
                    return null;
                }

                count += result.Count;
                if (result.EndOfMessage)
                {
                    return buffer[..count];
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// The token must carry a valid instance signature for this instance, name the participant of the path and still be valid on the
    /// gateway's clock. The database claim then proves it is the token fleeto-signer stored for that participant and was not used before.
    /// </summary>
    internal static bool TryVerifyToken(BrowserHello hello, RelayTrust trust, Guid participantId, DateTime now, out RemoteSessionToken token, out byte[] payload)
    {
        token = new RemoteSessionToken();
        payload = [];
        try
        {
            payload = Convert.FromBase64String(hello.Token);
            var signature = Convert.FromBase64String(hello.Signature);
            if (payload.Length is 0 or > 16 * 1024 || !trust.SigningKeys.TryGetValue(hello.KeyId, out var key) ||
                !Ed25519.Verify(key, SignatureContexts.RemoteSession, payload, signature))
            {
                return false;
            }

            token = RemoteSessionToken.Parser.ParseFrom(payload);
        }
        catch (Exception ex) when (ex is FormatException or InvalidProtocolBufferException or ArgumentException)
        {
            return false;
        }

        return Guid.TryParse(token.ParticipantId, out var tokenParticipant) && tokenParticipant == participantId &&
               Guid.TryParse(token.InstanceId, out var instance) && instance == trust.InstanceId &&
               token.ValidUntil is not null && token.ValidUntil.ToDateTime() > now;
    }

    internal static bool OriginAllowed(string origin, string webBaseUrl)
    {
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var given) || !Uri.TryCreate(webBaseUrl, UriKind.Absolute, out var expected))
        {
            return false;
        }

        return string.Equals(given.Scheme, expected.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(given.Host, expected.Host, StringComparison.OrdinalIgnoreCase) && given.Port == expected.Port;
    }

    /// <summary>The browser's address: from X-Forwarded-For only when the connection comes from the trusted host proxy on the relay port.</summary>
    private string? BrowserAddress(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && context.Connection.LocalPort == _options.RelayPort &&
            _proxy.IsTrusted(new IPEndPoint(remote, context.Connection.RemotePort)) &&
            context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            var last = forwarded.Split(',').Select(s => s.Trim()).LastOrDefault(s => s.Length > 0);
            if (IPAddress.TryParse(last, out var parsed))
            {
                remote = parsed;
            }
        }

        return remote is null ? null : (remote.IsIPv4MappedToIPv6 ? remote.MapToIPv4() : remote).ToString();
    }

    private async Task RefuseAsync(WebSocket browser, ClaimedParticipant participant, string? address, string reason)
    {
        try
        {
            await _store.EndParticipantAsync(participant, connected: false, reason, address, _time.GetUtcNow().UtcDateTime, CancellationToken.None);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Remote session {ParticipantId}: could not record the refusal", participant.ParticipantId);
        }

        _logger.LogInformation("Remote session {ParticipantId} for endpoint {EndpointId} refused: {Reason}", participant.ParticipantId, participant.EndpointId, reason);
        await FailAsync(browser, reason);
    }

    private static async Task FailAsync(WebSocket browser, string message)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await SendTextAsync(browser, new { type = "error", message }, timeout.Token);
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
        }

        await CloseAsync(browser, WebSocketCloseStatus.PolicyViolation, "refused");
    }

    private static Task SendTextAsync(WebSocket socket, object message, CancellationToken cancellationToken) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json)), WebSocketMessageType.Text, true, cancellationToken);

    private static async Task CloseAsync(WebSocket socket, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(status, description, timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            if (socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
            {
                socket.Abort();
            }
        }
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Revocation, tier, hosting
    // ---------------------------------------------------------------------------------------------------------------

    private void OnAllowListReloaded(Guid? endpointHint)
    {
        foreach (var pairing in _pairings.Values)
        {
            if (pairing.Identity is { } identity && (endpointHint is null || endpointHint == identity.EndpointId) &&
                !_allowList.IsStillAllowed(identity.Fingerprint, identity.EndpointId))
            {
                pairing.Close("The endpoint certificate was revoked or the endpoint was deleted.");
            }
        }
    }

    private async Task OnEndpointStatusAsync(string payload, CancellationToken cancellationToken)
    {
        if (Guid.TryParse(payload, out var endpointId) && _pairings.Values.Any(p => p.Participant.EndpointId == endpointId))
        {
            await CloseUnmanagedAsync([endpointId], cancellationToken);
        }
        else if (payload == NotificationBusEvents.Resync)
        {
            await CloseUnmanagedAsync(_pairings.Values.Select(p => p.Participant.EndpointId).Distinct().ToArray(), cancellationToken);
        }
    }

    /// <summary>Ends the relays of endpoints whose stored tier is no longer managed, or that were deleted.</summary>
    internal async Task CloseUnmanagedAsync(Guid[] endpointIds, CancellationToken cancellationToken)
    {
        if (endpointIds.Length == 0)
        {
            return;
        }

        IReadOnlyDictionary<Guid, EndpointTier> tiers;
        try
        {
            tiers = await _store.ReadTiersAsync(endpointIds, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not check the tier of endpoints with remote sessions");
            return;
        }

        foreach (var pairing in _pairings.Values)
        {
            var endpointId = pairing.Participant.EndpointId;
            if (endpointIds.Contains(endpointId) && (!tiers.TryGetValue(endpointId, out var tier) || tier != EndpointTier.Managed))
            {
                pairing.Close("The endpoint is no longer managed.");
            }
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!_ready && !stoppingToken.IsCancellationRequested)
        {
            try
            {
                var reset = await _store.ResetRemoteParticipantsAsync(_time.GetUtcNow().UtcDateTime, stoppingToken);
                _trust = await _store.ReadRelayTrustAsync(stoppingToken);
                _ready = true;
                if (reset > 0)
                {
                    _logger.LogInformation("{Count} remote session connections from before the start were ended", reset);
                }
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
            {
                _logger.LogWarning("The relay cannot read its database yet ({Error}); retrying in {Delay}", ex.Message, delay);
                await Task.Delay(delay, _time, stoppingToken).ContinueWith(_ => { }, TaskScheduler.Default);
                delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2));
            }
        }

        var lastTrust = _time.GetUtcNow();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TierCheckInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await CloseUnmanagedAsync(_pairings.Values.Select(p => p.Participant.EndpointId).Distinct().ToArray(), stoppingToken);
            if (_time.GetUtcNow() - lastTrust >= TrustReload)
            {
                try
                {
                    _trust = await _store.ReadRelayTrustAsync(stoppingToken);
                    lastTrust = _time.GetUtcNow();
                }
                catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
                {
                    _logger.LogWarning(ex, "Could not reload the instance signing keys for the relay; keeping the loaded keys");
                }
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (var pairing in _pairings.Values)
        {
            pairing.Close("The gateway is restarting.");
        }

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _allowList.Reloaded -= OnAllowListReloaded;
        _sessions.RemoteSessionRefused -= OnRefused;
        _statusSubscription.Dispose();
        _connectLimiter.Dispose();
        base.Dispose();
    }

    private sealed record EndpointArrival(WebSocket Socket, AgentIdentity Identity, RelayEndpointHello Hello)
    {
        /// <summary>Completed when the browser side no longer uses the endpoint socket.</summary>
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Pairing : IDisposable
    {
        private readonly CancellationTokenSource _closed = new();

        public Pairing(ClaimedParticipant participant, WebSocket browser, string? address)
        {
            Participant = participant;
            Browser = browser;
            Address = address;
        }

        public ClaimedParticipant Participant { get; }
        public WebSocket Browser { get; }
        public string? Address { get; }
        public TaskCompletionSource<EndpointArrival> Arrival { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentIdentity? Identity { get; set; }
        public string? CloseReason { get; private set; }
        public CancellationToken Closed => _closed.Token;

        public void Close(string reason)
        {
            CloseReason ??= reason;
            try
            {
                _closed.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }
        }

        public void Dispose() => _closed.Dispose();
    }

    private sealed class RemoteRefusedException(string message) : Exception(message);

    private sealed class RelayProtocolException(string message) : Exception(message);
}
