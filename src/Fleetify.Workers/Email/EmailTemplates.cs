using System.Globalization;
using System.Net;
using System.Text;

namespace Fleetify.Workers.Email;

/// <summary>Subject, HTML body and plain-text alternative of one email.</summary>
public sealed record EmailContent(string Subject, string HtmlBody, string TextBody);

/// <summary>Values shown in alert emails. Every string may come from an agent or a user: treat all of it as untrusted.</summary>
public sealed record AlertEmailModel(
    string Title,
    string Severity,
    string Hostname,
    string ClientName,
    string SiteName,
    DateTime OpenedAt,
    string Detail,
    string EndpointUrl,
    string? ResolvedReason = null,
    DateTime? ResolvedAt = null);

/// <summary>
/// Email bodies sent by Fleeto (branding §9: sender name Fleeto, no subject prefixes, card layout, plain-text
/// alternative always; §8: calm, no exclamation marks, cause and next step). Everything that comes from an agent or a
/// user (host names, client names, titles, error text, addresses) is HTML-encoded before it reaches the markup: a host
/// name is reported by the agent and must never be able to inject markup or a link into a mail sent in our name.
/// Subjects are stripped of line breaks and control characters.
/// </summary>
public static class EmailTemplates
{
    public const string SenderName = "Fleeto";

    private const string BrandColor = "#0F766E";
    private const string TextColor = "#1C1917";
    private const string MutedColor = "#78716C";
    private const string BorderColor = "#E7E5E4";
    private const string CriticalColor = "#DC2626";
    private const string WarningColor = "#B45309";

    public static EmailContent AlertOpened(AlertEmailModel alert)
    {
        var html = Layout($"""
            <p style="margin:0 0 8px">{SeverityChip(alert.Severity)}</p>
            <p style="margin:0 0 16px;font-size:17px;font-weight:600">{Enc(alert.Title)}</p>
            {AlertFacts(alert)}
            {DetailBlock(alert.Detail)}
            {Button(alert.EndpointUrl, "Open the endpoint")}
            """);

        var text = $"""
            {alert.Severity} alert

            {alert.Title}

            {AlertFactsText(alert)}{DetailText(alert.Detail)}
            Open the endpoint: {alert.EndpointUrl}
            """;

        return new EmailContent(Subject(alert.Title), html, Footer(text));
    }

    public static EmailContent AlertEscalated(AlertEmailModel alert)
    {
        var html = Layout($"""
            <p style="margin:0 0 8px">{SeverityChip(alert.Severity)}</p>
            <p style="margin:0 0 16px">This alert is now {Enc(alert.Severity.ToLowerInvariant())}.</p>
            <p style="margin:0 0 16px;font-size:17px;font-weight:600">{Enc(alert.Title)}</p>
            {AlertFacts(alert)}
            {DetailBlock(alert.Detail)}
            {Button(alert.EndpointUrl, "Open the endpoint")}
            """);

        var text = $"""
            This alert is now {alert.Severity.ToLowerInvariant()}.

            {alert.Title}

            {AlertFactsText(alert)}{DetailText(alert.Detail)}
            Open the endpoint: {alert.EndpointUrl}
            """;

        return new EmailContent(Subject($"Now {alert.Severity.ToLowerInvariant()}: {alert.Title}"), html, Footer(text));
    }

    /// <summary>The hold on an alert ended while the alert is still unresolved.</summary>
    public static EmailContent AlertHoldEnded(AlertEmailModel alert)
    {
        var html = Layout($"""
            <p style="margin:0 0 8px">{SeverityChip(alert.Severity)}</p>
            <p style="margin:0 0 16px">The hold on this alert has ended and the alert is still open.</p>
            <p style="margin:0 0 16px;font-size:17px;font-weight:600">{Enc(alert.Title)}</p>
            {AlertFacts(alert)}
            {DetailBlock(alert.Detail)}
            {Button(alert.EndpointUrl, "Open the endpoint")}
            """);

        var text = $"""
            The hold on this alert has ended and the alert is still open.

            {alert.Title}

            {AlertFactsText(alert)}{DetailText(alert.Detail)}
            Open the endpoint: {alert.EndpointUrl}
            """;

        return new EmailContent(Subject($"Still open after hold: {alert.Title}"), html, Footer(text));
    }

