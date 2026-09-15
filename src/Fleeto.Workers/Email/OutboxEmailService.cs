using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Workers.Common;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleeto.Workers.Email;

/// <summary>
/// Delivers the email outbox. Retries with backoff (1 min, 5 min, 15 min, 1 h, then every 6 h) and gives up after
/// <see cref="MaxAttempts"/> attempts, keeping the last error. After consecutive delivery failures a circuit breaker
/// pauses delivery, so a dead mail server is not hammered. Woken by <c>fleeto_outbox_emails</c>, polls every 30 seconds.
/// <para>
/// A pass claims its emails by moving their next attempt ten minutes ahead before sending, so a second workers process
/// or a crash mid-pass cannot send the same email twice within that window.
/// </para>
/// </summary>
public sealed class OutboxEmailService : WorkerLoop
{
    public const int MaxAttempts = 10;
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan NotConfiguredLogInterval = TimeSpan.FromHours(1);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly IEmailTransportFactory _transports;
    private readonly EmailOptions _options;
    private readonly CircuitBreaker _breaker;
    private DateTimeOffset _lastNotConfiguredLog = DateTimeOffset.MinValue;
    private IDisposable? _subscription;

    public OutboxEmailService(IFleetoDbContextFactory dbFactory, INotificationBus bus, IEmailTransportFactory transports,
        IOptions<EmailOptions> options, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<OutboxEmailService> logger)
        : base("outbox-email", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _transports = transports;
        _options = options.Value;
        _breaker = new CircuitBreaker(_options.CircuitBreakerFailures, TimeSpan.FromMinutes(_options.CircuitBreakerPauseMinutes), time);
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override TimeSpan MaxRunDuration => TimeSpan.FromMinutes(30);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.OutboxEmails, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        await DeliverPendingAsync(cancellationToken) >= Math.Max(1, _options.BatchSize) && !_breaker.IsOpen;

    /// <summary>Delay before the next attempt after <paramref name="attempts"/> failed attempts.</summary>
    public static TimeSpan RetryDelay(int attempts) => attempts switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        3 => TimeSpan.FromMinutes(15),
        4 => TimeSpan.FromHours(1),
        _ => TimeSpan.FromHours(6)
    };

    /// <summary>Attempts one batch of due emails. Returns the number attempted.</summary>
    public async Task<int> DeliverPendingAsync(CancellationToken cancellationToken)
    {
        if (_breaker.IsOpen)
        {
            return 0;
        }

        var batchSize = Math.Max(1, _options.BatchSize);
        var now = Time.GetUtcNow().UtcDateTime;

        await using var db = _dbFactory.CreateSystem();
        var dueIds = await db.OutboxEmails.AsNoTracking()
            .Where(e => e.SentAt == null && e.Attempts < MaxAttempts && e.NextAttemptAt <= now)
            .OrderBy(e => e.NextAttemptAt)
            .Select(e => e.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (dueIds.Count == 0)
        {
            return 0;
        }

        await using var session = await _transports.CreateSessionAsync(cancellationToken);
        if (session is null)
        {
            if (Time.GetUtcNow() - _lastNotConfiguredLog >= NotConfiguredLogInterval)
            {
                Logger.LogWarning("Email is not configured: {Count} or more email(s) are waiting. Configure SMTP in Settings, Email", dueIds.Count);
                _lastNotConfiguredLog = Time.GetUtcNow();
            }

            return 0;
        }

        // Truncated to PostgreSQL's microsecond precision so the equality below matches the stored value.
        var claimUntil = TruncateToMicroseconds(now + ClaimDuration);
        await db.OutboxEmails
            .Where(e => dueIds.Contains(e.Id) && e.SentAt == null && e.NextAttemptAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, claimUntil), cancellationToken);
        var emails = await db.OutboxEmails.AsNoTracking()
            .Where(e => dueIds.Contains(e.Id) && e.SentAt == null && e.NextAttemptAt == claimUntil)
            .OrderBy(e => e.CreatedAt)
            .ToListAsync(cancellationToken);

        var attempted = 0;
        foreach (var email in emails)
        {
            if (_breaker.IsOpen)
            {
                // Release the rest of the claim so these emails go out as soon as the pause ends.
                var remaining = emails.Skip(attempted).Select(e => e.Id).ToList();
                var resumeAt = (_breaker.OpenUntil ?? Time.GetUtcNow()).UtcDateTime;
                await db.OutboxEmails.Where(e => remaining.Contains(e.Id) && e.SentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, resumeAt), CancellationToken.None);
                break;
            }

            attempted++;
            try
            {
                await session.SendAsync(email, cancellationToken);
                _breaker.RecordSuccess();
                var sentAt = Time.GetUtcNow().UtcDateTime;
                await db.OutboxEmails.Where(e => e.Id == email.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(e => e.SentAt, sentAt)
                        .SetProperty(e => e.Attempts, e => e.Attempts + 1)
                        .SetProperty(e => e.LastError, (string?)null), CancellationToken.None);
            }
            catch (EmailDeliveryException ex)
            {
                await RecordFailureAsync(db, email.Id, email.Attempts, ex, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(db, email.Id, email.Attempts,
                    new EmailDeliveryException("Sending failed: " + ex.Message, permanent: false, ex), CancellationToken.None);
            }
        }

        return attempted;
    }

    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - value.Ticks % 10, DateTimeKind.Utc);

    private async Task RecordFailureAsync(FleetoDbContext db, Guid emailId, int previousAttempts, EmailDeliveryException failure,
        CancellationToken cancellationToken)
    {
        var attempts = failure.IsPermanent ? MaxAttempts : previousAttempts + 1;
        var nextAttemptAt = Time.GetUtcNow().UtcDateTime + RetryDelay(attempts);
        var error = OutboxEmails.Truncate(failure.Message, 1000);

        await db.OutboxEmails.Where(e => e.Id == emailId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Attempts, attempts)
                .SetProperty(e => e.NextAttemptAt, nextAttemptAt)
                .SetProperty(e => e.LastError, error), cancellationToken);

        if (!failure.IsPermanent && _breaker.RecordFailure())
        {
            Logger.LogWarning("Email delivery failed {Failures} times in a row; pausing delivery for {Minutes} minutes. Last error: {Error}",
                _options.CircuitBreakerFailures, _options.CircuitBreakerPauseMinutes, error);
        }

        if (attempts >= MaxAttempts)
        {
            Logger.LogWarning("Gave up on email {EmailId} after {Attempts} attempt(s): {Error}", emailId, attempts, error);
        }
        else
        {
            Logger.LogInformation("Email {EmailId} failed (attempt {Attempts}); next attempt at {NextAttemptAt:O}: {Error}",
                emailId, attempts, nextAttemptAt, error);
        }
    }
}
