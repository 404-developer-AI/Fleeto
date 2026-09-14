using Fleetify.Gateway.Data;
using Npgsql;

namespace Fleetify.Gateway.Sessions;

/// <summary>
/// Writes LastSeenAt for every endpoint that sent something since the previous flush, every 10 seconds in one statement.
/// A heartbeat only touches memory, so 10,000 agents cost one UPDATE per flush instead of hundreds per second.
/// </summary>
public sealed class LastSeenFlusher : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private readonly AgentSessionManager _sessions;
    private readonly GatewayStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<LastSeenFlusher> _logger;
    private readonly List<Guid> _ids = [];
    private readonly List<DateTime> _times = [];
    private readonly List<(AgentSession Session, long Ticks)> _pending = [];

    public LastSeenFlusher(AgentSessionManager sessions, GatewayStore store, TimeProvider time, ILogger<LastSeenFlusher> logger)
    {
        _sessions = sessions;
        _store = store;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(Interval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            await FlushAsync(stoppingToken);
        }
    }

    /// <summary>Flushes once. Failures are logged; the values stay unflushed and go out with the next flush.</summary>
    internal async Task<int> FlushAsync(CancellationToken cancellationToken)
    {
        _ids.Clear();
        _times.Clear();
        _pending.Clear();
        foreach (var session in _sessions.Sessions)
        {
            if (session.TryGetUnflushedLastSeen(out var ticks))
            {
                _ids.Add(session.EndpointId);
                _times.Add(new DateTime(ticks, DateTimeKind.Utc));
                _pending.Add((session, ticks));
            }
        }

        if (_ids.Count == 0)
        {
            return 0;
        }

        try
        {
            await _store.FlushLastSeenAsync([.. _ids], [.. _times], cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Could not write last-seen times for {Count} endpoints; retrying with the next flush", _ids.Count);
            return 0;
        }

        foreach (var (session, ticks) in _pending)
        {
            session.MarkFlushed(ticks);
        }

        return _ids.Count;
    }
}
