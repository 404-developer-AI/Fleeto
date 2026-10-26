using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Fleeto.Core.Domain;

namespace Fleeto.Infrastructure.Advisories;

/// <summary>One advisory as a feed gave it, cleaned: plain text, a checked link, CVE identifiers from its title and summary.</summary>
public sealed record ParsedAdvisory(string SourceId, string Title, string? Summary, DateTime PublishedAt, IReadOnlyList<string> Cves, string? Link);

/// <summary>What one fetch of a source gave, and how many items it left out.</summary>
public sealed record AdvisoryFeedParse(IReadOnlyList<ParsedAdvisory> Advisories, int Skipped);

/// <summary>
/// Reads the feed of a source of security advisories (0.8.0 step 0, VULNERABILITIES.md §2). The feed is untrusted input: no DTD and
/// no external entities, every text becomes one line of plain text, and a link is kept only when it points to the host of the source.
/// An item without a title, an id or a readable date is left out and counted, never fatal; a document that is not a feed throws
/// <see cref="InvalidDataException"/> so the previous advisories stay.
/// </summary>
public static class AdvisoryFeedParser
{
    private static readonly XmlReaderSettings ReaderSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = AdvisorySources.MaxDownloadBytes,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true
    };

    public static AdvisoryFeedParse Parse(AdvisorySourceInfo source, string text) => source.Format switch
    {
        AdvisoryFeedFormat.Rss => ParseRss(source, text),
        _ => throw new InvalidDataException($"Fleeto cannot read the format {source.Format}.")
    };

    private static AdvisoryFeedParse ParseRss(AdvisorySourceInfo source, string text)
    {
        XDocument document;
        try
        {
            using var reader = XmlReader.Create(new StringReader(text), ReaderSettings);
            document = XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            throw new InvalidDataException($"The feed is not valid XML ({ex.Message}). The advisories fetched before stay.", ex);
        }

        var channel = document.Root?.Name.LocalName == "rss" ? document.Root.Element("channel") : null;
        if (channel is null)
        {
            throw new InvalidDataException("The feed is not an RSS feed. The advisories fetched before stay.");
        }

        var advisories = new List<ParsedAdvisory>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var item in channel.Elements("item").Take(AdvisorySources.MaxItems))
        {
            var title = AdvisoryRules.PlainText(item.Element("title")?.Value, AdvisoryRules.MaxTitleLength);
            var rawLink = item.Element("link")?.Value.Trim();
            var id = (item.Element("guid")?.Value.Trim() is { Length: > 0 } guid ? guid : rawLink) ?? string.Empty;
            if (title.Length == 0 || id.Length is 0 or > AdvisoryRules.MaxSourceIdLength || !TryDate(item.Element("pubDate")?.Value, out var published) ||
                !seen.Add(id))
            {
                skipped++;
                continue;
            }

            var summary = AdvisoryRules.PlainText(item.Element("description")?.Value, AdvisoryRules.MaxSummaryLength);
            // A summary that only repeats the name of the feed (the CCB writes "CCB Advisories" in every item) is no summary.
            if (summary.Length == 0 || string.Equals(summary, AdvisoryRules.PlainText(channel.Element("description")?.Value, AdvisoryRules.MaxSummaryLength),
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(summary, AdvisoryRules.PlainText(channel.Element("title")?.Value, AdvisoryRules.MaxSummaryLength), StringComparison.OrdinalIgnoreCase))
            {
                summary = null;
            }

            advisories.Add(new ParsedAdvisory(id, title, summary, published, AdvisoryRules.Cves($"{title} {summary}"),
                AdvisoryRules.SafeLink(rawLink, source.Host)));
        }

        return new AdvisoryFeedParse(advisories, skipped);
    }

    /// <summary>
    /// An RFC 822 date as RSS writes it, in UTC. Accepts the two-digit year the CCB writes in its channel date as well; refuses a
    /// date that is not a date.
    /// </summary>
    internal static bool TryDate(string? value, out DateTime utc)
    {
        utc = default;
        var text = AdvisoryRules.PlainText(value, 100);
        if (text.Length == 0)
        {
            return false;
        }

        // "Mon, 26 Oct 2026 08:15:40 +0100": DateTimeOffset reads the offset only as "+01:00".
        var parts = text.Split(' ');
        if (parts.Length > 0 && parts[^1] is { Length: 5 } offset && offset[0] is '+' or '-' && offset.Skip(1).All(char.IsAsciiDigit))
        {
            parts[^1] = $"{offset[..3]}:{offset[3..]}";
            text = string.Join(' ', parts);
        }
        else if (parts.Length > 0 && parts[^1] is "GMT" or "UT" or "UTC" or "Z")
        {
            parts[^1] = "+00:00";
            text = string.Join(' ', parts);
        }

        string[] formats = ["ddd, d MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz", "ddd, d MMM yy HH:mm:ss zzz", "ddd, d MMM yyyy HH:mm zzz"];
        if (!DateTimeOffset.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var parsed))
        {
            return false;
        }

        utc = parsed.UtcDateTime;
        return utc.Year >= 2000;
    }
}
