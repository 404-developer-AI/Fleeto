using System.Text.Json;
using System.Text.Json.Serialization;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// One missing update of a patch scan (0.9.0), as stored in <see cref="PatchScan.UpdatesJson"/>. Short JSON names: a scan holds
/// hundreds of them.
/// </summary>
/// <param name="Id">Linux: <c>name:architecture</c>.</param>
/// <param name="Version">The version the upgrade would install; empty for a removal.</param>
/// <param name="InstalledVersion">The version installed now; empty for a new dependency.</param>
/// <param name="Security">From a security archive or advisory.</param>
/// <param name="Severity">The severity the tool named (AlmaLinux updateinfo, MsrcSeverity on Windows), empty when none.</param>
/// <param name="Classification">Windows (step 2): the update classification of the Windows Update Agent, such as Security Updates.</param>
/// <param name="KbNumbers">Windows: the KB numbers, as KB5031354.</param>
/// <param name="ReleasedAt">Windows: when Microsoft released this revision.</param>
/// <param name="Cves">Windows: the CVEs it fixes.</param>
public sealed record PatchScanUpdate(
    [property: JsonPropertyName("i")] string Id,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("v")] string Version,
    [property: JsonPropertyName("iv")] string InstalledVersion,
    [property: JsonPropertyName("s")] bool Security,
    [property: JsonPropertyName("o")] string Origin,
    [property: JsonPropertyName("a")] string Advisory,
    [property: JsonPropertyName("sv")] string Severity,
    [property: JsonPropertyName("h")] bool Held,
    [property: JsonPropertyName("r")] bool Removal,
    [property: JsonPropertyName("c")] string Classification = "",
    [property: JsonPropertyName("kb")] IReadOnlyList<string>? KbNumbers = null,
    [property: JsonPropertyName("ra")] DateTime? ReleasedAt = null,
    [property: JsonPropertyName("cv")] IReadOnlyList<string>? Cves = null,
    [property: JsonPropertyName("sz")] long SizeBytes = 0,
    [property: JsonPropertyName("dl")] bool Downloaded = false,
    [property: JsonPropertyName("rr")] bool RestartRequired = false,
    [property: JsonPropertyName("m")] bool Mandatory = false,
    [property: JsonPropertyName("dr")] bool IsDriver = false,
    [property: JsonPropertyName("fu")] bool IsFeatureUpdate = false);

/// <summary>An open vulnerability finding of an endpoint as the patch scan writer joins it (0.9.0, <c>PATCHING.md</c> §4).</summary>
public sealed record PatchFinding(string Package, IReadOnlyList<string> Binaries, string? FixedVersion, string Cve, VulnerabilitySeverity Severity,
    string? Advisory, DateTime? PublishedAt);

/// <summary>What a missing Linux package update fixes of the open findings of its endpoint.</summary>
public sealed record PatchFix(PatchSeverity Severity, IReadOnlyList<string> Cves, string Advisory, DateOnly? ReleaseDate);

/// <summary>
/// The rules of patch scans by the Fleeto agent (0.9.0, <c>PATCHING.md</c> §4 and §7): what a scan may hold, when scans run, and what
/// the workers add on Linux from the vulnerability findings the instance already keeps. Nothing here leaves the instance.
/// </summary>
public static class PatchScanRules
{
    /// <summary>The first agent that scans for missing updates. An older agent gets no scan interval, and the Patches tab says why.</summary>
    public const string MinimumAgentVersion = "0.9.0-alpha.1";

    /// <summary>Hours between two scheduled scans (decision 16).</summary>
    public const uint DefaultIntervalHours = 6;

    /// <summary>Updates one scan may hold; the agent cuts the list and says so.</summary>
    public const int MaxUpdates = 2000;

    public const int MaxProblemLength = 1000;
    public const int MaxNameLength = 200;
    public const int MaxVersionLength = 100;
    public const int MaxOriginLength = 100;
    public const int MaxAdvisoryLength = 100;
    public const int MaxServerLength = 255;
    public const int MaxTitleLength = 300;

