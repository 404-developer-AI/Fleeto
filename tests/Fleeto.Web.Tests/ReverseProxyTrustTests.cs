using System.Net;
using System.Text.RegularExpressions;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees that web, configured as src/Fleeto.Web/appsettings.json and deploy/compose/compose.yml deploy it, takes the client
/// address and scheme from X-Forwarded-* only from the gateway address of the instance's edge network (the host Caddy), so no other
/// container or private range on the VPS can escape a block of its own address or have its signals counted against another; and that
/// X-Forwarded-Host is never taken.
/// </summary>
public sealed class ReverseProxyTrustTests
{
    private const string EdgeGateway = "10.211.7.1";

    private static string RepoFile(params string[] parts)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "compose", "compose.yml")))
        {
            dir = dir.Parent;
        }

        return Path.Combine([dir!.FullName, .. parts]);
    }

    /// <summary>appsettings.json with the ReverseProxy__ variables of compose.yml on top, EDGE_GATEWAY as install.sh sets it.</summary>
    private static IConfiguration DeployedConfiguration()
    {
        var compose = File.ReadAllText(RepoFile("deploy", "compose", "compose.yml"));
        var environment = Regex.Matches(compose.Replace("\r", ""), @"^\s*(ReverseProxy__\w+):[ \t]*(.+?)[ \t]*$", RegexOptions.Multiline)
            .ToDictionary(m => m.Groups[1].Value.Replace("__", ":", StringComparison.Ordinal),
                m => (string?)Regex.Replace(m.Groups[2].Value.Trim('"'), @"\$\{EDGE_GATEWAY[^}]*\}", EdgeGateway));
        Assert.Equal(EdgeGateway + "/32", environment["ReverseProxy:KnownNetworks:0"]);
        Assert.DoesNotContain(environment.Keys, k => k.StartsWith("ReverseProxy:KnownNetworks:", StringComparison.Ordinal) && k != "ReverseProxy:KnownNetworks:0");

        var configuration = new ConfigurationBuilder()
            .AddJsonFile(RepoFile("src", "Fleeto.Web", "appsettings.json"))
            .AddInMemoryCollection(environment)
            .Build();
        Assert.Equal([EdgeGateway + "/32"], configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>()!);
        return configuration;
    }

    private static async Task<(WebApplication App, SecuritySignalRecorder Signals)> StartAsync(string peer)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var list = new IpBlockList(null!, new Testing.InMemoryNotificationBus(), TimeProvider.System, NullLogger<IpBlockList>.Instance);
        // The peer's own public address is blocked; it must not be able to talk its way out.
        list.Load([("81.2.69.50/32", IpBlockSource.Manual, null)], []);
        var signals = new SecuritySignalRecorder("web", new FakeTimeProvider(DateTimeOffset.UtcNow));
        builder.Services.AddSingleton(list);
        builder.Services.AddSingleton(signals);
        builder.Services.AddRouting();
        var app = builder.Build();
        // The TCP peer of the connection.
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
            return next(context);
        });

        // As Program.cs does with ReverseProxy:Enabled.
        app.UseForwardedHeaders(ReverseProxyHeaders.Options(DeployedConfiguration()));
        app.UseMiddleware<IpBlockMiddleware>();
        app.UseRouting();
        app.UseMiddleware<ScannerDetectionMiddleware>();
        app.MapGet("/whoami", (HttpContext c) => $"{c.Connection.RemoteIpAddress}|{c.Request.Host}|{c.Request.Scheme}");
        await app.StartAsync();
        return (app, signals);
    }

    private static HttpRequestMessage Forwarded(string path, string address)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Forwarded-For", address);
        request.Headers.Add("X-Forwarded-Host", "spoofed.example");
        request.Headers.Add("X-Forwarded-Proto", "https");
        return request;
    }

    [Fact]
    public async Task The_host_proxy_sets_the_client_address_and_scheme_but_never_the_host()
    {
        var (app, signals) = await StartAsync(EdgeGateway);
        await using (app)
        {
            var client = app.GetTestClient();
            var response = await client.SendAsync(Forwarded("/whoami", "81.2.70.7"));
            Assert.Equal("81.2.70.7|localhost|https", await response.Content.ReadAsStringAsync());

            // A blocked client behind the proxy is refused by its own address.
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(Forwarded("/whoami", "81.2.69.50"))).StatusCode);

            await client.SendAsync(Forwarded("/.env", "81.2.70.8"));
            Assert.Equal(1, signals.Current()[(SecuritySignalKind.SensitivePath, "81.2.70.8/32", null)]);
        }
    }

    [Theory]
    // Another container of the instance, other Docker networks and private ranges of the VPS.
    [InlineData("10.211.7.3")]
    [InlineData("192.168.1.50")]
    [InlineData("10.210.99.5")]
    [InlineData("172.20.0.9")]
    public async Task X_Forwarded_headers_from_any_other_peer_are_ignored(string peer)
    {
        var (app, signals) = await StartAsync(peer);
        await using (app)
        {
            var client = app.GetTestClient();
            var response = await client.SendAsync(Forwarded("/whoami", "81.2.70.7"));
            Assert.Equal($"{peer}|localhost|http", await response.Content.ReadAsStringAsync());

            // A probe counts against the peer itself, not an address it chose.
            await client.SendAsync(Forwarded("/.env", "81.2.70.8"));
            Assert.Equal(1, signals.Current()[(SecuritySignalKind.SensitivePath, $"{peer}/32", null)]);
            Assert.DoesNotContain(signals.Current().Keys, k => k.Network == "81.2.70.8/32");
        }
    }

    [Fact]
    public async Task A_blocked_public_peer_is_refused_whatever_it_forwards()
    {
        var (app, _) = await StartAsync("81.2.69.50");
        await using (app)
        {
            Assert.Equal(HttpStatusCode.Forbidden, (await app.GetTestClient().SendAsync(Forwarded("/whoami", "81.2.70.7"))).StatusCode);
        }
    }
}
