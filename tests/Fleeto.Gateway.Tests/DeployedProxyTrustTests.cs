using System.IO.Pipelines;
using System.Net;
using System.Text.RegularExpressions;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure.Security;
using Fleeto.Testing;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Guarantees that the gateway, as deploy/compose/compose.yml configures it, takes the client address from a PROXY v2 header
/// (agent port) or X-Forwarded-For (relay port) only from the gateway address of the instance's edge network, the address the host
/// Caddy arrives from, and from no other container or private range on the VPS.
/// </summary>
public sealed class DeployedProxyTrustTests
{
    private const string EdgeGateway = "10.211.7.1";

    private static readonly byte[] Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "compose", "compose.yml")))
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir!.FullName, .. parts]);
    }

    /// <summary>The Gateway__ProxyProtocol__TrustedNetworks__N values of compose.yml, with EDGE_GATEWAY as install.sh would set it.</summary>
    private static string[] ComposeTrustedNetworks()
    {
        var compose = File.ReadAllText(RepoFile("deploy", "compose", "compose.yml"));
        var networks = Regex.Matches(compose.Replace("\r", ""), @"^\s*Gateway__ProxyProtocol__TrustedNetworks__\d+:[ \t]*(.+?)[ \t]*$", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(["${EDGE_GATEWAY:?EDGE_GATEWAY must be set in instance.conf}/32"], networks);
        return [.. networks.Select(n => Regex.Replace(n, @"\$\{EDGE_GATEWAY[^}]*\}", EdgeGateway))];
    }

    private static IOptions<GatewayOptions> Options() => Microsoft.Extensions.Options.Options.Create(new GatewayOptions
    {
        RelayPort = 8082,
        ProxyProtocol = new ProxyProtocolOptions { Enabled = true, TrustedNetworks = ComposeTrustedNetworks() }
    });

    private static byte[] Header(string source)
    {
        var body = new List<byte>();
        body.AddRange(IPAddress.Parse(source).GetAddressBytes());
        body.AddRange(IPAddress.Parse("198.51.100.1").GetAddressBytes());
        body.AddRange([0xC3, 0x50, 0x01, 0xBB]);
        var header = new List<byte>(Signature) { 0x21, 0x11, 0, (byte)body.Count };
        header.AddRange(body);
        return [.. header, 0x16, 0x03, 0x01];
    }

    private sealed class Duplex(PipeReader input, PipeWriter output) : IDuplexPipe
    {
        public PipeReader Input { get; } = input;
        public PipeWriter Output { get; } = output;
    }

    private static async Task<EndPoint?> RemoteAfterProxyAsync(string peer, string headerSource)
    {
        var middleware = new ProxyProtocolMiddleware(Options(), NullLogger<ProxyProtocolMiddleware>.Instance);
        var pipe = new Pipe();
        await pipe.Writer.WriteAsync(Header(headerSource));
        var connection = new DefaultConnectionContext
        {
            RemoteEndPoint = new IPEndPoint(IPAddress.Parse(peer), 40000),
            Transport = new Duplex(pipe.Reader, new Pipe().Writer)
        };
        EndPoint? seen = null;
        await middleware.OnConnectionAsync(connection, c =>
        {
            seen = c.RemoteEndPoint;
            return Task.CompletedTask;
        });
        return seen;
    }

    [Fact]
    public async Task Proxy_v2_header_from_the_edge_gateway_sets_the_address()
    {
        var seen = await RemoteAfterProxyAsync(EdgeGateway, "81.2.70.9");
        Assert.Equal(new IPEndPoint(IPAddress.Parse("81.2.70.9"), 50000), seen);
    }

    [Theory]
    // Another container of the instance on the edge network, other Docker networks, private ranges and a public peer.
    [InlineData("10.211.7.3")]
    [InlineData("10.210.99.5")]
    [InlineData("192.168.1.50")]
    [InlineData("172.31.200.200")]
    [InlineData("81.2.69.50")]
    public async Task Proxy_v2_header_from_any_other_peer_is_not_trusted(string peer)
    {
        var middleware = new ProxyProtocolMiddleware(Options(), NullLogger<ProxyProtocolMiddleware>.Instance);
        Assert.False(middleware.IsTrusted(new IPEndPoint(IPAddress.Parse(peer), 40000)));
        var seen = await RemoteAfterProxyAsync(peer, "81.2.70.9");
        Assert.NotEqual(new IPEndPoint(IPAddress.Parse("81.2.70.9"), 50000), seen);
    }

    [Theory]
    [InlineData(EdgeGateway, "81.2.70.10")]
    [InlineData("10.211.7.3", "10.211.7.3")]
    [InlineData("10.210.42.17", "10.210.42.17")]
    [InlineData("192.168.1.50", "192.168.1.50")]
    public void Relay_port_takes_X_Forwarded_For_only_from_the_edge_gateway(string peer, string expected)
    {
        var options = Options();
        var list = new IpBlockList(null!, new InMemoryNotificationBus(), TimeProvider.System, NullLogger<IpBlockList>.Instance);
        list.Load([], []);
        var guard = new AgentConnectionGuard(list, new SecuritySignalRecorder("gateway", new FakeTimeProvider(DateTimeOffset.UtcNow)),
            new ProxyProtocolMiddleware(options, NullLogger<ProxyProtocolMiddleware>.Instance), options);

        var context = new DefaultHttpContext();
        context.Connection.LocalPort = 8082;
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = "81.2.70.10";
        Assert.Equal(IPAddress.Parse(expected), guard.ClientAddress(context));

        // Nobody but the host proxy can make itself unblockable by claiming a reserved address.
        context.Request.Headers["X-Forwarded-For"] = "10.1.2.3";
        Assert.Equal(peer == EdgeGateway ? IPAddress.Parse("10.1.2.3") : IPAddress.Parse(peer), guard.ClientAddress(context));
    }
}
