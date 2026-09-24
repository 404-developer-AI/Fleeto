using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Notifications;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Common;
using Fleeto.Infrastructure.Email;
using Fleeto.Workers.Email;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleeto.Workers.Webhooks;

/// <summary>
/// Delivers the webhook outbox with the same retry schedule as email (<see cref="OutboxEmailService.RetryDelay"/>) and gives
/// up after <see cref="MaxAttempts"/> attempts, keeping the last error. Every channel has its own circuit breaker, so one
/// receiver that is down does not hold up the others. Deliveries claimed by a pass move their next attempt ten minutes
/// ahead first, so a second workers process or a crash mid-pass cannot post the same delivery twice within that window;
/// receivers still get the same delivery id on every attempt and can drop duplicates.
/// <para>
/// A delivery whose channel was disabled or turned into an email channel before it went out is given up, not sent.
/// Woken by <c>fleeto_outbox_webhooks</c>, polls every 30 seconds.
/// </para>
/// <para>
/// Each pass first combines the alert notifications of Slack and Teams channels during a flood
/// (<see cref="NotificationBundler"/>, 0.6.0); generic webhooks are never combined. When combining fails, the notifications
/// go out one by one.
/// </para>
/// </summary>
public sealed class OutboxWebhookService : WorkerLoop
{
    public const int MaxAttempts = WebhookTargets.MaxDeliveryAttempts;
    private static readonly TimeSpan ClaimDuration = TimeSpan.FromMinutes(10);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly IWebhookSender _sender;
    private readonly NotificationBundler _bundler;
    private readonly ISecretProtector _protector;
    private readonly WebhookOptions _options;
    private readonly Dictionary<Guid, CircuitBreaker> _breakers = [];
    private IDisposable? _subscription;

    public OutboxWebhookService(IFleetoDbContextFactory dbFactory, INotificationBus bus, IWebhookSender sender, NotificationBundler bundler,
        ISecretProtector protector, IOptions<WebhookOptions> options, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<OutboxWebhookService> logger)
        : base("outbox-webhook", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _sender = sender;
        _bundler = bundler;
        _protector = protector;
        _options = options.Value;
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override TimeSpan MaxRunDuration => TimeSpan.FromMinutes(30);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.OutboxWebhooks, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        await DeliverPendingAsync(cancellationToken) >= Math.Max(1, _options.BatchSize);

    private CircuitBreaker BreakerFor(Guid channelId)
    {
        if (!_breakers.TryGetValue(channelId, out var breaker))
        {
            breaker = new CircuitBreaker(_options.CircuitBreakerFailures, TimeSpan.FromMinutes(_options.CircuitBreakerPauseMinutes), Time);
            _breakers[channelId] = breaker;
        }

        return breaker;
    }

    /// <summary>Attempts one batch of due deliveries. Returns the number attempted.</summary>
    public async Task<int> DeliverPendingAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _bundler.BundleWebhooksAsync(MaxAttempts, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning(ex, "Combining chat notifications failed; they go out one by one");
        }

        var batchSize = Math.Max(1, _options.BatchSize);
        var now = Time.GetUtcNow().UtcDateTime;
        var paused = _breakers.Where(b => b.Value.IsOpen).Select(b => b.Key).ToArray();

        await using var db = _dbFactory.CreateSystem();
        var dueIds = await db.OutboxWebhooks.AsNoTracking()
            .Where(w => w.SentAt == null && w.Attempts < MaxAttempts && w.NextAttemptAt <= now && !paused.Contains(w.NotificationChannelId))
            .OrderBy(w => w.NextAttemptAt)
            .Select(w => w.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);
        if (dueIds.Count == 0)
        {
            return 0;
        }

        // Truncated to PostgreSQL's microsecond precision so the equality below matches the stored value.
        var claimUntil = new DateTime((now + ClaimDuration).Ticks - (now + ClaimDuration).Ticks % 10, DateTimeKind.Utc);
        await db.OutboxWebhooks
            .Where(w => dueIds.Contains(w.Id) && w.SentAt == null && w.NextAttemptAt <= now)
            .ExecuteUpdateAsync(s => s.SetProperty(w => w.NextAttemptAt, claimUntil), cancellationToken);
        var deliveries = await db.OutboxWebhooks.AsNoTracking()
            .Where(w => dueIds.Contains(w.Id) && w.SentAt == null && w.NextAttemptAt == claimUntil)
            .OrderBy(w => w.CreatedAt)
            .ToListAsync(cancellationToken);

        var channelIds = deliveries.Select(d => d.NotificationChannelId).Distinct().ToList();
        var channels = await db.NotificationChannels.AsNoTracking()
            .Where(c => channelIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Name, c.Enabled, c.Type, c.WebhookFormat, c.EncryptedWebhook })
            .ToDictionaryAsync(c => c.Id, cancellationToken);
        var targets = new Dictionary<Guid, WebhookTarget>();