    public static EmailContent AlertResolved(AlertEmailModel alert)
    {
        var reason = string.IsNullOrWhiteSpace(alert.ResolvedReason) ? "The alert was resolved." : alert.ResolvedReason;
        var resolvedAt = alert.ResolvedAt is null ? string.Empty : $"<br>Resolved: {Enc(Utc(alert.ResolvedAt.Value))}";
        var resolvedAtText = alert.ResolvedAt is null ? string.Empty : $"\nResolved:  {Utc(alert.ResolvedAt.Value)}";

        var html = Layout($"""
            <p style="margin:0 0 8px"><span style="display:inline-block;padding:2px 10px;border-radius:12px;background:#F0FDFA;color:{BrandColor};font-size:13px;font-weight:600">Resolved</span></p>
            <p style="margin:0 0 16px;font-size:17px;font-weight:600">{Enc(alert.Title)}</p>
            <p style="margin:0 0 16px">{Enc(reason)}</p>
            <p style="margin:0 0 24px;color:{MutedColor}">
              Endpoint: {Enc(alert.Hostname)}<br>
              Client: {Enc(alert.ClientName)}<br>
              Site: {Enc(alert.SiteName)}<br>
              Opened: {Enc(Utc(alert.OpenedAt))}{resolvedAt}
            </p>
            {Button(alert.EndpointUrl, "Open the endpoint")}
            """);

        var text = $"""
            Resolved

            {alert.Title}

            {reason}

            Endpoint:  {alert.Hostname}
            Client:    {alert.ClientName}
            Site:      {alert.SiteName}
            Opened:    {Utc(alert.OpenedAt)}{resolvedAtText}

            Open the endpoint: {alert.EndpointUrl}
            """;

        return new EmailContent(Subject($"Resolved: {alert.Title}"), html, Footer(text));
    }

    public static EmailContent LicenseExpiringSoon(string instanceFqdn, DateTime expiresAt, string licensingUrl)
    {
        var html = Layout($"""
            <p style="margin:0 0 16px">The Fleeto license of <strong>{Enc(instanceFqdn)}</strong> expires on {Enc(Date(expiresAt))}.</p>
            <p style="margin:0 0 24px">Load a renewed license in Settings, Licensing before that date. After expiry a grace period of 14 days starts; when it ends, every managed endpoint behaves as agent-only until a new license is loaded. Nothing is deleted.</p>
            {Button(licensingUrl, "Open licensing")}
            """);

        var text = $"""
            The Fleeto license of {instanceFqdn} expires on {Date(expiresAt)}.

            Load a renewed license in Settings, Licensing before that date. After expiry a grace period of 14 days starts; when it ends, every managed endpoint behaves as agent-only until a new license is loaded. Nothing is deleted.

            Open licensing: {licensingUrl}
            """;

        return new EmailContent(Subject($"The Fleeto license of {instanceFqdn} expires on {Date(expiresAt)}"), html, Footer(text));
    }

    public static EmailContent LicenseGracePeriod(string instanceFqdn, DateTime expiredAt, DateTime graceEndsAt, string licensingUrl)
    {
        var html = Layout($"""
            <p style="margin:0 0 16px">The Fleeto license of <strong>{Enc(instanceFqdn)}</strong> expired on {Enc(Date(expiredAt))}. Everything keeps working until {Enc(Date(graceEndsAt))}.</p>
            <p style="margin:0 0 24px">Load a new license in Settings, Licensing before the grace period ends. After that date every managed endpoint behaves as agent-only: no checks, alerts or configuration changes. Nothing is deleted.</p>
            {Button(licensingUrl, "Open licensing")}
            """);

        var text = $"""
            The Fleeto license of {instanceFqdn} expired on {Date(expiredAt)}. Everything keeps working until {Date(graceEndsAt)}.

            Load a new license in Settings, Licensing before the grace period ends. After that date every managed endpoint behaves as agent-only: no checks, alerts or configuration changes. Nothing is deleted.

            Open licensing: {licensingUrl}
            """;

        return new EmailContent(Subject($"The Fleeto license of {instanceFqdn} has expired, grace period ends on {Date(graceEndsAt)}"), html, Footer(text));
    }

    public static EmailContent LicenseExpired(string instanceFqdn, DateTime graceEndedAt, string licensingUrl)
    {
        var html = Layout($"""
            <p style="margin:0 0 16px">The grace period of the Fleeto license of <strong>{Enc(instanceFqdn)}</strong> ended on {Enc(Date(graceEndedAt))}.</p>
            <p style="margin:0 0 24px">Every managed endpoint now behaves as agent-only: checks, alerts and configuration changes are paused. Nothing is deleted. Load a new license in Settings, Licensing to restore managed behaviour.</p>
            {Button(licensingUrl, "Open licensing")}
            """);

        var text = $"""
            The grace period of the Fleeto license of {instanceFqdn} ended on {Date(graceEndedAt)}.

            Every managed endpoint now behaves as agent-only: checks, alerts and configuration changes are paused. Nothing is deleted. Load a new license in Settings, Licensing to restore managed behaviour.

            Open licensing: {licensingUrl}
            """;

        return new EmailContent(Subject($"The Fleeto license of {instanceFqdn} has expired"), html, Footer(text));
    }

    public static EmailContent BackupFailed(string instanceFqdn, DateTime startedAt, string error, string backupsUrl)
    {
        var html = Layout($"""
            <p style="margin:0 0 16px">The backup of <strong>{Enc(instanceFqdn)}</strong> that started at {Enc(Utc(startedAt))} failed.</p>
            <p style="margin:0 0 24px;padding:12px 16px;background:#FEF2F2;border:1px solid #FECACA;border-radius:8px;color:#B91C1C">{EncBlock(error)}</p>
            <p style="margin:0 0 24px">Check the destination and the backup public key in Settings, Backups, then start a backup from that page. The next nightly backup runs on schedule.</p>
            {Button(backupsUrl, "Open backups")}
            """);

        var text = $"""
            The backup of {instanceFqdn} that started at {Utc(startedAt)} failed.

            Error: {error}

            Check the destination and the backup public key in Settings, Backups, then start a backup from that page. The next nightly backup runs on schedule.

            Open backups: {backupsUrl}
            """;

        return new EmailContent(Subject($"The backup of {instanceFqdn} failed"), html, Footer(text));
    }

