using System.Text.Json;
using Fleeto.Core.Domain;

namespace Fleeto.Infrastructure.Security;

/// <summary>What one fetch of a blocklist gave: the networks Fleeto may block, and how many lines it left out.</summary>
public sealed record IpFeedParse(IReadOnlyList<string> Networks, int Skipped);

/// <summary>
/// Reads a blocklist (0.7.0, security monitor step 3) into canonical CIDR networks. Leaves out what may never be blocked, whatever
/// a list says: private and reserved ranges (FireHOL level1 lists 10.0.0.0/8 and 172.16.0.0/12, where the containers of the
/// instance live) and networks wider than /8 or /19. Unreadable lines are counted, never fatal: one bad line does not cost the list.
/// </summary>
public static class IpFeedParser
{
    public static IpFeedParse Parse(IpFeedFormat format, string text)
    {
        var networks = new HashSet<string>(StringComparer.Ordinal);
        var skipped = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            var candidate = format == IpFeedFormat.JsonLinesCidr ? Cidr(line) : Token(line);
            if (candidate is null)
            {
                // A metadata line of a JSON list is not an unreadable line.
                if (format == IpFeedFormat.PlainList)
                {
                    skipped++;
                }

                continue;
            }

            if (!IpNetworks.TryParse(candidate, out var network) || IpNetworks.TouchesReserved(network) || IpNetworks.IsTooWide(network))
            {
                skipped++;
                continue;
            }

            networks.Add(IpNetworks.Format(network));
        }

        return new IpFeedParse([.. networks], skipped);
    }

    /// <summary>The first word of a line, without a trailing comment.</summary>
    private static string? Token(string line)
    {
        var end = line.IndexOfAny([' ', '\t', '#', ';', ',']);
        var token = end < 0 ? line : line[..end];
        return token.Length == 0 ? null : token;
    }

    private static string? Cidr(string line)
    {
        try
        {
            using var document = JsonDocument.Parse(line);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("cidr", out var cidr) && cidr.ValueKind == JsonValueKind.String
                ? cidr.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Why a new list must not replace the one before, or null when it may: too large, or shrunk to less than half of a list that
    /// held at least <see cref="IpFeeds.ShrinkFloor"/> networks, which means the source is broken rather than the internet cleaner.
    /// </summary>
    public static string? Refusal(int previous, int next)
    {
        if (next > IpFeeds.MaxEntries)
        {
            return $"The list holds {next:N0} networks, more than the {IpFeeds.MaxEntries:N0} Fleeto accepts. The previous list stays in use.";
        }

        return previous >= IpFeeds.ShrinkFloor && next < previous * IpFeeds.MinShare
            ? $"The list shrank from {previous:N0} to {next:N0} networks, which looks like a broken download. The previous list stays in use; Fleeto tries again later."
            : null;
    }
}
