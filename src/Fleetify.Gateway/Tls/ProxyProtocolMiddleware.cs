using System.Net;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Options;

namespace Fleetify.Gateway.Tls;

/// <summary>
/// Kestrel connection middleware on the agent port, before TLS: reads the PROXY protocol v2 header that the host Caddy
/// sends ahead of the passed-through TLS connection and sets the connection's remote address to the agent's address. That
/// address then reaches the session (Public IP of the endpoint), the logs and the per-address enrollment rate limit.
/// <para>
/// Security: only a connection from a trusted proxy network may carry a header; from anywhere else the bytes go to TLS
/// unread, so an agent cannot claim another address. A trusted connection without a header (a Caddy that does not send
/// one yet) keeps the proxy's address. A malformed header, or no complete header within the timeout, closes the connection.
/// </para>
/// </summary>
public sealed class ProxyProtocolMiddleware
{
    private readonly ProxyProtocolOptions _options;
    private readonly IReadOnlyList<(IPAddress Network, int PrefixLength)> _trusted;
    private readonly ILogger<ProxyProtocolMiddleware> _logger;

    public ProxyProtocolMiddleware(IOptions<GatewayOptions> options, ILogger<ProxyProtocolMiddleware> logger)
    {
        _options = options.Value.ProxyProtocol;
        _trusted = ParseNetworks(_options.TrustedNetworks);
        _logger = logger;
    }

    public bool Enabled => _options.Enabled;

    /// <summary>Parses CIDR networks such as <c>172.16.0.0/12</c>; throws on an invalid entry so a typo stops the start.</summary>
    public static IReadOnlyList<(IPAddress Network, int PrefixLength)> ParseNetworks(IEnumerable<string>? networks)
    {
        var result = new List<(IPAddress, int)>();
        foreach (var entry in networks ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            var parts = entry.Trim().Split('/');
            if (!IPAddress.TryParse(parts[0], out var address) || parts.Length > 2)
            {
                throw new InvalidOperationException($"Gateway:ProxyProtocol:TrustedNetworks contains an invalid network: {entry}");
            }

            var max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
            var prefix = max;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out prefix) || prefix < 0 || prefix > max))
            {
                throw new InvalidOperationException($"Gateway:ProxyProtocol:TrustedNetworks contains an invalid prefix length: {entry}");
            }

            result.Add((address, prefix));
        }

        return result;
    }

    public bool IsTrusted(EndPoint? remote)
    {
        if (remote is not IPEndPoint { Address: var address })
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var (network, prefix) in _trusted)
        {
            if (new IPNetwork(network, prefix).Contains(address))
            {
                return true;
            }
        }

        return false;
    }

    public async Task OnConnectionAsync(ConnectionContext connection, ConnectionDelegate next)
    {
        if (!_options.Enabled || !IsTrusted(connection.RemoteEndPoint))
        {
            await next(connection);
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(connection.ConnectionClosed);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(_options.HeaderTimeoutMilliseconds, 100, 60_000)));
        var input = connection.Transport.Input;
        while (true)
        {
            System.IO.Pipelines.ReadResult read;
            try
            {
                read = await input.ReadAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Closed a connection from {RemoteEndPoint}: no PROXY protocol header in time", connection.RemoteEndPoint);
                connection.Abort();
                return;
            }

            var buffer = read.Buffer;
            var result = ProxyProtocolV2.Parse(buffer);
            switch (result.Status)
            {
                case ProxyProtocolV2.Status.Parsed:
                    input.AdvanceTo(buffer.GetPosition(result.Consumed));
                    if (result.Source is { } source)
                    {
                        connection.RemoteEndPoint = source;
                    }

                    await next(connection);
                    return;
                case ProxyProtocolV2.Status.NotProxyProtocol:
                    input.AdvanceTo(buffer.Start);
                    await next(connection);
                    return;
                case ProxyProtocolV2.Status.NeedMoreData when !read.IsCompleted:
                    input.AdvanceTo(buffer.Start, buffer.End);
                    continue;
                default:
                    _logger.LogWarning("Closed a connection from {RemoteEndPoint}: malformed PROXY protocol header", connection.RemoteEndPoint);
                    input.AdvanceTo(buffer.Start, buffer.End);
                    connection.Abort();
                    return;
            }
        }
    }
}

/// <summary>PROXY protocol settings of the agent port (<c>Gateway:ProxyProtocol</c>).</summary>
public sealed class ProxyProtocolOptions
{
    /// <summary>On behind the host Caddy (Compose); off for local development, where agents connect directly.</summary>
    public bool Enabled { get; set; }

    /// <summary>Networks (CIDR) whose connections may carry a PROXY protocol header: the address the host proxy arrives from.</summary>
    public string[] TrustedNetworks { get; set; } = [];

    /// <summary>Time a trusted connection has to deliver a complete header.</summary>
    public int HeaderTimeoutMilliseconds { get; set; } = 5000;
}
