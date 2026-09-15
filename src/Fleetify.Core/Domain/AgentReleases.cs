using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleetify.Core.Entities;

namespace Fleetify.Core.Domain;

/// <summary>
/// A semantic version <c>MAJOR.MINOR.PATCH[-PRERELEASE]</c> as Fleeto uses it (CLAUDE.md, Versioning): build metadata is ignored, a
/// pre-release orders below its release, and pre-release identifiers compare numerically when both are numbers. The Go agent
/// (<c>internal/release</c>) and install.sh follow the same rules.
/// </summary>
public sealed partial record SemanticVersion(int Major, int Minor, int Patch, string PreRelease) : IComparable<SemanticVersion>
{
    [GeneratedRegex(@"^(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})\.(0|[1-9]\d{0,8})(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z.-]+)?$")]
    private static partial Regex Pattern();

    public static bool TryParse(string? value, out SemanticVersion version)
    {
        version = null!;
        if (string.IsNullOrEmpty(value) || value.Length > 50)
        {
            return false;
        }

        var match = Pattern().Match(value);
        if (!match.Success)
        {
            return false;
        }

        version = new SemanticVersion(int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture), match.Groups[4].Value);
        return true;
    }

    public int CompareTo(SemanticVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var core = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (core != 0)
        {
            return core;
        }

        if (PreRelease.Length == 0 || other.PreRelease.Length == 0)
        {
            return (PreRelease.Length == 0 ? 1 : 0) - (other.PreRelease.Length == 0 ? 1 : 0);
        }

        var left = PreRelease.Split('.');
        var right = other.PreRelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var leftNumeric = long.TryParse(left[i], NumberStyles.None, CultureInfo.InvariantCulture, out var leftNumber);
            var rightNumeric = long.TryParse(right[i], NumberStyles.None, CultureInfo.InvariantCulture, out var rightNumber);
            var result = (leftNumeric, rightNumeric) switch
            {
                (true, true) => leftNumber.CompareTo(rightNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => string.CompareOrdinal(left[i], right[i])
            };
            if (result != 0)
            {
                return Math.Sign(result);
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    /// <summary>True when <paramref name="installed"/> is a valid version lower than <paramref name="release"/>; unknown versions are not "out of date".</summary>
    public static bool IsOlder(string? installed, string? release) =>
        TryParse(installed, out var a) && TryParse(release, out var b) && a.CompareTo(b) < 0;

    public override string ToString() => $"{Major}.{Minor}.{Patch}{(PreRelease.Length > 0 ? "-" + PreRelease : string.Empty)}";
}

/// <summary>One agent binary in a release manifest.</summary>
public sealed record AgentBinary(AgentComponent Component, string Platform, string Architecture, string File, string Sha256, long Size);

/// <summary>
/// The part of the signed release manifest (<c>manifest.json</c>, written by <c>fleetify-tool release manifest</c>) that concerns the agent
/// binaries. The signature over the exact bytes is checked separately (release public keys); this only reads and validates the shape.
/// </summary>
public sealed partial record ReleaseManifest(string Version, IReadOnlyList<AgentBinary> AgentBinaries)
{
    public const long MaxBinaryBytes = 128L * 1024 * 1024;

    [GeneratedRegex("^[a-z0-9]{1,16}-[a-z0-9]{1,16}/fleetify-(agent|watchdog)(\\.exe)?$")]
    private static partial Regex FilePattern();

    [GeneratedRegex("^[0-9a-f]{64}$")]
    private static partial Regex Sha256Pattern();

    /// <summary>Parses a manifest. Returns null with a reason when it is not a format-1 manifest with valid agent binaries.</summary>
    public static ReleaseManifest? TryParse(ReadOnlySpan<byte> json, out string? problem)
    {
        problem = null;
        try
        {
            using var document = JsonDocument.Parse(json.ToArray(), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("formatVersion", out var format) || format.ValueKind != JsonValueKind.Number ||
                format.GetInt32() != 1)
            {
                problem = "The release manifest has an unsupported format.";
                return null;
            }

            var version = root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            if (!SemanticVersion.TryParse(version, out _))
            {
                problem = "The release manifest has no valid version.";
                return null;
            }

            var binaries = new List<AgentBinary>();
            if (root.TryGetProperty("agentBinaries", out var list))
            {
                if (list.ValueKind != JsonValueKind.Array || list.GetArrayLength() > 32)
                {
                    problem = "The agent binaries of the release manifest are invalid.";
                    return null;
                }

                foreach (var item in list.EnumerateArray())
                {
                    var binary = ReadBinary(item);
                    if (binary is null || binaries.Any(b => b.Component == binary.Component && b.Platform == binary.Platform && b.Architecture == binary.Architecture))
                    {
                        problem = "An agent binary in the release manifest is invalid or listed twice.";
                        return null;
                    }

                    binaries.Add(binary);
                }
            }

            return new ReleaseManifest(version!, binaries);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            problem = "The release manifest is not valid JSON.";
            return null;
        }
    }

    private static AgentBinary? ReadBinary(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        string? Text(string name) => item.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var component = Text("component") switch { "agent" => (AgentComponent?)AgentComponent.Agent, "watchdog" => AgentComponent.Watchdog, _ => null };
        var platform = Text("platform");
        var architecture = Text("architecture");
        var file = Text("file");
        var sha = Text("sha256");
        if (component is null || platform is null || architecture is null || file is null || sha is null || !FilePattern().IsMatch(file) ||
            !Sha256Pattern().IsMatch(sha) || file != ExpectedFile(component.Value, platform, architecture) ||
            !item.TryGetProperty("size", out var sizeElement) || !sizeElement.TryGetInt64(out var size) || size <= 0 || size > MaxBinaryBytes)
        {
            return null;
        }

        return new AgentBinary(component.Value, platform, architecture, file, sha, size);
    }

    /// <summary>The only file name a binary may have: <c>&lt;platform&gt;-&lt;architecture&gt;/fleetify-&lt;component&gt;[.exe]</c>.</summary>
    public static string ExpectedFile(AgentComponent component, string platform, string architecture) =>
        $"{platform}-{architecture}/fleetify-{(component == AgentComponent.Agent ? "agent" : "watchdog")}{(platform == "windows" ? ".exe" : string.Empty)}";
}

/// <summary>The update ring rules (decided 2026-09-15): Preview at once, Standard after 7 days, Delayed after 14 days, from installation.</summary>
public static class UpdateRings
{
    public static TimeSpan Delay(UpdateRing ring) => ring switch
    {
        UpdateRing.Preview => TimeSpan.Zero,
        UpdateRing.Standard => TimeSpan.FromDays(7),
        UpdateRing.Delayed => TimeSpan.FromDays(14),
        _ => throw new ArgumentOutOfRangeException(nameof(ring), ring, null)
    };

    /// <summary>When endpoints of <paramref name="ring"/> may start installing the release: the ring delay, or earlier when released to all.</summary>
    public static DateTime AvailableAt(AgentRelease release, UpdateRing ring)
    {
        var byRing = release.InstalledAt + Delay(ring);
        return release.ReleasedToAllAt is { } all && all < byRing ? all : byRing;
    }

    /// <summary>True when an endpoint of <paramref name="ring"/> may install the release now.</summary>
    public static bool IsAllowed(AgentRelease release, UpdateRing ring, DateTime now) =>
        release.PausedAt is null && AvailableAt(release, ring) <= now;
}
