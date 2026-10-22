namespace Fleeto.Core.Domain;

/// <summary>How a blocklist is written.</summary>
public enum IpFeedFormat
{
    /// <summary>One address or CIDR network per line; <c>#</c> and <c>;</c> start a comment.</summary>
    PlainList,
    /// <summary>One JSON object per line with a <c>cidr</c> field (Spamhaus DROP); other lines are metadata.</summary>
    JsonLinesCidr
}

/// <param name="Key">Stored in <c>IpFeeds</c>; never renamed once released.</param>
/// <param name="Urls">Fetched and combined; empty for CrowdSec, whose address is built from the integration of the admin.</param>
public sealed record IpFeedInfo(string Key, string Name, string Description, IReadOnlyList<string> Urls, IpFeedFormat Format, bool DefaultEnabled,
    string TermsUrl);

/// <summary>
/// The blocklists Fleeto can fetch (0.7.0, security monitor step 3). Only lists of addresses that serve nothing but attacks, so
/// blocking them costs no customer anything; a list that blocks too much (a whole provider) is left out. An allowed address is
/// never blocked by a list, and a list never refuses an agent that presents a valid certificate.
/// </summary>
public static class IpFeeds
{
    public const string SpamhausDrop = "spamhaus-drop";
    public const string FeodoTracker = "feodo-tracker";
    public const string FireHolLevel1 = "firehol-level1";
    public const string BlocklistDe = "blocklist-de";
    public const string CrowdSec = "crowdsec";

    /// <summary>The address CrowdSec serves a Console blocklist integration from; only the integration id comes from the admin.</summary>
    public const string CrowdSecContentUrl = "https://admin.api.crowdsec.net/v1/integrations/{0}/content";

    /// <summary>How often a list is fetched.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(6);

    /// <summary>How long after a failure the next attempt waits.</summary>
    public static readonly TimeSpan RetryInterval = TimeSpan.FromHours(1);

    /// <summary>The most networks one list may hold; a larger list is refused as broken.</summary>
    public const int MaxEntries = 500_000;

    /// <summary>A list that shrinks to less than this share of the one before is refused as broken, when the one before had at least <see cref="ShrinkFloor"/>.</summary>
    public const double MinShare = 0.5;

    public const int ShrinkFloor = 100;

    public static readonly IReadOnlyList<IpFeedInfo> All =
    [
        new(SpamhausDrop, "Spamhaus DROP", "Networks hijacked or leased by criminals. Spamhaus recommends dropping all traffic from them.",
            ["https://www.spamhaus.org/drop/drop_v4.json", "https://www.spamhaus.org/drop/drop_v6.json"], IpFeedFormat.JsonLinesCidr, true,
            "https://www.spamhaus.org/drop/"),
        new(FeodoTracker, "abuse.ch Feodo Tracker", "Command and control servers of botnets such as Emotet, Dridex and QakBot.",
            ["https://feodotracker.abuse.ch/downloads/ipblocklist_recommended.txt"], IpFeedFormat.PlainList, true,
            "https://feodotracker.abuse.ch/blocklist/"),
        new(FireHolLevel1, "FireHOL level 1", "A combination of lists with no false positives (Spamhaus, DShield, Feodo and others). Private ranges are left out.",
            ["https://iplists.firehol.org/files/firehol_level1.netset"], IpFeedFormat.PlainList, true, "https://iplists.firehol.org/?ipset=firehol_level1"),
        new(BlocklistDe, "blocklist.de", "Addresses that attacked servers of volunteers in the last 48 hours: SSH, mail, web logins. Larger and changes fast.",
            ["https://lists.blocklist.de/lists/all.txt"], IpFeedFormat.PlainList, false, "https://www.blocklist.de/en/index.html"),
        new(CrowdSec, "CrowdSec", "The blocklists your CrowdSec Console account subscribes to, through a firewall integration of the Console.",
            [], IpFeedFormat.PlainList, false, "https://www.crowdsec.net/"),
    ];

    public static IpFeedInfo? Find(string key) => All.FirstOrDefault(f => f.Key == key);
}
