using System.Text.RegularExpressions;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>How a source of security advisories is written.</summary>
public enum AdvisoryFeedFormat
{
    /// <summary>An RSS 2.0 feed: one item per advisory with a title, a link, a guid and a publication date.</summary>
    Rss
}

/// <param name="Key">Stored in <c>AdvisorySources</c> and <c>Advisories</c>; never renamed once released.</param>
/// <param name="Host">The only host a link of this source may point to; any other link is not shown.</param>
/// <param name="Language">The language the source publishes in (ISO 639-1).</param>
public sealed record AdvisorySourceInfo(string Key, string Name, string Description, string FeedUrl, AdvisoryFeedFormat Format, string Host,
    string Language, string HomeUrl);

/// <summary>
/// Security advisories on the dashboard (0.8.0 step 0, VULNERABILITIES.md §2): the warnings a national CSIRT or a vendor publishes,
/// information for the team, never matched to endpoints. A source is a kind, so another one is a parser, not a redesign. Everything
/// from a source is untrusted: shown as plain text, and the only link shown is one to the host of its source.
/// </summary>
public static class AdvisorySources
{
    public const string Ccb = "ccb";

    /// <summary>How often a source is fetched.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(1);

    /// <summary>A source that has not been fetched for this long is an instance health problem.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(48);

    /// <summary>The panel shows the advisories published in this period.</summary>
    public static readonly TimeSpan Shown = TimeSpan.FromDays(30);

    /// <summary>Advisories, and how they were handled, are kept this long after their publication.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(395);

    /// <summary>The largest feed Fleeto downloads.</summary>
    public const int MaxDownloadBytes = 2 * 1024 * 1024;

    /// <summary>The most items Fleeto reads from one feed.</summary>
    public const int MaxItems = 500;

    public static readonly IReadOnlyList<AdvisorySourceInfo> All =
    [
        // Only the RSS feed: the pages of the warnings answer 403 to a client that is not a browser (checked 2026-10-26), and the
        // feed carries no summary and no CVE identifiers.
        new(Ccb, "CCB", "Warnings of the Centre for Cybersecurity Belgium.", "https://ccb.belgium.be/advisories.xml", AdvisoryFeedFormat.Rss,
            "ccb.belgium.be", "en", "https://ccb.belgium.be/advisories"),
    ];

    public static AdvisorySourceInfo? Find(string key) => All.FirstOrDefault(s => s.Key == key);
}

/// <summary>Rules of security advisories (0.8.0 step 0).</summary>
public static partial class AdvisoryRules
{
    public const int MaxTitleLength = 500;
    public const int MaxSummaryLength = 2000;
    public const int MaxLinkLength = 1000;
    public const int MaxSourceIdLength = 1000;
    public const int MaxCves = 200;
    public const int MaxCommentLength = 1000;

    [GeneratedRegex(@"\bCVE-\d{4}-\d{4,7}\b", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex CvePattern();

    /// <summary>The CVE identifiers in a text, uppercase, distinct, in the order they appear, at most <see cref="MaxCves"/>.</summary>
    public static IReadOnlyList<string> Cves(string? text) =>
        string.IsNullOrEmpty(text)
            ? []
            : CvePattern().Matches(text).Select(m => m.Value.ToUpperInvariant()).Distinct(StringComparer.Ordinal).Take(MaxCves).ToList();

    /// <summary>
    /// The link to show for an advisory, or null: only an absolute https link to the host of its source (or a subdomain of it), so
    /// a source that is taken over cannot send a technician elsewhere.
    /// </summary>
    public static string? SafeLink(string? link, string host)
    {
        if (string.IsNullOrWhiteSpace(link) || link.Length > MaxLinkLength ||
            !Uri.TryCreate(link.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            return null;
        }

        var linkHost = uri.IdnHost.TrimEnd('.');
        return string.Equals(linkHost, host, StringComparison.OrdinalIgnoreCase) ||
               linkHost.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)
            ? uri.AbsoluteUri
            : null;
    }

    /// <summary>Text from a source as one line of plain text: control characters and runs of white space become one space.</summary>
    public static string PlainText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(Math.Min(text.Length, maxLength));
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                space = builder.Length > 0;
                continue;
            }

            if (space)
            {
                builder.Append(' ');
                space = false;
            }

            builder.Append(c);
        }

        return builder.Length > maxLength ? builder.ToString(0, maxLength - 1).TrimEnd() + "…" : builder.ToString();
    }

    /// <summary>Whether a source is due for a fetch.</summary>
    public static bool IsDue(DateTime? lastAttemptAt, DateTime now) => lastAttemptAt is null || now - lastAttemptAt >= AdvisorySources.RefreshInterval;

    /// <summary>
    /// The instance health problems of the sources: one per enabled source that could not be fetched for <see cref="AdvisorySources.StaleAfter"/>.
    /// A source counts from its first attempt, so a new instance does not report a problem before it tried.
    /// </summary>
    public static IReadOnlyList<HealthFinding> HealthFindings(IEnumerable<AdvisorySourceState> sources, DateTime now) =>
        sources.Where(s => s.Enabled && s.FailingSince is { } since && now - since >= AdvisorySources.StaleAfter)
            .Select(s =>
            {
                var name = AdvisorySources.Find(s.Key)?.Name ?? s.Key;
                var last = s.LastSuccessAt is { } success ? $"The advisories on the dashboard date from {success:yyyy-MM-dd HH:mm} UTC." : "No advisories were fetched yet.";
                return new HealthFinding($"advisories:{s.Key}", HealthComponent.Advisories, AlertSeverity.Warning,
                    $"The security advisories of {name} could not be fetched for 48 hours",
                    $"{last} {s.Problem ?? "The source could not be reached."} Check that the workers can reach {AdvisorySources.Find(s.Key)?.Host ?? "the source"}.");
            })
            .ToList();
}

/// <summary>The state of a source as the rules judge it.</summary>
public sealed record AdvisorySourceState(string Key, bool Enabled, DateTime? LastSuccessAt, DateTime? FailingSince, string? Problem);
