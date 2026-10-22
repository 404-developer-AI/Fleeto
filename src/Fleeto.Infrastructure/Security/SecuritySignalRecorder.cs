using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Infrastructure.Performance;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Fleeto.Infrastructure.Security;

/// <summary>
/// Counts what web and the gateway see of attacks (0.7.0, security monitor): per minute, per kind, per attacker network and
/// optional subject. Nothing here writes to the database: <see cref="SecuritySignalFlusher"/> takes the window once a minute and
/// writes it in one batch, so a flood of requests never becomes a flood of writes. A window holds at most
/// <see cref="MaxKeysPerMinute"/> distinct keys; beyond that new keys are dropped and counted, existing ones still count.
/// </summary>
public sealed class SecuritySignalRecorder
{
    public const int MaxKeysPerMinute = 5000;

    /// <summary>Longest subject kept; a longer one is cut.</summary>
    public const int MaxSubjectLength = 100;

    private readonly TimeProvider _time;
    private Window _window;

    public SecuritySignalRecorder(string component, TimeProvider time)
    {
        Component = component;
        _time = time;
        _window = new Window(PerfRecorder.MinuteOf(time.GetUtcNow().UtcDateTime));
    }

    /// <summary>A recorder that is never flushed: in the tool, and in tests that do not look at signals.</summary>
    public static SecuritySignalRecorder Disabled { get; } = new("none", TimeProvider.System);

    /// <summary>web or gateway.</summary>
    public string Component { get; }

    /// <summary>Counts one occurrence. An address that is not IPv4 or IPv6 (a test host, a Unix socket) is not counted.</summary>
    public void Record(SecuritySignalKind kind, IPAddress? address, string? subject = null)
    {
        if (address is null)
        {
            return;
        }

        address = IpNetworks.Normalize(address);
        if (address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
        {
            return;
        }

        var network = IpNetworks.AttackerNetwork(address);
        if (subject is { Length: > MaxSubjectLength })
        {
            subject = subject[..MaxSubjectLength];
        }

        Volatile.Read(ref _window).Add(new SignalKey(kind, network, subject));
    }

    /// <summary>Starts a new window for <paramref name="minute"/> and returns the one that ends, for the flusher.</summary>
    internal Window Swap(DateTime minute) => Interlocked.Exchange(ref _window, new Window(minute));

    /// <summary>The counts of the current minute, for tests.</summary>
    public IReadOnlyDictionary<(SecuritySignalKind Kind, string Network, string? Subject), int> Current() =>
        Volatile.Read(ref _window).Counts.ToDictionary(p => (p.Key.Kind, IpNetworks.Format(p.Key.Network), p.Key.Subject), p => p.Value);

    internal readonly record struct SignalKey(SecuritySignalKind Kind, IPNetwork Network, string? Subject);

    internal sealed class Window(DateTime minute)
    {
        private int _dropped;

        public DateTime Minute { get; } = minute;

        public ConcurrentDictionary<SignalKey, int> Counts { get; } = new();

        public int Dropped => Volatile.Read(ref _dropped);

        public void Add(SignalKey key)
        {
            if (Counts.TryGetValue(key, out _) || Counts.Count < MaxKeysPerMinute)
            {
                Counts.AddOrUpdate(key, 1, (_, count) => count + 1);
            }
            else
            {
                Interlocked.Increment(ref _dropped);
            }
        }
    }
}

/// <summary>
/// Writes the security signals of the last minute in one binary COPY (0.7.0, security monitor), on every minute boundary and
/// once more on shutdown. A failed write is dropped with a warning: signals are never worth retrying at the cost of the work
/// they describe, and the next minute counts again.
/// </summary>
public sealed class SecuritySignalFlusher : BackgroundService
{
    private static readonly TimeSpan SwapGrace = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(15);

    private readonly SecuritySignalRecorder _recorder;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly ILogger<SecuritySignalFlusher> _logger;
    private DateTime _lastWarning = DateTime.MinValue;

    public SecuritySignalFlusher(SecuritySignalRecorder recorder, NpgsqlDataSource dataSource, TimeProvider time, ILogger<SecuritySignalFlusher> logger)
    {
        _recorder = recorder;
        _dataSource = dataSource;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var next = PerfRecorder.MinuteOf(now).AddMinutes(1);
            try
            {
                await Task.Delay(next - now, _time, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            await FlushAsync(next, CancellationToken.None);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        await FlushAsync(PerfRecorder.MinuteOf(_time.GetUtcNow().UtcDateTime).AddMinutes(1), cancellationToken);
    }

    /// <summary>Swaps the window and writes the one that ends. <paramref name="nextMinute"/> is the minute of the new window.</summary>
    public async Task FlushAsync(DateTime nextMinute, CancellationToken cancellationToken)
    {
        var window = _recorder.Swap(nextMinute);
        await Task.Delay(SwapGrace, CancellationToken.None).ConfigureAwait(false);
        if (window.Counts.IsEmpty)
        {
            return;
        }

        try
        {
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var writer = await connection.BeginBinaryImportAsync("""
                COPY "SecuritySignals" ("Minute", "Component", "Kind", "Network", "Subject", "Count") FROM STDIN (FORMAT BINARY)
                """, cancellationToken);
            foreach (var (key, count) in window.Counts)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(window.Minute, NpgsqlDbType.TimestampTz, cancellationToken);
                await writer.WriteAsync(_recorder.Component, NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync(key.Kind.ToString(), NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync(IpNetworks.Format(key.Network), NpgsqlDbType.Varchar, cancellationToken);
                if (key.Subject is null)
                {
                    await writer.WriteNullAsync(cancellationToken);
                }
                else
                {
                    await writer.WriteAsync(key.Subject, NpgsqlDbType.Varchar, cancellationToken);
                }

                await writer.WriteAsync(count, NpgsqlDbType.Integer, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
            if (window.Dropped > 0)
            {
                _logger.LogWarning("{Dropped} security signals of {Minute:HH:mm} were not stored: more than {Max} addresses in one minute",
                    window.Dropped, window.Minute, SecuritySignalRecorder.MaxKeysPerMinute);
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            var now = _time.GetUtcNow().UtcDateTime;
            if (now - _lastWarning >= WarningInterval)
            {
                _lastWarning = now;
                _logger.LogWarning(ex, "The security signals of {Minute:HH:mm} could not be written and were dropped", window.Minute);
            }
        }
    }
}

public static class SecurityMonitorRegistration
{
    /// <summary>
    /// What web and the gateway need of the security monitor (0.7.0): the signal recorder with its flusher, and the list of
    /// blocked and allowed addresses. The other components get the disabled recorder and no list.
    /// </summary>
    public static IServiceCollection AddFleetoSecurityMonitor(this IServiceCollection services, FleetoComponent component)
    {
        if (component is not (FleetoComponent.Web or FleetoComponent.Gateway))
        {
            services.AddSingleton(SecuritySignalRecorder.Disabled);
            return services;
        }

        services.AddSingleton(sp => new SecuritySignalRecorder(component.PerfName(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<SecuritySignalFlusher>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<SecuritySignalFlusher>());
        services.AddSingleton<IpBlockList>();
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<IpBlockList>());
        return services;
    }
}
