using Fleeto.Core.Entities;

namespace Fleeto.Signer.Processing;

/// <summary>
/// In-memory sliding one-minute rate limits per request kind. A last line of defence against a compromised or
/// misbehaving requester flooding the signer; per process, so two signers together allow twice the rate.
/// </summary>
public sealed class SigningRateLimiter
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    public static readonly IReadOnlyDictionary<SigningRequestKind, int> DefaultLimits = new Dictionary<SigningRequestKind, int>
    {
        [SigningRequestKind.AgentEnrollment] = 600,
        [SigningRequestKind.AgentRenewal] = 600,
        [SigningRequestKind.AgentRecovery] = 600,
        [SigningRequestKind.WatchdogCertificate] = 600,
        [SigningRequestKind.Job] = 3000,
        [SigningRequestKind.GatewayCertificate] = 10,
        [SigningRequestKind.AgentConfig] = 20_000
    };

    private readonly IReadOnlyDictionary<SigningRequestKind, int> _limits;
    private readonly Dictionary<SigningRequestKind, Queue<DateTime>> _events = [];
    private readonly Lock _lock = new();

    public SigningRateLimiter()
        : this(DefaultLimits)
    {
    }

    public SigningRateLimiter(IReadOnlyDictionary<SigningRequestKind, int> limits)
    {
        _limits = limits;
    }

    /// <summary>Takes one slot for <paramref name="kind"/> when the last minute has room. Unknown kinds get no slots.</summary>
    public bool TryAcquire(SigningRequestKind kind, DateTime now)
    {
        if (!_limits.TryGetValue(kind, out var limit))
        {
            return false;
        }

        lock (_lock)
        {
            if (!_events.TryGetValue(kind, out var queue))
            {
                queue = new Queue<DateTime>();
                _events[kind] = queue;
            }

            while (queue.Count > 0 && now - queue.Peek() >= Window)
            {
                queue.Dequeue();
            }

            if (queue.Count >= limit)
            {
                return false;
            }

            queue.Enqueue(now);
            return true;
        }
    }
}
