using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Security;

namespace Fleetify.Infrastructure.Notifications;

/// <summary>Where a webhook channel posts to. Stored encrypted: Slack and Teams URLs are secrets themselves.</summary>
public sealed record WebhookTarget(string Url, string? SigningSecret);

/// <summary>
/// Webhook channel rules shared by web (validation, storage) and workers (delivery): URL validation, encryption bound to the
/// channel, and the request signature.
/// <para>
/// Signature (generic format): header <c>X-Fleeto-Signature: t=&lt;unix seconds&gt;,v1=&lt;hex&gt;</c>, where the hex value is
/// HMAC-SHA256 over <c>"&lt;t&gt;.&lt;body&gt;"</c> (UTF-8) with the signing secret (UTF-8, including its <c>whsec_</c> prefix)
/// as key. Receivers compare in constant time and refuse a timestamp older than five minutes.
/// </para>
/// </summary>
public static class WebhookTargets
{
    public const int MaxUrlLength = 2000;

    /// <summary>Delivery attempts before the workers give up on a webhook.</summary>
    public const int MaxDeliveryAttempts = 10;

    public const string SignatureHeader = "X-Fleeto-Signature";
    public const string EventHeader = "X-Fleeto-Event";
    public const string DeliveryHeader = "X-Fleeto-Delivery";
    private const string SecretPrefix = "whsec_";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Checks an admin-entered URL: absolute https, no user name or password in it, and not aimed at the instance's own
    /// network by name or address. Host names are checked again on every delivery against the address they resolve to.
    /// Returns the problem (cause and next step), or null with the parsed URL.
    /// </summary>
    public static string? ValidateUrl(string? text, out Uri? uri)
    {
        uri = null;
        var value = text?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return "Enter the webhook URL.";
        }

        if (value.Length > MaxUrlLength)
        {
            return $"The webhook URL is longer than {MaxUrlLength} characters.";
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var parsed) || parsed.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(parsed.Host))
        {
            return "Enter the webhook URL as an https address, for example https://hooks.slack.com/services/....";
        }

        if (!string.IsNullOrEmpty(parsed.UserInfo))
        {
            return "The webhook URL contains a user name or password. Remove it; Fleeto signs generic webhooks instead.";
        }

        var host = parsed.IdnHost.TrimEnd('.');
        if (parsed.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(host.Trim('[', ']'), out var address) || !NetworkAddressPolicy.IsPublic(address))
            {
                return "The webhook URL points to a private or local address. Fleeto only sends webhooks to public addresses.";
            }
        }
        else if (host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || !host.Contains('.') ||
                 host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ||
                 host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase))
        {
            return "The webhook URL points to a local host name. Fleeto only sends webhooks to public addresses.";
        }

        uri = parsed;
        return null;
    }

    public static string Protect(ISecretProtector protector, Guid channelId, WebhookTarget target) =>
        protector.Protect(SecretPurposes.Settings, JsonSerializer.Serialize(target, JsonOptions), AssociatedData(channelId));

    public static WebhookTarget Unprotect(ISecretProtector protector, Guid channelId, string stored) =>
        JsonSerializer.Deserialize<WebhookTarget>(protector.Unprotect(SecretPurposes.Settings, stored, AssociatedData(channelId)), JsonOptions)
        ?? throw new InvalidOperationException("The stored webhook target is empty.");

    private static string AssociatedData(Guid channelId) => "NotificationChannels|" + channelId.ToString("D");

    /// <summary>A new signing secret: <c>whsec_</c> and 256 random bits, base64url.</summary>
    public static string NewSigningSecret() =>
        SecretPrefix + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The value of <see cref="SignatureHeader"/> for a body sent at <paramref name="timestamp"/>.</summary>
    public static string Sign(string secret, DateTimeOffset timestamp, string body)
    {
        var seconds = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(seconds + "." + body));
        return $"t={seconds},v1={Convert.ToHexStringLower(mac)}";
    }
}

/// <summary>The alert facts a webhook carries. Hostnames and client names only; never check output or event log text.</summary>
public sealed record AlertNotification(
    NotificationEvent Event,
    Guid AlertId,
    string Title,
    string Detail,
    AlertSeverity Severity,
    DateTime OpenedAt,
    DateTime? ResolvedAt,
    string? ResolvedReason,
    Guid EndpointId,
    string Hostname,
    Guid SiteId,
    string SiteName,
    Guid ClientId,
    string ClientCode,
    string ClientName,
    string EndpointUrl);

/// <summary>Request bodies per <see cref="WebhookFormat"/>. Built when the notification is queued and stored as is.</summary>
public static class WebhookPayloads
{
    private const int MaxDetailLength = 2000;

    public static string Headline(AlertNotification alert) => alert.Event switch
    {
        NotificationEvent.Escalated => $"Now {alert.Severity.ToString().ToLowerInvariant()}: {alert.Title}",
        NotificationEvent.Resolved => $"Resolved: {alert.Title}",
        NotificationEvent.HoldEnded => $"Still open after hold: {alert.Title}",
        _ => alert.Title
    };