        var attempted = 0;
        foreach (var delivery in deliveries)
        {
            var breaker = BreakerFor(delivery.NotificationChannelId);
            if (breaker.IsOpen)
            {
                // Release the claim so the delivery goes out as soon as the channel's pause ends.
                var resumeAt = (breaker.OpenUntil ?? Time.GetUtcNow()).UtcDateTime;
                await db.OutboxWebhooks.Where(w => w.Id == delivery.Id && w.SentAt == null)
                    .ExecuteUpdateAsync(s => s.SetProperty(w => w.NextAttemptAt, resumeAt), CancellationToken.None);
                continue;
            }

            if (!channels.TryGetValue(delivery.NotificationChannelId, out var channel) || !channel.Enabled ||
                channel is not { Type: NotificationChannelType.Webhook, WebhookFormat: not null, EncryptedWebhook: not null })
            {
                await RecordFailureAsync(db, delivery, new WebhookDeliveryException("The channel was disabled or changed before delivery.", permanent: true),
                    null, CancellationToken.None);
                continue;
            }

            attempted++;
            try
            {
                if (!targets.TryGetValue(channel.Id, out var target))
                {
                    try
                    {
                        target = WebhookTargets.Unprotect(_protector, channel.Id, channel.EncryptedWebhook);
                    }
                    catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or System.Text.Json.JsonException)
                    {
                        throw new WebhookDeliveryException("The stored webhook URL could not be decrypted. Enter the URL again in Settings, Notification channels.",
                            permanent: true, ex);
                    }

                    targets[channel.Id] = target;
                }

                var headers = new List<KeyValuePair<string, string>>
                {
                    new(WebhookTargets.EventHeader, delivery.Category),
                    new(WebhookTargets.DeliveryHeader, delivery.Id.ToString("D"))
                };
                if (channel.WebhookFormat == WebhookFormat.Generic && !string.IsNullOrEmpty(target.SigningSecret))
                {
                    headers.Add(new(WebhookTargets.SignatureHeader, WebhookTargets.Sign(target.SigningSecret, Time.GetUtcNow(), delivery.Payload)));
                }

                await _sender.SendAsync(new Uri(target.Url), delivery.Payload, headers, cancellationToken);
                breaker.RecordSuccess();
                var sentAt = Time.GetUtcNow().UtcDateTime;
                await db.OutboxWebhooks.Where(w => w.Id == delivery.Id)
                    .ExecuteUpdateAsync(s => s
                        .SetProperty(w => w.SentAt, sentAt)
                        .SetProperty(w => w.Attempts, w => w.Attempts + 1)
                        .SetProperty(w => w.LastError, (string?)null), CancellationToken.None);
            }
            catch (WebhookDeliveryException ex)
            {
                await RecordFailureAsync(db, delivery, ex, breaker, CancellationToken.None);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                await RecordFailureAsync(db, delivery, new WebhookDeliveryException("Posting the webhook failed: " + ex.Message, permanent: false, ex),
                    breaker, CancellationToken.None);
            }
        }

        return attempted;
    }

    private async Task RecordFailureAsync(FleetoDbContext db, OutboxWebhook delivery, WebhookDeliveryException failure, CircuitBreaker? breaker,
        CancellationToken cancellationToken)
    {
        var attempts = failure.IsPermanent ? MaxAttempts : delivery.Attempts + 1;
        var nextAttemptAt = Time.GetUtcNow().UtcDateTime + OutboxEmailService.RetryDelay(attempts);
        var error = OutboxEmails.Truncate(failure.Message, 1000);

        await db.OutboxWebhooks.Where(w => w.Id == delivery.Id)
            .ExecuteUpdateAsync(s => s
                .SetProperty(w => w.Attempts, attempts)
                .SetProperty(w => w.NextAttemptAt, nextAttemptAt)
                .SetProperty(w => w.LastError, error), cancellationToken);

        if (!failure.IsPermanent && breaker is not null && breaker.RecordFailure())
        {
            Logger.LogWarning("Webhook delivery to channel {ChannelId} failed {Failures} times in a row; pausing the channel for {Minutes} minutes. Last error: {Error}",
                delivery.NotificationChannelId, _options.CircuitBreakerFailures, _options.CircuitBreakerPauseMinutes, error);
        }

        if (attempts >= MaxAttempts)
        {
            Logger.LogWarning("Gave up on webhook delivery {DeliveryId} after {Attempts} attempt(s): {Error}", delivery.Id, attempts, error);
        }
        else
        {
            Logger.LogInformation("Webhook delivery {DeliveryId} failed (attempt {Attempts}); next attempt at {NextAttemptAt:O}: {Error}",
                delivery.Id, attempts, nextAttemptAt, error);
        }
    }
}