    /// <summary>A short message to confirm that email delivery works. Public so the web can reuse the same text.</summary>
    public static EmailContent TestEmail(string instanceFqdn)
    {
        var html = Layout($"""
            <p style="margin:0 0 16px">This is a test email from the Fleeto instance <strong>{Enc(instanceFqdn)}</strong>.</p>
            <p style="margin:0">Email delivery works. Alert notifications and license messages will reach this address.</p>
            """);

        var text = $"""
            This is a test email from the Fleeto instance {instanceFqdn}.

            Email delivery works. Alert notifications and license messages will reach this address.
            """;

        return new EmailContent(Subject($"Test email from Fleeto on {instanceFqdn}"), html, Footer(text));
    }

    /// <summary>One line, no control characters, at most 250 characters: safe as a mail header value.</summary>
    internal static string Subject(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        var subject = builder.ToString().Trim();
        return subject.Length <= 250 ? subject : subject[..247] + "...";
    }

    private static string AlertFacts(AlertEmailModel alert) => $"""
        <p style="margin:0 0 16px;color:{MutedColor}">
          Endpoint: {Enc(alert.Hostname)}<br>
          Client: {Enc(alert.ClientName)}<br>
          Site: {Enc(alert.SiteName)}<br>
          Opened: {Enc(Utc(alert.OpenedAt))}
        </p>
        """;

    private static string AlertFactsText(AlertEmailModel alert) => $"""
        Endpoint:  {alert.Hostname}
        Client:    {alert.ClientName}
        Site:      {alert.SiteName}
        Opened:    {Utc(alert.OpenedAt)}

        """;

    private static string DetailBlock(string detail) => string.IsNullOrWhiteSpace(detail)
        ? string.Empty
        : $"""<p style="margin:0 0 24px;padding:12px 16px;background:#FAFAF9;border:1px solid {BorderColor};border-radius:8px">{EncBlock(detail)}</p>""";

    private static string DetailText(string detail) => string.IsNullOrWhiteSpace(detail) ? string.Empty : $"Detail: {detail}\n";

    private static string SeverityChip(string severity)
    {
        var critical = string.Equals(severity, "Critical", StringComparison.OrdinalIgnoreCase);
        var color = critical ? CriticalColor : WarningColor;
        var background = critical ? "#FEF2F2" : "#FFFBEB";
        return $"""<span style="display:inline-block;padding:2px 10px;border-radius:12px;background:{background};color:{color};font-size:13px;font-weight:600">{Enc(severity)}</span>""";
    }

    private static string Enc(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    /// <summary>Encodes free text and keeps its line breaks readable.</summary>
    private static string EncBlock(string? value) => Enc(value).Replace("\r", string.Empty).Replace("\n", "<br>");

    /// <summary>Invariant formatting: the same email looks the same whichever server sends it.</summary>
    private static string Utc(DateTime value) =>
        DateTime.SpecifyKind(value, DateTimeKind.Utc).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";

    private static string Date(DateTime value) => value.ToString("d MMMM yyyy", CultureInfo.InvariantCulture);

    private static string Button(string link, string label)
    {
        // Links are built from the instance base URL; encode anyway so an odd URL cannot break out of the attribute.
        var safeLink = Enc(link);
        return $"""
            <table role="presentation" cellpadding="0" cellspacing="0" style="margin:0 0 8px">
              <tr><td style="border-radius:8px;background:{BrandColor}">
                <a href="{safeLink}" style="display:inline-block;padding:12px 24px;color:#ffffff;text-decoration:none;font-weight:600;font-size:15px">{Enc(label)}</a>
              </td></tr>
            </table>
            """;
    }

    private static string Footer(string text) => text.TrimEnd() + "\n\nThis is an automated message from Fleeto. Replies are not monitored.\n";

    private static string Layout(string bodyHtml) => $"""
        <!doctype html>
        <html><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"></head>
        <body style="margin:0;padding:24px;background:#FAFAF9;font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif;color:{TextColor};font-size:15px;line-height:1.6">
          <table role="presentation" cellpadding="0" cellspacing="0" width="100%" style="max-width:560px;margin:0 auto;background:#ffffff;border:1px solid {BorderColor};border-radius:12px">
            <tr><td style="padding:32px">
              <p style="margin:0 0 24px;font-size:18px;font-weight:600;color:{BrandColor}">{SenderName}</p>
              {bodyHtml}
            </td></tr>
          </table>
          <p style="max-width:560px;margin:16px auto 0;color:{MutedColor};font-size:12px;text-align:center">
            This is an automated message from Fleeto. Replies are not monitored.
          </p>
        </body></html>
        """;
}
