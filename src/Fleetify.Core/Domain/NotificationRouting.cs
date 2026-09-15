using Fleetify.Core.Entities;

namespace Fleetify.Core.Domain;

/// <summary>What happened to an alert, as far as notifications are concerned.</summary>
public enum NotificationEvent
{
    Opened,
    /// <summary>The severity went up (warning to critical).</summary>
    Escalated,
    Resolved,
    /// <summary>The hold on an alert ended while the alert is still unresolved.</summary>
    HoldEnded
}

/// <summary>
/// Routing rules (decided 2026-09-15): a notification channel receives an alert when it is enabled, the alert belongs to one
/// of its clients (or the channel takes all clients), the alert's severity is at least the channel's minimum, and, for a
/// resolve, the channel wants resolves. There is no time-based escalation.
/// </summary>
public static class NotificationRouting
{
    public static bool Receives(NotificationChannel channel, IReadOnlySet<Guid> channelClientIds, Guid alertClientId, AlertSeverity severity,
        NotificationEvent notificationEvent) =>
        channel.Enabled &&
        (channel.AllClients || channelClientIds.Contains(alertClientId)) &&
        channel.MinimumSeverity <= severity &&
        (notificationEvent != NotificationEvent.Resolved || channel.NotifyOnResolve);

    /// <summary>Event name used in webhook payloads and outbox categories.</summary>
    public static string EventName(NotificationEvent notificationEvent) => notificationEvent switch
    {
        NotificationEvent.Opened => "alert.opened",
        NotificationEvent.Escalated => "alert.escalated",
        NotificationEvent.Resolved => "alert.resolved",
        _ => "alert.hold_ended"
    };
}
