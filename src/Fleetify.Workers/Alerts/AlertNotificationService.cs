using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Workers.Common;
using Fleetify.Workers.Email;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Workers.Alerts;

public enum AlertTransitionKind
{
    Opened,
    /// <summary>The severity went up (warning to critical).</summary>
    Escalated,
    Resolved,
    /// <summary>The hold on an alert ended while the alert is still unresolved.</summary>
    HoldEnded
}

/// <summary>An alert state change that was just written in the caller's transaction.</summary>
public sealed record AlertTransition(Guid AlertId, AlertTransitionKind Kind);

/// <summary>
/// Turns alert transitions into outbox emails for every enabled notification channel whose minimum severity the alert
/// meets. Called inside the transaction that changes the alert, after the alert rows are saved: the emails are
/// committed together with the transition or not at all, so a crash can neither lose a notification nor send one
/// twice, and no "last notified" bookkeeping is needed.
/// <para>
/// While an alert is on hold its escalation and resolve emails are not sent; opening never happens on hold (a new alert
/// has no hold). When the hold ends and the alert is still unresolved, one <see cref="AlertTransitionKind.HoldEnded"/>
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
    /// Adds one <see cref="OutboxEmail"/> per recipient to <paramref name="db"/> (not saved). The alert rows must already be
    /// saved in the current transaction. Returns the number of emails added.
    /// </summary>
    public async Task<int> AddNotificationsAsync(FleetifyDbContext db, IReadOnlyCollection<AlertTransition> transitions,
        CancellationToken cancellationToken)
    {
        if (transitions.Count == 0)
        {
            return 0;
        }

        var channels = await db.NotificationChannels.AsNoTracking()
            .Where(c => c.Enabled && c.Type == NotificationChannelType.Email)
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
                a.Severity,
                a.Title,
                a.Detail,
                a.OpenedAt,
                a.ResolvedAt,
                a.ResolvedReason,
                a.HeldUntil,
                a.EndpointId,
                Hostname = a.Endpoint!.Hostname,
                SiteName = a.Endpoint.Site!.Name,
                ClientName = a.Endpoint.Site.Client!.Name
            })
            .ToDictionaryAsync(a => a.Id, cancellationToken);

        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        var added = 0;

        foreach (var transition in transitions)
        {
            if (!alerts.TryGetValue(transition.AlertId, out var alert))
            {
                continue;
            }

            if (alert.HeldUntil is { } heldUntil && heldUntil > now && transition.Kind != AlertTransitionKind.HoldEnded)
            {
                continue;
            }

            var recipients = channels
                .Where(c => c.MinimumSeverity <= alert.Severity)
                .Where(c => transition.Kind != AlertTransitionKind.Resolved || c.NotifyOnResolve)
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
                AlertTransitionKind.Opened => (EmailTemplates.AlertOpened(model), CategoryOpened),
                AlertTransitionKind.Escalated => (EmailTemplates.AlertEscalated(model), CategoryEscalated),
                AlertTransitionKind.HoldEnded => (EmailTemplates.AlertHoldEnded(model), CategoryHoldEnded),
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
