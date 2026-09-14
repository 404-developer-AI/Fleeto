using System.Buffers;
using System.Net.WebSockets;
using Fleetify.Gateway.Diagnostics;
using Fleetify.Gateway.Http;
using Fleetify.Gateway.Tls;
using Fleetify.Protocol;
using Fleetify.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.Extensions.Options;

namespace Fleetify.Gateway.Sessions;

/// <summary>
/// <c>GET /v1/connect</c>: authorizes the client certificate against the allow list, upgrades to a WebSocket and runs
/// the session: one receive loop (one protobuf AgentMessage per binary message, at most 4 MiB) and one send loop that
/// drains the session outbox.
/// </summary>
public sealed class AgentConnectionHandler
{
    private const int InitialBufferBytes = 4 * 1024;
    private static readonly TimeSpan SendLoopDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly AgentSessionManager _manager;
    private readonly CertificateAllowList _allowList;
    private readonly GatewayMetrics _metrics;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;
    private readonly ILogger<AgentConnectionHandler> _logger;

    public AgentConnectionHandler(AgentSessionManager manager, CertificateAllowList allowList, GatewayMetrics metrics, TimeProvider time,
        IOptions<GatewayOptions> options, ILogger<AgentConnectionHandler> logger)
    {
        _manager = manager;
        _allowList = allowList;
        _metrics = metrics;
        _time = time;
        _options = options.Value;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            await Problems.WriteAsync(context, StatusCodes.Status400BadRequest, "WebSocket required",
                "This endpoint only accepts WebSocket connections from a Fleeto agent.");
            return;
        }

        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
        var decision = _allowList.Authorize(certificate, out var identity);
        if (decision == AllowListDecision.NotLoaded || !_manager.IsReady)
        {
            _metrics.ConnectionRefused();
            context.Response.Headers.RetryAfter = "30";
            await Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "Gateway starting",
                "The gateway is not ready to accept agent sessions yet. Reconnect in a moment.");
            return;
        }

        if (decision != AllowListDecision.Accepted || identity is null)
        {
            _metrics.ConnectionRefused();
            _logger.LogInformation("Refused an agent session from {RemoteAddress}: {Reason}", RemoteAddress(context),
                certificate is null ? "no client certificate" : "certificate not on the allow list");
            await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "Agent certificate not accepted",
                certificate is null
                    ? "A client certificate is required. Enroll the agent first."
                    : "The agent certificate is not valid for this instance. Enroll the agent again with a new token.");
            return;
        }

        using var webSocket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
        {
            DangerousEnableCompression = false,
            KeepAliveInterval = TimeSpan.FromSeconds(30),
            KeepAliveTimeout = TimeSpan.FromSeconds(60)
        });
        using var session = new AgentSession(identity, RemoteAddress(context), _options.SendQueueCapacity, _time.GetUtcNow().UtcDateTime);
        await RunAsync(webSocket, session);
    }

    internal async Task RunAsync(WebSocket webSocket, AgentSession session)
    {
        var sendLoop = SendLoopAsync(webSocket, session);
        var buffer = new ReceiveBuffer(InitialBufferBytes);
        var accepted = false;
        try
        {
            AgentMessage? hello;
            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(session.Closed))
            {
                helloTimeout.CancelAfter(TimeSpan.FromSeconds(_options.HelloTimeoutSeconds));
                try
                {
                    hello = await ReceiveAsync(webSocket, buffer, helloTimeout.Token);
                }
                catch (OperationCanceledException) when (!session.Closed.IsCancellationRequested)
                {
                    session.Close(DisconnectCode.ProtocolError, "No Hello within the time limit.");
                    return;
                }
            }

            if (hello is null)
            {
                return;
            }

            if (hello.BodyCase != AgentMessage.BodyOneofCase.Hello)
            {
                session.Close(DisconnectCode.ProtocolError, "The first message must be Hello.");
                return;
            }

            accepted = await _manager.OpenAsync(session, hello.Hello, session.Closed);
            if (!accepted)
            {
                return;
            }

            while (true)
            {
                var message = await ReceiveAsync(webSocket, buffer, session.Closed);
                if (message is null)
                {
                    break;
                }

                await _manager.HandleAsync(session, message, session.Closed);
            }
        }
        catch (InvalidProtocolBufferException)
        {
            session.Close(DisconnectCode.ProtocolError, "A message could not be parsed.");
        }
        catch (ProtocolViolationException ex)
        {
            session.Close(DisconnectCode.ProtocolError, ex.Message);
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
            // Connection closed, aborted or cancelled; handled below.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Endpoint {EndpointId}: session failed", session.EndpointId);
        }
        finally
        {
            buffer.Dispose();
            if (!session.IsClosing)
            {
                session.Abort("The connection was closed.");
            }

            // Let the send loop deliver a queued Disconnect before the socket goes away.
            await Task.WhenAny(sendLoop, Task.Delay(SendLoopDrainTimeout));
            await AwaitPeerCloseAsync(webSocket);
            session.Abort(session.CloseReason);
            if (accepted)
            {
                await _manager.CloseAsync(session);
            }

            if (webSocket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
            {
                webSocket.Abort();
            }
        }
    }

    /// <summary>
    /// Reads one complete binary message. Returns null when the agent closed the connection. The buffer grows up to the
    /// protocol limit and shrinks back after an unusually large message.
    /// </summary>
    private static async ValueTask<AgentMessage?> ReceiveAsync(WebSocket webSocket, ReceiveBuffer buffer, CancellationToken cancellationToken)
    {
        var count = 0;
        while (true)
        {
            var capacity = Math.Min(buffer.Array.Length, ProtocolLimits.MaxMessageBytes);
            if (count >= capacity)
            {
                if (capacity >= ProtocolLimits.MaxMessageBytes)
                {
                    throw new ProtocolViolationException($"A message is larger than {ProtocolLimits.MaxMessageBytes / (1024 * 1024)} MiB.");
                }

                buffer.Grow(count, Math.Min(buffer.Array.Length * 2, ProtocolLimits.MaxMessageBytes));
                continue;
            }

            var result = await webSocket.ReceiveAsync(buffer.Array.AsMemory(count, capacity - count), cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.MessageType != WebSocketMessageType.Binary)
            {
                throw new ProtocolViolationException("Only binary messages are allowed.");
            }

            count += result.Count;
            if (result.EndOfMessage)
            {
                break;
            }
        }

        var message = AgentMessage.Parser.ParseFrom(buffer.Array.AsSpan(0, count));
        if (buffer.Array.Length > InitialBufferBytes * 16)
        {
            buffer.Reset(InitialBufferBytes);
        }

        return message;
    }

    private async Task SendLoopAsync(WebSocket webSocket, AgentSession session)
    {
        byte[]? buffer = null;
        var disconnected = false;
        try
        {
            await foreach (var message in session.Outbox.ReadAllAsync(CancellationToken.None))
            {
                var size = message.CalculateSize();
                if (buffer is null || buffer.Length < size)
                {
                    if (buffer is not null)
                    {
                        ArrayPool<byte>.Shared.Return(buffer);
                    }

                    buffer = ArrayPool<byte>.Shared.Rent(Math.Max(size, 1024));
                }

                message.WriteTo(buffer.AsSpan(0, size));
                session.MarkSendStarted(_time.GetUtcNow().UtcDateTime);
                await webSocket.SendAsync(buffer.AsMemory(0, size), WebSocketMessageType.Binary, true, session.Closed);
                session.MarkSendFinished();

                if (message.BodyCase == ServerMessage.BodyOneofCase.Disconnect)
                {
                    using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await webSocket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, closeTimeout.Token);
                    // Do not abort now: an abort resets the TCP connection, and a reset makes the peer discard the
                    // Disconnect it has not read yet. The receive loop ends on the agent's close frame or when the
                    // close grace period of the session runs out.
                    disconnected = true;
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException or ObjectDisposedException)
        {
            // The connection is gone; the receive loop notices through the cancelled session.
        }
        finally
        {
            if (buffer is not null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            if (!disconnected)
            {
                session.Abort(session.CloseReason);
            }
        }
    }

    /// <summary>
    /// After our close frame, gives the agent a moment to answer with its own, so the TCP connection ends with a normal
    /// close instead of a reset that could discard the Disconnect message on the agent's side.
    /// </summary>
    private static async Task AwaitPeerCloseAsync(WebSocket webSocket)
    {
        if (webSocket.State != WebSocketState.CloseSent)
        {
            return;
        }

        var scratch = new byte[256];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try
        {
            while (webSocket.State == WebSocketState.CloseSent)
            {
                var result = await webSocket.ReceiveAsync(scratch.AsMemory(), timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or WebSocketException or IOException)
        {
        }
    }

    private static string RemoteAddress(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

    /// <summary>A pooled receive buffer that can grow; a class because async methods cannot take ref parameters.</summary>
    private sealed class ReceiveBuffer : IDisposable
    {
        public ReceiveBuffer(int size)
        {
            Array = ArrayPool<byte>.Shared.Rent(size);
        }

        public byte[] Array { get; private set; }

        public void Grow(int keep, int size)
        {
            var larger = ArrayPool<byte>.Shared.Rent(size);
            Array.AsSpan(0, keep).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(Array);
            Array = larger;
        }

        public void Reset(int size)
        {
            ArrayPool<byte>.Shared.Return(Array);
            Array = ArrayPool<byte>.Shared.Rent(size);
        }

        public void Dispose() => ArrayPool<byte>.Shared.Return(Array);
    }

    private sealed class ProtocolViolationException : Exception
    {
        public ProtocolViolationException(string message)
            : base(message)
        {
        }
    }
}
