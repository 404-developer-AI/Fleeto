using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Notifications;
using Fleeto.Workers.Common;
using Fleeto.Workers.Email;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Alerts;

/// <summary>An alert state change that was just written in the caller's transaction.</summary>
public sealed record AlertTransition(Guid AlertId, NotificationEvent Kind);

/// <summary>
/// Turns alert transitions into outbox emails and outbox webhooks for every channel that receives the alert under the
/// routing rules (<see cref="NotificationRouting"/>: client, minimum severity, resolves). Called inside the transaction that
/// changes the alert, after the alert rows are saved: the notifications are committed together with the transition or not
/// at all, so a crash can neither lose a notification nor send one twice, and no "last notified" bookkeeping is needed.
/// <para>
/// While an alert is on hold its escalation and resolve emails are not sent; opening never happens on hold (a new alert
/// has no hold). When the hold ends and the alert is still unresolved, one <see cref="NotificationEvent.HoldEnded"/>
/// email goes out.
/// </para>
/// </summary>
public sealed class AlertNotificationService
{
    public const string CategoryOpened = "alert.opened";
    public const string CategoryEscalated = "alert.escalated";
    public const string CategoryResolved = "alert.resolved";
    public const string CategoryHoldEnded = "alert.hold_ended";

    private readonly TimeProvider _time;

    public AlertNotificationService(TimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Adds one <see cref="OutboxEmail"/> per recipient and one <see cref="OutboxWebhook"/> per webhook channel to
    /// <paramref name="db"/> (not saved). The alert rows must already be saved in the current transaction. Returns the number
    /// of notifications added.
    /// </summary>
    public async Task<int> AddNotificationsAsync(FleetoDbContext db, IReadOnlyCollection<AlertTransition> transitions,
        CancellationToken cancellationToken)
    {
        if (transitions.Count == 0)
        {
            return 0;
        }

        var channels = await db.NotificationChannels.AsNoTracking()
            .Where(c => c.Enabled)
            .Include(c => c.Clients)
            .ToListAsync(cancellationToken);
        if (channels.Count == 0)
        {
            return 0;
        }

        var ids = transitions.Select(t => t.AlertId).Distinct().ToList();
        var alerts = await db.Alerts.AsNoTracking()
            .Where(a => ids.Contains(a.Id))
            .Select(a => new
            {
                a.Id,
                a.ClientId,
                a.Severity,
                a.Title,
                a.Detail,
                a.OpenedAt,
                a.ResolvedAt,
                a.ResolvedReason,
                a.HeldUntil,
                a.EndpointId,
                Hostname = a.Endpoint!.Hostname,
                a.Endpoint.SiteId,
                SiteName = a.Endpoint.Site!.Name,
                ClientCode = a.Endpoint.Site.Client!.Code,
                ClientName = a.Endpoint.Site.Client.Name
            })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        var added = 0;
        var clientSets = channels.ToDictionary(c => c.Id, c => (IReadOnlySet<Guid>)c.Clients.Select(x => x.ClientId).ToHashSet());

        foreach (var transition in transitions)
        {
            if (!alerts.TryGetValue(transition.AlertId, out var alert))
            {
                continue;
            }

            if (alert.HeldUntil is { } heldUntil && heldUntil > now && transition.Kind != NotificationEvent.HoldEnded)
            {
                continue;
            }

            var receiving = channels
                .Where(c => NotificationRouting.Receives(c, clientSets[c.Id], alert.ClientId, alert.Severity, transition.Kind))
                .ToList();
            if (receiving.Count == 0)
            {
                continue;
            }

            var notification = new AlertNotification(transition.Kind, alert.Id, alert.Title, alert.Detail, alert.Severity, alert.OpenedAt,
                alert.ResolvedAt, alert.ResolvedReason, alert.EndpointId, alert.Hostname, alert.SiteId, alert.SiteName, alert.ClientId,
                alert.ClientCode, alert.ClientName, instance.EndpointUrl(alert.EndpointId));
            foreach (var channel in receiving.Where(c => c is { Type: NotificationChannelType.Webhook, WebhookFormat: not null }))
            {
                var id = Guid.NewGuid();
                db.OutboxWebhooks.Add(new OutboxWebhook
                {
                    Id = id,
                    NotificationChannelId = channel.Id,
                    Category = NotificationRouting.EventName(transition.Kind),
                    Payload = WebhookPayloads.Alert(channel.WebhookFormat!.Value, id, instance.Fqdn, notification, now),
                    NextAttemptAt = now,
                    CreatedAt = now
                });
                added++;
            }

            var recipients = receiving
                .Where(c => c.Type == NotificationChannelType.Email)
                .SelectMany(c => EmailAddresses.Split(c.Recipients))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (recipients.Count == 0)
            {
                continue;
            }

            var model = new AlertEmailModel(alert.Title, alert.Severity.ToString(), alert.Hostname, alert.ClientName, alert.SiteName,
                alert.OpenedAt, alert.Detail, instance.EndpointUrl(alert.EndpointId), alert.ResolvedReason, alert.ResolvedAt);
            var (content, category) = transition.Kind switch
            {
                NotificationEvent.Opened => (EmailTemplates.AlertOpened(model), CategoryOpened),
                NotificationEvent.Escalated => (EmailTemplates.AlertEscalated(model), CategoryEscalated),
                NotificationEvent.HoldEnded => (EmailTemplates.AlertHoldEnded(model), CategoryHoldEnded),
                _ => (EmailTemplates.AlertResolved(model), CategoryResolved)
            };

            foreach (var recipient in recipients)
            {
                db.OutboxEmails.Add(OutboxEmails.Create(recipient, content, category, now));
                added++;
            }
        }

        return added;
    }
}
