using System.Text.Json;
using System.Text.Json.Serialization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Notifications;

/// <summary>One alert notification as a line of a digest. Every string may come from an agent or a user: untrusted.</summary>
public sealed record DigestLine(
    NotificationEvent Event,
    AlertSeverity Severity,
    string Title,
    string Hostname,
    string ClientCode,
    string ClientName,
    string SiteName,
    DateTime At,
    string EndpointUrl)
{
    /// <summary>What happened, as the single notification would have said it.</summary>
    public string Headline => Event switch
    {
        NotificationEvent.Escalated => $"Now {Severity.ToString().ToLowerInvariant()}: {Title}",
        NotificationEvent.Resolved => $"Resolved: {Title}",
        NotificationEvent.HoldEnded => $"Still open after hold: {Title}",
        _ => Title
    };
}

/// <summary>What to do with the waiting alert notifications of one recipient or channel.</summary>
/// <param name="Individually">How many of the oldest go out on their own.</param>
/// <param name="BundleNow">True when the rest goes out now, combined in one digest.</param>
/// <param name="PostponeUntil">When the rest is looked at again, when it may not go out yet.</param>
public readonly record struct DigestDecision(int Individually, bool BundleNow, DateTime? PostponeUntil);

/// <summary>
/// Alert notifications during a flood (0.6.0, decided with the developer on 2026-09-24). Per email address, and per Slack
/// or Teams channel: the first <see cref="IndividualLimit"/> within <see cref="Window"/> go out on their own; what comes
/// after is combined into one digest per <see cref="Window"/> for as long as the flood lasts. Opened, escalated, resolved
/// and hold-ended notifications all count. Generic webhooks always get every notification on its own: an integration
/// needs each event.
/// </summary>
public static class NotificationDigest
{
    public const int IndividualLimit = 5;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    /// <summary>Category of a digest, in the email and the webhook outbox, and the webhook event header.</summary>
    public const string Category = "alert.digest";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static DigestLine Line(AlertNotification alert) => new(alert.Event, alert.Severity, Cut(alert.Title, 500), Cut(alert.Hostname, 255),
        alert.ClientCode, Cut(alert.ClientName, 200), Cut(alert.SiteName, 200), alert.ResolvedAt ?? alert.OpenedAt, alert.EndpointUrl);

    public static string Serialize(DigestLine line) => JsonSerializer.Serialize(line, Json);

    public static DigestLine? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<DigestLine>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Decides for one recipient or channel. <paramref name="sentInWindow"/> counts the alert notifications and digests that
    /// went out within <see cref="Window"/>; <paramref name="lastDigestAt"/> is when the last digest was made.
    /// </summary>
    public static DigestDecision Decide(int sentInWindow, DateTime? lastDigestAt, int waiting, DateTime now)
    {
        var allowance = Math.Max(0, IndividualLimit - sentInWindow);
        if (waiting <= allowance)
        {
            return new DigestDecision(waiting, false, null);
        }

        return lastDigestAt is { } last && now - last < Window
            ? new DigestDecision(allowance, false, last + Window)
            : new DigestDecision(allowance, true, null);
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
