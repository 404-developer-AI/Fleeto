namespace Fleeto.Infrastructure.Integrations;

/// <summary>
/// Paces outgoing requests to an external product that publishes no hard limit (0.4.0). A token bucket: it allows a short
/// burst up to <see cref="Capacity"/> and then one request per refill interval, and it makes a caller wait rather than
/// refusing it, because the caller is a background sync or an admin pressing a button, not a request to serve now.
///
/// Action1 counts every call of the whole API against one budget per enterprise and recommends staying under 30 a minute
/// (CLAUDE.md, Patch management). One budget lives per process, so the permits are split over the containers that call:
/// the workers poll, web makes the few calls an admin triggers.
/// </summary>
public sealed class RequestBudget
{
    private readonly object _lock = new();
    private readonly TimeProvider _time;
    private readonly double _refillPerSecond;
    private double _tokens;
    private DateTimeOffset _lastRefill;
    private DateTimeOffset _notBefore;

    /// <param name="permitsPerMinute">Requests a minute over time.</param>
    /// <param name="capacity">Requests that may go out at once after an idle period. Defaults to one minute's worth.</param>
    public RequestBudget(int permitsPerMinute, TimeProvider time, int? capacity = null)
    {
        Capacity = Math.Max(1, capacity ?? permitsPerMinute);
        _refillPerSecond = Math.Max(1, permitsPerMinute) / 60d;
        _time = time;
        _tokens = Capacity;
        _lastRefill = time.GetUtcNow();
        _notBefore = _lastRefill;
    }

    public int Capacity { get; }

    /// <summary>Tokens available right now, for tests and logging.</summary>
    public double Available
    {
        get
        {
            lock (_lock)
            {
                Refill();
                return _tokens;
            }
        }
    }

    /// <summary>Returns when one request may go out. Waits when the budget is empty; throws only on cancellation.</summary>
    public async Task AcquireAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            TimeSpan wait;
            lock (_lock)
            {
                Refill();
                var hold = _notBefore - _time.GetUtcNow();
                if (hold <= TimeSpan.Zero && _tokens >= 1)
                {
                    _tokens -= 1;
                    return;
                }

                // Whichever comes later: the pause the product asked for, or the next token.
                var untilToken = _tokens >= 1 ? TimeSpan.Zero : TimeSpan.FromSeconds((1 - _tokens) / _refillPerSecond);
                wait = hold > untilToken ? hold : untilToken;
            }

            await Task.Delay(wait, _time, cancellationToken);
        }
    }

    /// <summary>
    /// Holds every request for <paramref name="pause"/> after the product answered "too many requests": its own
    /// <c>retry_after</c> wins over what Fleeto thinks is left. When the pause has passed the normal rate applies again,
    /// so a pause is exactly as long as the product asked for and never longer.
    /// </summary>
    public void Pause(TimeSpan pause)
    {
        lock (_lock)
        {
            var until = _time.GetUtcNow() + (pause > TimeSpan.Zero ? pause : TimeSpan.Zero);
            if (until > _notBefore)
            {
                _notBefore = until;
            }
        }
    }

    private void Refill()
    {
        var now = _time.GetUtcNow();
        if (now <= _lastRefill)
        {
            return;
        }

        _tokens = Math.Min(Capacity, _tokens + (now - _lastRefill).TotalSeconds * _refillPerSecond);
        _lastRefill = now;
    }
}
