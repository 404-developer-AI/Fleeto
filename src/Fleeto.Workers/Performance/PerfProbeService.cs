using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Performance;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fleeto.Workers.Performance;

/// <summary>
/// Runs the edge checks and benchmarks admins ask for on Settings, Performance (0.7.0 step 3). Web may not reach the internet, so it
/// writes the request and notifies; this loop claims it, runs it and writes the result, one at a time. A request no loop picked up
/// within 10 minutes expires with that reason, and probes are kept 30 days.
/// </summary>
public sealed class PerfProbeService : WorkerLoop
{
    public static readonly TimeSpan Expiry = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly PerfEdgeCheck _edge;
    private readonly PerfBenchmark _benchmark;
    private IDisposable? _subscription;

    public PerfProbeService(IFleetoDbContextFactory dbFactory, INotificationBus bus, NpgsqlDataSource dataSource, IOptions<BackupOptions> backup,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<PerfProbeService> logger, PerfEdgeCheck? edge = null)
        : base("perf-probes", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _edge = edge ?? new PerfEdgeCheck();
        var directory = string.IsNullOrWhiteSpace(backup.Value.TempDirectory) ? Path.GetTempPath() : backup.Value.TempDirectory;
        _benchmark = new PerfBenchmark(dataSource, directory);
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.PerformanceProbes, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override Task<bool> RunOnceAsync(CancellationToken cancellationToken) => ProcessAsync(cancellationToken);

    /// <summary>
    /// Expires what waited too long, removes old probes, and runs the oldest waiting one. Returns true when one ran, so the loop looks
    /// for the next at once.
    /// </summary>
    public async Task<bool> ProcessAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        var expired = now - Expiry;
        await db.PerfProbes.Where(p => p.CompletedAt == null && p.StartedAt == null && p.RequestedAt < expired)
            .ExecuteUpdateAsync(p => p
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.Problem, "The workers did not run it within 10 minutes. Ask again; if it expires again, check that the workers run."), cancellationToken);
        await db.PerfProbes.Where(p => p.CompletedAt == null && p.StartedAt != null && p.StartedAt < expired)
            .ExecuteUpdateAsync(p => p
                .SetProperty(x => x.CompletedAt, now)
                .SetProperty(x => x.Problem, "It stopped before it finished, probably because the workers restarted. Ask again."), cancellationToken);
        var old = now - Retention;
        await db.PerfProbes.Where(p => p.RequestedAt < old).ExecuteDeleteAsync(cancellationToken);

        var next = await db.PerfProbes.AsNoTracking().Where(p => p.CompletedAt == null && p.StartedAt == null)
            .OrderBy(p => p.RequestedAt).Select(p => new { p.Id, p.Kind }).FirstOrDefaultAsync(cancellationToken);
        if (next is null)
        {
            return false;
        }

        // Claimed by the update itself, so two passes never run the same probe.
        var claimed = await db.PerfProbes.Where(p => p.Id == next.Id && p.StartedAt == null)
            .ExecuteUpdateAsync(p => p.SetProperty(x => x.StartedAt, now), cancellationToken);
        if (claimed == 0)
        {
            return true;
        }

        IReadOnlyList<PerfProbeItem> items = [];
        string? problem = null;
        try
        {
            items = next.Kind == PerfProbeKind.EdgeCheck
                ? await RunEdgeCheckAsync(db, cancellationToken)
                : await _benchmark.RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            problem = Problem(next.Kind, ex);
            Logger.LogWarning("The {Kind} of the performance monitor failed: {Error}", next.Kind, ex.GetType().Name);
        }

        var json = PerformanceReader.SerializeItems(items);
        var completed = Time.GetUtcNow().UtcDateTime;
        await db.PerfProbes.Where(p => p.Id == next.Id)
            .ExecuteUpdateAsync(p => p
                .SetProperty(x => x.CompletedAt, completed)
                .SetProperty(x => x.ItemsJson, json)
                .SetProperty(x => x.Problem, problem), cancellationToken);
        return true;
    }

    private async Task<IReadOnlyList<PerfProbeItem>> RunEdgeCheckAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        if (string.IsNullOrWhiteSpace(instance.Fqdn))
        {
            throw new InvalidOperationException("The instance has no FQDN.");
        }

        return await _edge.RunAsync(instance.Fqdn, cancellationToken);
    }

    /// <summary>Why a probe failed, in words for the page: the cause and the next step, never an address or a message of the exception.</summary>
    public static string Problem(PerfProbeKind kind, Exception ex)
    {
        var text = (kind, ex) switch
        {
            (PerfProbeKind.EdgeCheck, InvalidOperationException) => "The instance has no FQDN yet, so there is no URL to check.",
            (PerfProbeKind.EdgeCheck, System.Net.Sockets.SocketException) =>
                "The instance URL could not be reached from the VPS: its name did not resolve or port 443 did not answer. Check DNS and the host Caddy.",
            (PerfProbeKind.EdgeCheck, System.Security.Authentication.AuthenticationException) =>
                "The TLS handshake with the instance URL failed. Check the certificate of the host Caddy for the FQDN.",
            (PerfProbeKind.EdgeCheck, HttpRequestException) => "The instance URL did not answer over HTTPS. Check the host Caddy and web.",
            (PerfProbeKind.EdgeCheck, OperationCanceledException or TimeoutException) => "The instance URL did not answer within 10 seconds. Check the host Caddy and web.",
            (PerfProbeKind.Benchmark, NpgsqlException) => "The database did not answer the benchmark queries. Check that it runs.",
            _ => $"It failed with {ex.GetType().Name}. The workers log has the details."
        };
        return text.Length <= PerfProbe.MaxProblemLength ? text : text[..PerfProbe.MaxProblemLength];
    }
}
