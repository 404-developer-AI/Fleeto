using System.Net;
using Fleeto.Core.Entities;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure.Security;
using Fleeto.Testing;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// The security monitor on the agent port (0.7.0): an address an admin blocked is dropped before TLS, an automatically blocked
/// one is not (an endpoint with a valid certificate may share it), and a connection that never completes its TLS handshake
/// counts as a signal while one that does counts nothing. Enrollment, recovery and the CA refuse any block.
/// </summary>
public sealed class AgentConnectionGuardTests
{
    private static (AgentConnectionGuard Guard, SecuritySignalRecorder Signals) NewGuard()
    {
        var list = new IpBlockList(null!, new InMemoryNotificationBus(), TimeProvider.System, NullLogger<IpBlockList>.Instance);
        list.Load([("81.2.69.1/32", IpBlockSource.Manual, null), ("81.2.69.2/32", IpBlockSource.Automatic, null)], []);
        var signals = new SecuritySignalRecorder("gateway", new FakeTimeProvider(DateTimeOffset.UtcNow));
        var options = Options.Create(new GatewayOptions());
        var proxy = new ProxyProtocolMiddleware(options, NullLogger<ProxyProtocolMiddleware>.Instance);
        return (new AgentConnectionGuard(list, signals, proxy, options), signals);
    }

    private static DefaultConnectionContext Connection(string address) =>
        new() { RemoteEndPoint = new IPEndPoint(IPAddress.Parse(address), 50000) };

    [Fact]
    public async Task A_manually_blocked_address_is_dropped_before_tls()
    {
        var (guard, signals) = NewGuard();
        var reached = false;
        await guard.OnConnectionAsync(Connection("81.2.69.1"), _ =>
        {
            reached = true;
            return Task.CompletedTask;
        });

        Assert.False(reached);
        Assert.Equal(1, signals.Current()[(SecuritySignalKind.Blocked, "81.2.69.1/32", null)]);
    }

    [Fact]
    public async Task An_automatically_blocked_address_reaches_tls_and_a_completed_handshake_counts_nothing()
    {
        var (guard, signals) = NewGuard();
        var reached = false;
        await guard.OnConnectionAsync(Connection("81.2.69.2"), connection =>
        {
            reached = true;
            return guard.OnSecuredAsync(connection, _ => Task.CompletedTask);
        });

        Assert.True(reached);
        Assert.Empty(signals.Current());
        Assert.True(guard.IsBlocked(IPAddress.Parse("81.2.69.2")));
        Assert.False(guard.IsBlocked(IPAddress.Parse("81.2.69.3")));
    }

    [Fact]
    public async Task A_handshake_that_never_completes_counts_as_a_signal()
    {
        var (guard, signals) = NewGuard();
        await guard.OnConnectionAsync(Connection("81.2.69.9"), _ => Task.CompletedTask);
        Assert.Equal(1, signals.Current()[(SecuritySignalKind.TlsHandshakeFailed, "81.2.69.9/32", null)]);
    }
}
