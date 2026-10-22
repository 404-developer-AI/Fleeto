using System.Net;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Microsoft.AspNetCore.Connections;

namespace Fleeto.Gateway.Tls;

/// <summary>
/// The security monitor on the agent port (0.7.0). <see cref="OnConnectionAsync"/> runs after the PROXY protocol header, so it
/// sees the agent's address, and before TLS: an address an admin blocked is dropped before the handshake, and a connection that
/// never completes its handshake counts as a signal (a port scanner, a client without a certificate of the instance). Kestrel
/// logs handshake failures only at debug level, so this is the only place they become visible.
/// <para>
/// Only a manual block drops the connection here. An address blocked by the security monitor or a blocklist (step 3) can be the shared
/// address of an office with managed endpoints behind it, so on the agent port it is refused only where no valid certificate
/// is involved: enrollment, recovery and the CA endpoint (<see cref="RefuseBlocked"/>). An endpoint with a valid certificate is
/// stronger evidence than an address on a list.
/// </para>
/// </summary>
public sealed class AgentConnectionGuard
{
    private readonly IpBlockList _list;
    private readonly SecuritySignalRecorder _signals;
    private readonly ProxyProtocolMiddleware _proxy;
    private readonly int _relayPort;

    public AgentConnectionGuard(IpBlockList list, SecuritySignalRecorder signals, ProxyProtocolMiddleware proxy,
        Microsoft.Extensions.Options.IOptions<GatewayOptions> options)
    {
        _list = list;
        _signals = signals;
        _proxy = proxy;
        _relayPort = options.Value.RelayPort;
    }

    /// <summary>
    /// The caller's address: on the agent port the one of the PROXY protocol header; on the relay port, behind the host proxy, the
    /// last X-Forwarded-For entry, but only when the connection comes from the trusted proxy.
    /// </summary>
    public IPAddress? ClientAddress(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;
        if (remote is not null && context.Connection.LocalPort == _relayPort &&
            _proxy.IsTrusted(new IPEndPoint(remote, context.Connection.RemotePort)) &&
            context.Request.Headers["X-Forwarded-For"].ToString() is { Length: > 0 } forwarded)
        {
            var last = forwarded.Split(',').Select(s => s.Trim()).LastOrDefault(s => s.Length > 0);
            if (IPAddress.TryParse(last, out var parsed))
            {
                remote = parsed;
            }
        }

        return remote is null ? null : IpNetworks.Normalize(remote);
    }

    /// <summary>Before TLS: drops a manually blocked address, and counts a connection whose handshake never completes.</summary>
    public async Task OnConnectionAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        var address = (connection.RemoteEndPoint as IPEndPoint)?.Address;
        if (_list.Check(address) == IpVerdict.BlockedManual)
        {
            _signals.Record(SecuritySignalKind.Blocked, address);
            connection.Abort(new ConnectionAbortedException("The address is blocked."));
            return;
        }

        var handshake = new HandshakeFeature();
        connection.Features.Set(handshake);
        try
        {
            await next(connection);
        }
        finally
        {
            if (!handshake.Completed)
            {
                _signals.Record(SecuritySignalKind.TlsHandshakeFailed, address);
            }
        }
    }

    /// <summary>After TLS: the handshake completed.</summary>
    public Task OnSecuredAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        if (connection.Features.Get<HandshakeFeature>() is { } handshake)
        {
            handshake.Completed = true;
        }

        return next(connection);
    }

    /// <summary>
    /// Endpoint filter for what the agent port serves without a valid certificate (enrollment, recovery, the CA): any block
    /// refuses it with 403. Never on the session of an agent with a valid certificate.
    /// </summary>
    public static Func<EndpointFilterInvocationContext, EndpointFilterDelegate, ValueTask<object?>> RefuseBlocked =>
        (invocation, next) =>
        {
            var context = invocation.HttpContext;
            var guard = context.RequestServices.GetRequiredService<AgentConnectionGuard>();
            return guard.IsBlocked(guard.ClientAddress(context))
                ? ValueTask.FromResult<object?>(Results.Problem(statusCode: StatusCodes.Status403Forbidden, title: "Address blocked",
                    detail: "Requests from this address are blocked. If that is a mistake, ask an administrator of the instance to allow the address in Settings, Security."))
                : next(invocation);
        };

    /// <summary>Whether any block applies to the address; counted as a signal when it does.</summary>
    public bool IsBlocked(IPAddress? address)
    {
        if (_list.Check(address) is not (IpVerdict.BlockedManual or IpVerdict.BlockedAutomatic or IpVerdict.BlockedFeed))
        {
            return false;
        }

        _signals.Record(SecuritySignalKind.Blocked, address);
        return true;
    }

    private sealed class HandshakeFeature
    {
        public bool Completed { get; set; }
    }
}

/// <summary>
/// Recognises vulnerability scanners on the agent and relay ports (0.7.0, security monitor), as web does: a probe gets a bare 404
/// and counts as a signal, and so does any other 404 on a path without a route. The verdict is <see cref="ScannerPatterns"/>.
/// </summary>
public sealed class GatewayScannerMiddleware
{
    private readonly RequestDelegate _next;
    private readonly SecuritySignalRecorder _signals;
    private readonly AgentConnectionGuard _guard;
    private readonly int _healthPort;

    public GatewayScannerMiddleware(RequestDelegate next, SecuritySignalRecorder signals, AgentConnectionGuard guard,
        Microsoft.Extensions.Options.IOptions<GatewayOptions> options)
    {
        _next = next;
        _signals = signals;
        _guard = guard;
        _healthPort = options.Value.HealthPort;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (context.Connection.LocalPort == _healthPort)
        {
            await _next(context);
            return;
        }

        var routeExists = context.GetEndpoint() is not null;
        var raw = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget ?? context.Request.Path.Value ?? string.Empty;
        var question = raw.IndexOf('?');
        var match = ScannerPatterns.Classify(question < 0 ? raw : raw[..question], question < 0 ? string.Empty : raw[(question + 1)..],
            context.Request.Headers.UserAgent.ToString(), routeExists);
        if (match.Verdict != ScannerVerdict.None)
        {
            _signals.Record(match.Verdict switch
            {
                ScannerVerdict.ScannerAgent => SecuritySignalKind.ScannerAgent,
                ScannerVerdict.AttackPattern => SecuritySignalKind.AttackPattern,
                _ when match.HighConfidence => SecuritySignalKind.SensitivePath,
                _ => SecuritySignalKind.ScannerPath
            }, _guard.ClientAddress(context));
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await _next(context);
        if (!routeExists && context.Response.StatusCode == StatusCodes.Status404NotFound)
        {
            _signals.Record(SecuritySignalKind.NotFound, _guard.ClientAddress(context));
        }
    }
}