    public static string Alert(WebhookFormat format, Guid deliveryId, string instanceFqdn, AlertNotification alert, DateTime now) => format switch
    {
        WebhookFormat.Slack => Slack(Headline(alert), Facts(alert), Trim(alert.Event == NotificationEvent.Resolved ? alert.ResolvedReason ?? "" : alert.Detail),
            alert.EndpointUrl, "Open the endpoint"),
        WebhookFormat.Teams => Teams(Headline(alert), Facts(alert), Trim(alert.Event == NotificationEvent.Resolved ? alert.ResolvedReason ?? "" : alert.Detail),
            alert.EndpointUrl, "Open the endpoint", alert.Event == NotificationEvent.Resolved ? "Good" : alert.Severity == AlertSeverity.Critical ? "Attention" : "Warning"),
        _ => Serialize(new JsonObject
        {
            ["id"] = deliveryId,
            ["type"] = NotificationRouting.EventName(alert.Event),
            ["version"] = 1,
            ["createdAt"] = Utc(now),
            ["instance"] = instanceFqdn,
            ["alert"] = new JsonObject
            {
                ["id"] = alert.AlertId,
                ["title"] = alert.Title,
                ["detail"] = alert.Detail,
                ["severity"] = alert.Severity.ToString().ToLowerInvariant(),
                ["state"] = alert.ResolvedAt is null ? "open" : "resolved",
                ["openedAt"] = Utc(alert.OpenedAt),
                ["resolvedAt"] = alert.ResolvedAt is { } resolvedAt ? Utc(resolvedAt) : null,
                ["resolvedReason"] = alert.ResolvedReason,
                ["url"] = alert.EndpointUrl,
                ["endpoint"] = new JsonObject { ["id"] = alert.EndpointId, ["hostname"] = alert.Hostname },
                ["site"] = new JsonObject { ["id"] = alert.SiteId, ["name"] = alert.SiteName },
                ["client"] = new JsonObject { ["id"] = alert.ClientId, ["code"] = alert.ClientCode, ["name"] = alert.ClientName }
            }
        })
    };

    public static string Test(WebhookFormat format, Guid deliveryId, string instanceFqdn, string channelsUrl, DateTime now)
    {
        const string headline = "Test message from Fleeto";
        var text = $"Webhook delivery from {instanceFqdn} works with the current settings.";
        return format switch
        {
            WebhookFormat.Slack => Slack(headline, [], text, channelsUrl, "Open notification channels"),
            WebhookFormat.Teams => Teams(headline, [], text, channelsUrl, "Open notification channels", "Accent"),
            _ => Serialize(new JsonObject
            {
                ["id"] = deliveryId,
                ["type"] = "test",
                ["version"] = 1,
                ["createdAt"] = Utc(now),
                ["instance"] = instanceFqdn,
                ["message"] = text
            })
        };
    }

    private static List<(string Name, string Value)> Facts(AlertNotification alert)
    {
        var facts = new List<(string, string)>
        {
            ("Severity", alert.Severity.ToString()),
            ("Endpoint", alert.Hostname),
            ("Client", $"{alert.ClientName} ({alert.ClientCode})"),
            ("Site", alert.SiteName),
            ("Opened", Utc(alert.OpenedAt).Replace('T', ' ').TrimEnd('Z') + " UTC")
        };
        if (alert.ResolvedAt is { } resolvedAt)
        {
            facts.Add(("Resolved", Utc(resolvedAt).Replace('T', ' ').TrimEnd('Z') + " UTC"));
        }

        return facts;
    }

    private static string Slack(string headline, List<(string Name, string Value)> facts, string text, string url, string linkText)
    {
        var lines = new StringBuilder($"*{SlackEscape(headline)}*");
        foreach (var (name, value) in facts)
        {
            lines.Append('\n').Append(name).Append(": ").Append(SlackEscape(value));
        }

        var blocks = new JsonArray
        {
            new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "mrkdwn", ["text"] = lines.ToString() } }
        };
        if (!string.IsNullOrWhiteSpace(text))
        {
            blocks.Add(new JsonObject { ["type"] = "section", ["text"] = new JsonObject { ["type"] = "plain_text", ["text"] = text } });
        }

        blocks.Add(new JsonObject
        {
            ["type"] = "context",
            ["elements"] = new JsonArray { new JsonObject { ["type"] = "mrkdwn", ["text"] = $"<{SlackEscape(url)}|{SlackEscape(linkText)}>" } }
        });
        return Serialize(new JsonObject { ["text"] = headline, ["blocks"] = blocks });
    }

    private static string Teams(string headline, List<(string Name, string Value)> facts, string text, string url, string linkText, string color)
    {
        var body = new JsonArray
        {
            new JsonObject { ["type"] = "TextBlock", ["text"] = headline, ["weight"] = "Bolder", ["size"] = "Medium", ["wrap"] = true, ["color"] = color }
        };
        if (facts.Count > 0)
        {
            var factArray = new JsonArray();
            foreach (var (name, value) in facts)
            {
                factArray.Add(new JsonObject { ["title"] = name, ["value"] = value });
            }

            body.Add(new JsonObject { ["type"] = "FactSet", ["facts"] = factArray });
        }

        if (!string.IsNullOrWhiteSpace(text))
        {
            body.Add(new JsonObject { ["type"] = "TextBlock", ["text"] = text, ["wrap"] = true });
        }

        var card = new JsonObject
        {
            ["$schema"] = "http://adaptivecards.io/schemas/adaptive-card.json",
            ["type"] = "AdaptiveCard",
            ["version"] = "1.4",
            ["body"] = body,
            ["actions"] = new JsonArray { new JsonObject { ["type"] = "Action.OpenUrl", ["title"] = linkText, ["url"] = url } }
        };
        return Serialize(new JsonObject
        {
            ["type"] = "message",
            ["attachments"] = new JsonArray
            {
                new JsonObject { ["contentType"] = "application/vnd.microsoft.card.adaptive", ["contentUrl"] = null, ["content"] = card }
            }
        });
    }

    private static string SlackEscape(string value) => value.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Trim(string value) => value.Length <= MaxDetailLength ? value : value[..MaxDetailLength] + "…";

    private static string Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static string Serialize(JsonObject value) => value.ToJsonString();
}
