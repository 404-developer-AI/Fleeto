using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleeto.Core.Domain;

/// <summary>
/// The distribution and the state of its package manager as the agent reported them (0.8.0, VULNERABILITIES.md §6), stored as
/// <c>InventorySnapshots.PackageInfoJson</c>. Null in the snapshot when an older agent or another platform did not report it.
/// </summary>
public sealed record PackageState(
    string DistributionId,
    string DistributionVersion,
    string? Variant,
    string? VariantVersion,
    string PackageManager,
    IReadOnlyList<string> UbuntuProServices,
    bool LivepatchActive,
    IReadOnlyList<string> LivepatchFixes,
    bool RestartRequired,
    string? PackageLists,
    DateTime? PackageListsUpdatedAt,
    IReadOnlyList<string>? ModuleStreams = null);

/// <summary>One installed package as matching reads it from <c>InventorySnapshots.SoftwareJson</c>.</summary>
public sealed record InstalledPackage(
    string Name,
    string Version,
    string? SourceName,
    string? SourceVersion,
    int Epoch,
    string? Architecture,
    string? Origin,
    string? ModuleStream);

/// <summary>Reads and writes the package parts of an inventory, so the gateway and the workers agree on their shape.</summary>
public static class PackageInventory
{
    public const string Dpkg = "dpkg";
    public const string Rpm = "rpm";

    /// <summary>The origin the agent reports for a Debian package that is in no apt list.</summary>
    public const string LocalOrigin = "local";

    public const int MaxText = 200;
    public const int MaxServices = 20;
    public const int MaxFixes = 2000;
    public const int MaxModuleStreams = 100;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Serialize(PackageState state) => JsonSerializer.Serialize(state, Json);

    /// <summary>The stored state, or null when there is none or it cannot be read.</summary>
    public static PackageState? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<PackageState>(json, Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The packages of a software list; items without a name or version are left out. An unreadable list is empty.</summary>
    public static IReadOnlyList<InstalledPackage> ParseSoftware(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return (JsonSerializer.Deserialize<List<InstalledPackage>>(json, Json) ?? [])
                .Where(p => !string.IsNullOrEmpty(p.Name) && !string.IsNullOrEmpty(p.Version))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
