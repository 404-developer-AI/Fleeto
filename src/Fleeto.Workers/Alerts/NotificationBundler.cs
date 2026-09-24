using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Alerts;

/// <summary>
/// Combines alert notifications during a flood (0.6.0; the rule is <see cref="NotificationDigest"/>). Runs at the start of every
/// pass of the email and webhook outbox, before anything is sent: per email address, and per Slack or Teams channel, it lets the
/// first notifications of a window through on their own, writes the rest into one digest in the same outbox, or postpones them
/// until the next digest may go out. A combined notification is marked with the digest it went into and counts as done; the
/// digest is delivered, retried and given up like any other row, so nothing is lost or sent twice.
/// </summary>
public sealed class NotificationBundler
{
    // One bundling pass at a time over every workers process, so two passes cannot both make a digest of the same rows.
    private const long BundleLockKey = 0x466C744469676573; // "FltDiges"

    /// <summary>At most this many waiting notifications are looked at per pass; the rest waits for the next pass.</summary>
    internal const int MaxWaiting = 5000;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly TimeProvider _time;
    private readonly ILogger<NotificationBundler> _logger;

    public NotificationBundler(IFleetoDbContextFactory dbFactory, TimeProvider time, ILogger<NotificationBundler> logger)
    {
        _dbFactory = dbFactory;
        _time = time;
        _logger = logger;
    }

    /// <summary>Bundles the waiting alert emails. Returns the number of digests written.</summary>
    public async Task<int> BundleEmailsAsync(int maxAttempts, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({BundleLockKey})", cancellationToken);

        var waiting = await db.OutboxEmails.AsNoTracking()
            .Where(e => e.SentAt == null && e.DigestLine != null && e.BundledInto == null && e.Attempts < maxAttempts && e.NextAttemptAt <= now)
            .OrderBy(e => e.CreatedAt)
            .Take(MaxWaiting)
            .Select(e => new Waiting(e.Id, e.ToAddress, e.DigestLine!))
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return 0;
        }

        var addresses = waiting.Select(w => w.Target).Distinct().ToList();
        var windowStart = now - NotificationDigest.Window;
        // Bounded by CreatedAt so the (ToAddress, CreatedAt) index serves it; a notification sent in the window was created in the last day.
        var recent = await db.OutboxEmails.AsNoTracking()
            .Where(e => addresses.Contains(e.ToAddress) && e.CreatedAt >= now.AddDays(-1) && e.BundledInto == null &&
                        (e.DigestLine != null || e.Category == NotificationDigest.Category) &&
                        (e.SentAt >= windowStart || (e.Category == NotificationDigest.Category && e.CreatedAt >= windowStart)))
            .Select(e => new Recent(e.ToAddress, e.Category == NotificationDigest.Category, e.CreatedAt))
            .ToListAsync(cancellationToken);

        InstanceInfo? instance = null;
        var digests = 0;
        foreach (var group in waiting.GroupBy(w => w.Target))
        {
            var sent = recent.Where(r => r.Target == group.Key).ToList();
            var decision = NotificationDigest.Decide(sent.Count, sent.Where(r => r.IsDigest).Select(r => (DateTime?)r.CreatedAt).Max(), group.Count(), now);
            var rest = group.Skip(decision.Individually).ToList();
            if (rest.Count == 0)
            {
                continue;
            }

            var ids = rest.Select(r => r.Id).ToList();
            if (!decision.BundleNow)
            {
                var until = decision.PostponeUntil!.Value;
                await db.OutboxEmails.Where(e => ids.Contains(e.Id)).ExecuteUpdateAsync(s => s.SetProperty(e => e.NextAttemptAt, until), cancellationToken);
                continue;
            }

            instance ??= await InstanceQueries.GetInstanceAsync(db, cancellationToken);
            var digest = OutboxEmails.Create(group.Key, EmailTemplates.AlertDigest(instance.Fqdn, Lines(rest), instance.AlertsUrl), NotificationDigest.Category, now);
            db.OutboxEmails.Add(digest);
            await db.SaveChangesAsync(cancellationToken);
            await db.OutboxEmails.Where(e => ids.Contains(e.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.BundledInto, digest.Id).SetProperty(e => e.SentAt, now), cancellationToken);
            digests++;
        }

        await transaction.CommitAsync(cancellationToken);
        if (digests > 0)
        {
            _logger.LogInformation("Combined waiting alert emails into {Digests} digest(s)", digests);
        }

