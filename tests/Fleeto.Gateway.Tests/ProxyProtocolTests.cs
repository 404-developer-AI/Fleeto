using System.Buffers;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using Fleeto.Gateway.Tls;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Guarantees that the gateway reads the PROXY protocol v2 header of the host proxy correctly and safely: the agent address
/// comes from the header only for a trusted proxy, reaches the per-address rate limit, a malformed header closes the
/// connection, and a connection without a header still works.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class ProxyProtocolTests
{
    private static readonly byte[] Signature = [0x0D, 0x0A, 0x0D, 0x0A, 0x00, 0x0D, 0x0A, 0x51, 0x55, 0x49, 0x54, 0x0A];

    private readonly GatewayFixture _fixture;

    public ProxyProtocolTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    private static byte[] Header(string source, ushort sourcePort = 50000, byte command = 0x21, byte? family = null, byte[]? tlv = null)
    {
        var address = IPAddress.Parse(source);
        var v4 = address.AddressFamily == AddressFamily.InterNetwork;
        var destination = v4 ? IPAddress.Parse("198.51.100.1") : IPAddress.Parse("2001:db8::1");
        var body = new List<byte>();
        body.AddRange(address.GetAddressBytes());
        body.AddRange(destination.GetAddressBytes());
        body.Add((byte)(sourcePort >> 8));
        body.Add((byte)sourcePort);
        body.Add(0x01);
        body.Add(0xBB);
        body.AddRange(tlv ?? []);

        var header = new List<byte>(Signature) { command, family ?? (byte)(v4 ? 0x11 : 0x21), (byte)(body.Count >> 8), (byte)body.Count };
        header.AddRange(body);
        return header.ToArray();
    }

    private static ProxyProtocolV2.Result Parse(byte[] bytes) => ProxyProtocolV2.Parse(new ReadOnlySequence<byte>(bytes));

    [Fact]
    public void Parses_ipv4_and_ipv6_sources_and_skips_tlvs()
    {
        var v4 = Parse([.. Header("203.0.113.7", 40000, tlv: [0x05, 0x00, 0x02, 0xAA, 0xBB]), 0x16, 0x03]);
        Assert.Equal(ProxyProtocolV2.Status.Parsed, v4.Status);
        Assert.Equal(new IPEndPoint(IPAddress.Parse("203.0.113.7"), 40000), v4.Source);
        Assert.Equal(16 + 12 + 5, v4.Consumed);

        var v6 = Parse(Header("2001:db8::7"));
        Assert.Equal(ProxyProtocolV2.Status.Parsed, v6.Status);
        Assert.Equal(IPAddress.Parse("2001:db8::7"), v6.Source!.Address);

        var mapped = Parse(Header("::ffff:203.0.113.9"));
        Assert.Equal(IPAddress.Parse("203.0.113.9"), mapped.Source!.Address);
    }

    [Fact]
    public void Local_command_and_unsupported_families_keep_the_connection_address()
    {
        var local = Parse(Header("203.0.113.7", command: 0x20));
        Assert.Equal(ProxyProtocolV2.Status.Parsed, local.Status);
        Assert.Null(local.Source);

        var udp = Parse(Header("203.0.113.7", family: 0x12));
        Assert.Equal(ProxyProtocolV2.Status.Parsed, udp.Status);
        Assert.Null(udp.Source);
    }

    [Fact]
    public void Tls_and_partial_input_are_recognised()
    {
        Assert.Equal(ProxyProtocolV2.Status.NotProxyProtocol, Parse([0x16, 0x03, 0x01, 0x00]).Status);
        Assert.Equal(ProxyProtocolV2.Status.NotProxyProtocol, Parse(Encoding.ASCII.GetBytes("PROXY TCP4 1.2.3.4 5.6.7.8 1 2\r\n")).Status);

        var full = Header("203.0.113.7");
        Assert.Equal(ProxyProtocolV2.Status.NeedMoreData, Parse(full[..5]).Status);
        Assert.Equal(ProxyProtocolV2.Status.NeedMoreData, Parse(full[..20]).Status);
        Assert.Equal(ProxyProtocolV2.Status.NeedMoreData, Parse([]).Status);
    }

    [Fact]
    public void Malformed_headers_are_invalid()
    {
        var wrongVersion = Header("203.0.113.7");
        wrongVersion[12] = 0x11;
        Assert.Equal(ProxyProtocolV2.Status.Invalid, Parse(wrongVersion).Status);

        var wrongCommand = Header("203.0.113.7");
        wrongCommand[12] = 0x2F;
        Assert.Equal(ProxyProtocolV2.Status.Invalid, Parse(wrongCommand).Status);

        var tooShort = Header("203.0.113.7");
        tooShort[14] = 0;
        tooShort[15] = 4;
        Assert.Equal(ProxyProtocolV2.Status.Invalid, Parse(tooShort).Status);

        byte[] tooLong = [.. Signature, 0x21, 0x11, 0xFF, 0xFF];
        Assert.Equal(ProxyProtocolV2.Status.Invalid, Parse(tooLong).Status);
    }

    [Fact]
    public void Trusted_networks_are_parsed_and_matched()
    {
        Assert.Throws<InvalidOperationException>(() => ProxyProtocolMiddleware.ParseNetworks(["not-an-address"]));
        Assert.Throws<InvalidOperationException>(() => ProxyProtocolMiddleware.ParseNetworks(["10.0.0.0/33"]));
        Assert.Equal(2, ProxyProtocolMiddleware.ParseNetworks(["172.16.0.0/12", " ", "::1"]).Count);
    }

    [Fact]
    public async Task The_host_uses_the_header_address_of_a_trusted_proxy_and_ignores_it_otherwise()
    {
        await using var signer = new FakeSigner(_fixture);
        var (trustedApp, trustedPort, trustedHealth) = await StartAsync("127.0.0.0/8");
        var (untrustedApp, untrustedPort, untrustedHealth) = await StartAsync("10.0.0.0/8");
        try
        {
            using var plain = new HttpClient();
            await WaitForHealthyAsync(plain, trustedHealth);
            await WaitForHealthyAsync(plain, untrustedHealth);

            // One request per address per minute: the rate limit partitions on the address from the header.
            Assert.Equal(200, await GetCaStatusAsync(trustedPort, Header("203.0.113.10")));
            Assert.Equal(429, await GetCaStatusAsync(trustedPort, Header("203.0.113.10")));
            Assert.Equal(200, await GetCaStatusAsync(trustedPort, Header("203.0.113.11")));
            // A trusted connection without a header keeps working.
            Assert.Equal(200, await GetCaStatusAsync(trustedPort, null));
            // A malformed header closes the connection.
            var malformed = Header("203.0.113.12");
            malformed[12] = 0x2F;
            Assert.Null(await GetCaStatusAsync(trustedPort, malformed));

            // From an untrusted address the header is not read, so TLS fails on it.
            Assert.Null(await GetCaStatusAsync(untrustedPort, Header("203.0.113.13")));
            Assert.Equal(200, await GetCaStatusAsync(untrustedPort, null));
        }
        finally
        {
            await trustedApp.StopAsync();
            await untrustedApp.StopAsync();
            await trustedApp.DisposeAsync();
            await untrustedApp.DisposeAsync();
        }
    }

    private async Task<(WebApplication App, int AgentPort, int HealthPort)> StartAsync(string trustedNetwork)
    {
        var agentPort = FreePort();
        var healthPort = FreePort();
        var app = GatewayApplication.Build(
            [
                "--Gateway:ListenAddress=127.0.0.1",
                $"--Gateway:AgentPort={agentPort}",
                $"--Gateway:HealthPort={healthPort}",
                $"--Gateway:RelayPort={FreePort()}",
                "--Gateway:EnrollmentsPerMinutePerAddress=1",
                "--Gateway:ProxyProtocol:Enabled=true",
                $"--Gateway:ProxyProtocol:TrustedNetworks:0={trustedNetwork}",
                "--Logging:LogLevel:Default=Error"
            ],
            builder =>
            {
                builder.Services.AddSingleton(_fixture.Database.DataSource);
                builder.Logging.SetMinimumLevel(LogLevel.Error);
            });
        await app.StartAsync();
        return (app, agentPort, healthPort);
    }

    /// <summary>GET /v1/ca over TLS, optionally after a PROXY header. Returns the status code, or null when the connection failed.</summary>
    private static async Task<int?> GetCaStatusAsync(int port, byte[]? header)
    {
        try
        {
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            var network = tcp.GetStream();
            if (header is not null)
            {
                await network.WriteAsync(header);
            }

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            // Test only: the server certificate is not what is being tested here.
            await using var tls = new SslStream(network, false, (_, _, _, _) => true);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = "localhost" }, timeout.Token);
            await tls.WriteAsync(Encoding.ASCII.GetBytes("GET /v1/ca HTTP/1.1\r\nHost: localhost\r\nConnection: close\r\n\r\n"), timeout.Token);
            var buffer = new byte[64];
            var read = await tls.ReadAsync(buffer, timeout.Token);
            var statusLine = Encoding.ASCII.GetString(buffer, 0, read);
            return int.Parse(statusLine.Split(' ')[1], System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or System.Security.Authentication.AuthenticationException or SocketException or OperationCanceledException)
        {
            return null;
        }
    }

    private static async Task WaitForHealthyAsync(HttpClient client, int port)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var response = await client.GetAsync($"http://127.0.0.1:{port}/health");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(200);
        }

        Assert.Fail("The gateway did not become healthy.");
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