    /// <summary>A "Scan now" within this time of the previous one is refused, as the agent would ignore it.</summary>
    public static readonly TimeSpan RequestGap = TimeSpan.FromMinutes(15);

    /// <summary>A request the agent did not receive within this time is no longer delivered.</summary>
    public static readonly TimeSpan RequestLifetime = TimeSpan.FromMinutes(15);

    /// <summary>Scans an endpoint may send in 24 hours; more are acknowledged and not stored, so a broken agent cannot fill the database.</summary>
    public const int MaxScansPerEndpointPerDay = 200;

    /// <summary>Older scans than this go; the newest of every endpoint stays.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);

    /// <summary>A scan this old while the agent is online is stale, and so is its patch state: the patch state alert opens.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromDays(7);

    /// <summary>The tools a scan can come from, as the agent names them.</summary>
    public static readonly IReadOnlyList<string> Tools = ["apt", "dnf", "windows_update"];

    /// <summary>The update type of a Linux update, in the words of <see cref="PatchPolicyRules.UpdateTypes"/>.</summary>
    public const string SecurityType = "Security Updates";

    public const string RegularType = "Regular Updates";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>True when the agent version scans for missing updates.</summary>
    public static bool AgentScans(string? agentVersion) =>
        SemanticVersion.TryParse(agentVersion, out _) && !SemanticVersion.IsOlder(agentVersion, MinimumAgentVersion);

    /// <summary>True when the platform scans: Linux with apt or dnf (step 1) and Windows with the Windows Update Agent (step 2).</summary>
    public static bool PlatformScans(string? osPlatform) => osPlatform is "linux" or "windows";

    /// <summary>Where a Windows scan searched, as the agent names it.</summary>
    public static readonly IReadOnlyList<string> Sources = ["windows_update", "microsoft_update", "wsus"];

    /// <summary>The source of a scan in words for the Patches tab: "Windows Update", "Microsoft Update", "WSUS (wsus01:8530)".</summary>
    public static string? DescribeSource(string? source, string? server) => source switch
    {
        "windows_update" => "Windows Update",
        "microsoft_update" => "Microsoft Update",
        "wsus" => string.IsNullOrEmpty(server) ? "WSUS" : $"WSUS ({server})",
        _ => null
    };

    /// <summary>
    /// The update type of a Windows update in the words of <see cref="PatchPolicyRules.UpdateTypes"/>, from its update classification in
    /// the Windows Update Agent, so a patch policy filters both engines alike.
    /// </summary>
    public static string WindowsUpdateType(string? classification) => classification?.Trim() switch
    {
        "Security Updates" => "Security Updates",
        "Critical Updates" => "Critical - Non-Security",
        "Updates" => "Regular Updates",
        "Update Rollups" => "Update Rollups",
        "Definition Updates" => "Definition Updates",
        "Feature Packs" => "Feature Updates",
        "Service Packs" => "Service Packs",
        "Tools" => "Tools",
        "Drivers" => "Drivers",
        "Upgrades" => "Upgrades",
        _ => "Unknown"
    };

    /// <summary>
    /// The scan interval for the signed configuration: <see cref="DefaultIntervalHours"/> for a managed endpoint whose engine is Fleeto
    /// on a platform that scans; 0 otherwise, so an agent-only configuration never carries one.
    /// </summary>
    public static uint IntervalHours(EndpointTier effectiveTier, PatchEngineResult engine, string? osPlatform) =>
        effectiveTier == EndpointTier.Managed && engine.Engine == EndpointPatchEngine.Fleeto && PlatformScans(osPlatform) ? DefaultIntervalHours : 0;

    /// <summary>
    /// Why an endpoint whose engine is Fleeto is not scanned, for the Patches tab; null when it is. The engine itself and its reasons
    /// are <see cref="PatchEngineRules"/>.
    /// </summary>
    public static string? Problem(string? osPlatform, string? agentVersion) =>
        !PlatformScans(osPlatform)
            ? "Fleeto does not read the missing updates of this operating system."
            : !AgentScans(agentVersion)
                ? $"Update the agent to {MinimumAgentVersion} or newer to see the missing updates of this endpoint."
                : null;