        return digests;
    }

    /// <summary>Bundles the waiting alert notifications of Slack and Teams channels. Returns the number of digests written.</summary>
    public async Task<int> BundleWebhooksAsync(int maxAttempts, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({BundleLockKey})", cancellationToken);

        var waiting = await db.OutboxWebhooks.AsNoTracking()
            .Where(w => w.SentAt == null && w.DigestLine != null && w.BundledInto == null && w.Attempts < maxAttempts && w.NextAttemptAt <= now)
            .OrderBy(w => w.CreatedAt)
            .Take(MaxWaiting)
            .Select(w => new { w.Id, w.NotificationChannelId, w.DigestLine })
            .ToListAsync(cancellationToken);
        if (waiting.Count == 0)
        {
            return 0;
        }

        var channelIds = waiting.Select(w => w.NotificationChannelId).Distinct().ToList();
        var formats = await db.NotificationChannels.AsNoTracking()
            .Where(c => channelIds.Contains(c.Id) && c.WebhookFormat != null)
            .ToDictionaryAsync(c => c.Id, c => c.WebhookFormat!.Value, cancellationToken);
        var windowStart = now - NotificationDigest.Window;
        var recent = await db.OutboxWebhooks.AsNoTracking()
            .Where(w => channelIds.Contains(w.NotificationChannelId) && w.CreatedAt >= now.AddDays(-1) && w.BundledInto == null &&
                        (w.DigestLine != null || w.Category == NotificationDigest.Category) &&
                        (w.SentAt >= windowStart || (w.Category == NotificationDigest.Category && w.CreatedAt >= windowStart)))
            .Select(w => new { w.NotificationChannelId, IsDigest = w.Category == NotificationDigest.Category, w.CreatedAt })
            .ToListAsync(cancellationToken);

        InstanceInfo? instance = null;
        var digests = 0;
        foreach (var group in waiting.GroupBy(w => w.NotificationChannelId))
        {
            // A generic channel never gets a digest; a channel that changed format since is left to the outbox, which gives it up.
            if (!formats.TryGetValue(group.Key, out var format) || format == WebhookFormat.Generic)
            {
                continue;
            }

            var sent = recent.Where(r => r.NotificationChannelId == group.Key).ToList();
            var decision = NotificationDigest.Decide(sent.Count, sent.Where(r => r.IsDigest).Select(r => (DateTime?)r.CreatedAt).Max(), group.Count(), now);
            var rest = group.Skip(decision.Individually).ToList();
            if (rest.Count == 0)
            {
                continue;
            }

            var ids = rest.Select(r => r.Id).ToList();
            if (!decision.BundleNow)
            {
                var until = decision.PostponeUntil!.Value;
                await db.OutboxWebhooks.Where(w => ids.Contains(w.Id)).ExecuteUpdateAsync(s => s.SetProperty(w => w.NextAttemptAt, until), cancellationToken);
                continue;
            }

            instance ??= await InstanceQueries.GetInstanceAsync(db, cancellationToken);
            var digest = new OutboxWebhook
            {
                Id = Guid.NewGuid(),
                NotificationChannelId = group.Key,
                Category = NotificationDigest.Category,
                Payload = WebhookPayloads.Digest(format, Lines(rest.Select(r => new Waiting(r.Id, string.Empty, r.DigestLine!))), instance.AlertsUrl),
                NextAttemptAt = now,
                CreatedAt = now
            };
            db.OutboxWebhooks.Add(digest);
            await db.SaveChangesAsync(cancellationToken);
            await db.OutboxWebhooks.Where(w => ids.Contains(w.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(w => w.BundledInto, digest.Id).SetProperty(w => w.SentAt, now), cancellationToken);
            digests++;
        }

        await transaction.CommitAsync(cancellationToken);
        if (digests > 0)
        {
            _logger.LogInformation("Combined waiting chat notifications into {Digests} digest(s)", digests);
        }

        return digests;
    }

    private static List<DigestLine> Lines(IEnumerable<Waiting> waiting) =>
        waiting.Select(w => NotificationDigest.Deserialize(w.Line)).OfType<DigestLine>().ToList();

    private sealed record Waiting(Guid Id, string Target, string Line);

    private sealed record Recent(string Target, bool IsDigest, DateTime CreatedAt);
}
