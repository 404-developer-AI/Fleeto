using Fleeto.Core.Domain;

namespace Fleeto.Core.Entities;

// Vulnerabilities on Linux endpoints (0.8.0, VULNERABILITIES.md §8). The feeds and their records are public data and instance-wide;
// the findings and summaries belong to an endpoint and carry its ClientId like every other endpoint table.

/// <summary>
/// A feed of vulnerability data and the state of its last fetch: <c>ubuntu:22.04</c> (one OVAL file per release), <c>debian</c> (the
/// security tracker) or <c>almalinux</c> (the OSV zip). <see cref="Version"/> rises with every change of its records, so the workers
/// know which endpoints to match again.
/// </summary>
public class VulnerabilityFeed
{
    public string Key { get; set; } = string.Empty;
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>The last time its records changed.</summary>
    public DateTime? LastChangedAt { get; set; }

    /// <summary>The first failed attempt since the last success; null while the feed works.</summary>
    public DateTime? FailingSince { get; set; }

    /// <summary>Why the last attempt failed or its data was refused, with the next step; null when it worked.</summary>
    public string? Problem { get; set; }

    public int RecordCount { get; set; }

    /// <summary>The ETag and Last-Modified of the last download, so an unchanged file is not downloaded again.</summary>
    public string? ETag { get; set; }

    public string? LastModified { get; set; }

    /// <summary>What the records were made for (the releases, the kernel flavours); a change fetches the feed again.</summary>
    public string? Scope { get; set; }

    public long Version { get; set; }

    /// <summary>An admin asked for a fetch now (Settings, Vulnerabilities); the workers fetch the feed and clear it.</summary>
    public DateTime? RefreshRequestedAt { get; set; }
}

/// <summary>One statement of a feed: a CVE on one package of one release (<see cref="VulnerabilityRecord"/>). Replaced as a whole per feed.</summary>
public class VulnerabilityFeedRecord
{
    public long Id { get; set; }
    public string Feed { get; set; } = string.Empty;
    public string Distribution { get; set; } = string.Empty;
    public string Release { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;
    public string Cve { get; set; } = string.Empty;
    public VulnerabilityRecordStatus Status { get; set; }
    public string? FixedVersion { get; set; }
    public string? ProService { get; set; }
    public VulnerabilitySeverity Severity { get; set; }
    public string? SourceSeverity { get; set; }
    public decimal? Cvss { get; set; }
    public string? Advisory { get; set; }
    public DateTime? PublishedAt { get; set; }
    public bool IsKernel { get; set; }
    public string? KernelPattern { get; set; }

    /// <summary>AlmaLinux: the module streams the errata names ("container-tools:rhel8", comma separated), for a fixed version of a module build.</summary>
    public string? ModuleStreams { get; set; }
}

/// <summary>Ubuntu's priority of a CVE, which a Debian record without an urgency of its own borrows (VULNERABILITIES.md §5).</summary>
public class VulnerabilityCve
{
    public string Cve { get; set; } = string.Empty;
    public VulnerabilitySeverity UbuntuPriority { get; set; }
}

/// <summary>
/// A finding: one CVE on one source package of one endpoint (VULNERABILITIES.md §7). Open while <see cref="EndedAt"/> is null;
/// an ended finding keeps how it ended, so the history never lies.
/// </summary>
public class EndpointVulnerability
{
    public const int MaxBinaries = 50;

    public long Id { get; set; }
    public Guid ClientId { get; set; }
    public Guid EndpointId { get; set; }
    public string Cve { get; set; } = string.Empty;
    public string Package { get; set; } = string.Empty;
    public string InstalledVersion { get; set; } = string.Empty;
    public string? FixedVersion { get; set; }
    public VulnerabilityFixState State { get; set; }
    public VulnerabilitySeverity Severity { get; set; }
    public string? SourceSeverity { get; set; }
    public decimal? Cvss { get; set; }
    public string? Advisory { get; set; }
    public string[] Binaries { get; set; } = [];
    public bool IsKernel { get; set; }
    public DateTime FirstSeenAt { get; set; }
    public DateTime? EndedAt { get; set; }
    public VulnerabilityEndReason? EndReason { get; set; }
}

/// <summary>
/// One CVE in one site, counted over the open findings of its endpoints (0.8.0 step 2): what the overview per client and site reads,
/// because counting endpoints per CVE over every finding of an instance takes seconds. The workers rebuild the table after matching,
/// at most every few minutes, so it can lag the findings by that much; <see cref="CountedAt"/> says how much. A row is visible only
/// to a caller that sees the whole site, so a user with part of a site never sees counts of endpoints it cannot see.
/// </summary>
public class VulnerabilityCveCount
{
    public Guid ClientId { get; set; }
    public Guid SiteId { get; set; }
    public string Cve { get; set; } = string.Empty;

    /// <summary>The most severe finding of the CVE in the site, as a rank: 4 critical, 3 high, 2 medium, 1 low, 0 unknown.</summary>
    public int Rank { get; set; }

    /// <summary>The most severe finding with a fix the endpoint can install, as a rank; null when none has one.</summary>
    public int? FixableRank { get; set; }

    /// <summary>Endpoints with the CVE open (fixable, without a fix, or fixed only in Ubuntu Pro).</summary>
    public int Endpoints { get; set; }

    /// <summary>Endpoints on which a fix of the CVE can be installed.</summary>
    public int FixableEndpoints { get; set; }

    /// <summary>The source packages the CVE affects in the site.</summary>
    public string[] Packages { get; set; } = [];

    public DateTime CountedAt { get; set; }
}

/// <summary>
/// The vulnerabilities of one endpoint in numbers (VULNERABILITIES.md §8): what the dashboard tile and the overview per client read,
/// never the findings themselves. The counts per severity are of open findings with a fix the endpoint can install.
/// </summary>
public class EndpointVulnerabilitySummary
{
    public Guid EndpointId { get; set; }
    public Guid ClientId { get; set; }
    public VulnerabilityCoverage Coverage { get; set; }
    public string? Distribution { get; set; }
    public string? Release { get; set; }
    public int Critical { get; set; }
    public int High { get; set; }
    public int Medium { get; set; }
    public int Low { get; set; }
    public int Unknown { get; set; }

    /// <summary>Open findings without a fix yet.</summary>
    public int NoFix { get; set; }

    /// <summary>Open findings whose fix only Ubuntu Pro ships, on an endpoint without the service.</summary>
    public int ProOnly { get; set; }

    /// <summary>Findings the distribution does not count as open.</summary>
    public int NotOpen { get; set; }

    /// <summary>Packages from other repositories, which the feeds do not cover.</summary>
    public int NotCovered { get; set; }

    public bool RestartRequired { get; set; }

    /// <summary>"ok", "missing" or "unreadable" for the apt lists; null on rpm.</summary>
    public string? PackageLists { get; set; }

    public DateTime? PackageListsUpdatedAt { get; set; }

    /// <summary>The packages the feeds do not cover, as JSON (<see cref="Vulnerabilities.SerializeNotCovered"/>), at most
    /// <see cref="Vulnerabilities.MaxNotCoveredListed"/> of them; <see cref="NotCovered"/> counts them all.</summary>
    public string? NotCoveredJson { get; set; }

    /// <summary>The inventory and the feed versions this summary was matched from; a change of either matches the endpoint again.</summary>
    public string InventoryHash { get; set; } = string.Empty;

    public string FeedVersion { get; set; } = string.Empty;
    public DateTime MatchedAt { get; set; }
}