    public static string Serialize(IEnumerable<PatchScanUpdate> updates) => JsonSerializer.Serialize(updates, Json);

    /// <summary>Reads stored updates; empty for a missing or damaged value.</summary>
    public static IReadOnlyList<PatchScanUpdate> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<PatchScanUpdate>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The severity AlmaLinux updateinfo names, in Fleeto's words; Unspecified for anything else.</summary>
    public static PatchSeverity SeverityOf(string? tool) => tool?.Trim().ToLowerInvariant() switch
    {
        "critical" => PatchSeverity.Critical,
        "important" => PatchSeverity.Important,
        "moderate" => PatchSeverity.Moderate,
        "low" => PatchSeverity.Low,
        _ => PatchSeverity.Unspecified
    };

    /// <summary>The severity of a vulnerability finding as a patch severity.</summary>
    public static PatchSeverity SeverityOf(VulnerabilitySeverity severity) => severity switch
    {
        VulnerabilitySeverity.Critical => PatchSeverity.Critical,
        VulnerabilitySeverity.High => PatchSeverity.Important,
        VulnerabilitySeverity.Medium => PatchSeverity.Moderate,
        VulnerabilitySeverity.Low => PatchSeverity.Low,
        _ => PatchSeverity.Unspecified
    };

    /// <summary>
    /// What a missing package update fixes (<c>PATCHING.md</c> §4): every open finding of the endpoint on the package (by source package
    /// or one of its binaries) whose fixed version is at or below the version the update installs. The update then carries their CVEs,
    /// the highest of their severities and the severity the tool named, the advisory the tool named or else the one of the finding, and
    /// the earliest published date of their advisories as its release date. Null when it fixes nothing.
    /// </summary>
    public static PatchFix? FixOf(PatchScanUpdate update, string tool, IReadOnlyList<PatchFinding> findings)
    {
        if (update.Removal || update.Version.Length == 0)
        {
            return null;
        }

        var compare = tool == "dnf" ? (Func<string, string, int>)PackageVersions.CompareRpm : PackageVersions.CompareDpkg;
        var fixedBy = findings.Where(f => f.FixedVersion is { Length: > 0 } fixedVersion &&
                                          (string.Equals(f.Package, update.Name, StringComparison.Ordinal) ||
                                           f.Binaries.Contains(update.Name, StringComparer.Ordinal)) &&
                                          SafeCompare(compare, fixedVersion, update.Version) <= 0)
            .ToList();
        var toolSeverity = SeverityOf(update.Severity);
        if (fixedBy.Count == 0)
        {
            return update.Security || toolSeverity != PatchSeverity.Unspecified || update.Advisory.Length > 0
                ? new PatchFix(toolSeverity, [], update.Advisory, null)
                : null;
        }

        var severity = fixedBy.Select(f => SeverityOf(f.Severity)).Append(toolSeverity).Max();
        var cves = fixedBy.Select(f => f.Cve).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(200).ToList();
        var advisory = update.Advisory.Length > 0 ? update.Advisory : fixedBy.Select(f => f.Advisory).FirstOrDefault(a => !string.IsNullOrEmpty(a)) ?? string.Empty;
        var published = fixedBy.Where(f => f.PublishedAt is not null).Select(f => f.PublishedAt!.Value).DefaultIfEmpty().Min();
        return new PatchFix(severity, cves, advisory, published == default ? null : DateOnly.FromDateTime(published));
    }

    /// <summary>A version that cannot be compared counts as newer than the update, so it never claims a fix it cannot prove.</summary>
    private static int SafeCompare(Func<string, string, int> compare, string a, string b)
    {
        try
        {
            return compare(a, b);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or OverflowException)
        {
            return 1;
        }
    }
}
