using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fleeto.Core.Domain;

/// <summary>A folder of a storage scan (0.6.0). Short JSON names: a scan holds hundreds of them. The path can hold a person's name.</summary>
/// <param name="SizeBytes">Size on disk of everything below the folder.</param>
/// <param name="FileCount">Files below the folder, at every depth.</param>
/// <param name="FolderCount">Folders below the folder, at every depth.</param>
public sealed record StorageFolderEntry(
    [property: JsonPropertyName("p")] string Path,
    [property: JsonPropertyName("s")] long SizeBytes,
    [property: JsonPropertyName("f")] long FileCount,
    [property: JsonPropertyName("d")] long FolderCount);

/// <summary>A file of a storage scan (0.6.0). The path can hold a person's name.</summary>
public sealed record StorageFileEntry(
    [property: JsonPropertyName("p")] string Path,
    [property: JsonPropertyName("s")] long SizeBytes,
    [property: JsonPropertyName("m")] DateTime? ModifiedAt);

/// <summary>A stored scan as the growth rules need it.</summary>
public sealed record StorageScanPoint(DateTime ReceivedAt, IReadOnlyList<StorageFolderEntry> Folders);

/// <summary>How much a volume grew between two scans, and the folder that grew most, if one stands out.</summary>
/// <param name="Folder">The deepest folder that holds at least half of the growth of the folder above it; null when the root does.</param>
public sealed record StorageGrowth(long GrowthBytes, double Days, string? Folder, long FolderGrowthBytes);

/// <summary>Rules of storage analysis (0.6.0): what a scan may hold, when scans run, and how growth is measured.</summary>
public static class StorageRules
{
    /// <summary>The first agent that scans storage. An older agent never gets a scan request, and "Scan now" says why.</summary>
    public const string MinimumAgentVersion = "0.6.0-alpha.10";

    public const int DefaultScanIntervalHours = 24;

    /// <summary>The intervals a policy offers, in hours; 0 turns scheduled scans off.</summary>
    public static readonly IReadOnlyList<int> ScanIntervalChoices = [0, 6, 12, 24, 72, 168];

    public const int MaxFolders = 300;
    public const int MaxFiles = 50;
    public const int MaxPathLength = 1024;
    public const int MaxVolumeLength = 256;

    /// <summary>Reports the gateway stores per endpoint in 24 hours; more are acked and dropped, so a broken agent cannot fill the disk.</summary>
    public const int MaxScansPerEndpointPerDay = 100;

    /// <summary>A technician can ask for a scan of an endpoint once per this gap; the agent ignores requests closer together anyway.</summary>
    public static readonly TimeSpan RequestGap = TimeSpan.FromMinutes(15);

    /// <summary>How long a technician's request waits for the agent to connect.</summary>
    public static readonly TimeSpan TechnicianRequestLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// A Disk free check in warning or critical asks for a scan when the endpoint had no scan and no request within this time, and
    /// the request waits this long for the agent.
    /// </summary>
    public static readonly TimeSpan DiskFreeScanGap = TimeSpan.FromHours(12);

    /// <summary>Every scan is kept this long; older ones only as the first scan of their week per volume.</summary>
    public static readonly TimeSpan DailyRetention = TimeSpan.FromDays(30);

    /// <summary>The weekly scans are kept this long (13 months).</summary>
    public static readonly TimeSpan WeeklyRetention = TimeSpan.FromDays(400);

    /// <summary>A baseline scan may be this much younger than the period, since scheduled scans move a little from day to day.</summary>
    public static readonly TimeSpan BaselineTolerance = TimeSpan.FromHours(6);

    /// <summary>Without a scan as old as the period, the oldest scan at least this old is the baseline.</summary>
    public static readonly TimeSpan MinimumBaselineAge = TimeSpan.FromHours(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>True when the agent version scans storage.</summary>
    public static bool AgentSupportsStorageScan(string? agentVersion) =>
        SemanticVersion.TryParse(agentVersion, out _) && !SemanticVersion.IsOlder(agentVersion, MinimumAgentVersion);

    public static bool IsValidScanInterval(int hours) => ScanIntervalChoices.Contains(hours);

    public static string SerializeFolders(IEnumerable<StorageFolderEntry> folders) => JsonSerializer.Serialize(folders, Json);

    public static string SerializeFiles(IEnumerable<StorageFileEntry> files) => JsonSerializer.Serialize(files, Json);

    /// <summary>Reads stored folders; empty for a missing or damaged value.</summary>
    public static IReadOnlyList<StorageFolderEntry> ParseFolders(string? json) => Parse<StorageFolderEntry>(json);

    /// <summary>Reads stored files; empty for a missing or damaged value.</summary>
    public static IReadOnlyList<StorageFileEntry> ParseFiles(string? json) => Parse<StorageFileEntry>(json);

    private static IReadOnlyList<T> Parse<T>(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<T>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The folder a path is in: <c>C:\Users</c> for <c>C:\Users\Public</c>, <c>C:\</c> for <c>C:\Users</c>, <c>/</c> for <c>/var</c>;
    /// null for a root (<c>C:\</c>, <c>/</c>). The separator is a backslash when the path has one, otherwise a slash.
    /// </summary>
    public static string? ParentPath(string path)
    {
        var separator = path.Contains('\\') ? '\\' : '/';
        var trimmed = path.Length > 1 && path[^1] == separator && !(path.Length == 3 && path[1] == ':') ? path[..^1] : path;
        if (trimmed == "/" || (trimmed.Length == 3 && trimmed[1] == ':' && trimmed[2] == '\\'))
        {
            return null;
        }

        var index = trimmed.LastIndexOf(separator);
        if (index < 0)
        {
            return null;
        }

        var parent = trimmed[..index];
        if (parent.Length == 0)
        {
            return "/";
        }

        return parent.Length == 2 && parent[1] == ':' ? parent + '\\' : parent;
    }

    /// <summary>The last part of a path: <c>Public</c> for <c>C:\Users\Public</c>, the path itself for a root.</summary>
    public static string FolderName(string path)
    {
        if (ParentPath(path) is null)
        {
            return path;
        }

        var separator = path.Contains('\\') ? '\\' : '/';
        var trimmed = path.TrimEnd(separator);
        return trimmed[(trimmed.LastIndexOf(separator) + 1)..];
    }

    /// <summary>
    /// The scan to measure growth against: the newest complete scan at least <paramref name="period"/> older than the latest
    /// (with <see cref="BaselineTolerance"/>), or else the oldest at least <see cref="MinimumBaselineAge"/> older. Null when there
    /// is none yet. <paramref name="candidates"/> are complete scans older than the latest.
    /// </summary>
    public static T? Baseline<T>(IEnumerable<T> candidates, Func<T, DateTime> receivedAt, DateTime latest, TimeSpan period) where T : class
    {
        var list = candidates.Where(c => receivedAt(c) < latest).OrderBy(receivedAt).ToList();
        var old = list.LastOrDefault(c => receivedAt(c) <= latest - period + BaselineTolerance);
        return old ?? list.FirstOrDefault(c => receivedAt(c) <= latest - MinimumBaselineAge);
    }

    /// <summary>
    /// How much the volume grew from <paramref name="baseline"/> to <paramref name="latest"/>, measured on the volume root (the first
    /// folder), and the folder that grew most: from the root down, the child that grew most, as long as it holds at least half of
    /// its parent's growth. Folders missing from the baseline are left out, since a smaller folder may simply not have been listed.
    /// Null when a scan has no folders or the roots differ.
    /// </summary>
    public static StorageGrowth? Growth(StorageScanPoint latest, StorageScanPoint baseline)
    {
        if (latest.Folders.Count == 0 || baseline.Folders.Count == 0 ||
            !string.Equals(latest.Folders[0].Path, baseline.Folders[0].Path, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var before = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in baseline.Folders)
        {
            before.TryAdd(folder.Path, folder.SizeBytes);
        }

        var children = latest.Folders.Skip(1)
            .Where(f => ParentPath(f.Path) is not null)
            .ToLookup(f => ParentPath(f.Path)!, StringComparer.OrdinalIgnoreCase);

        var root = latest.Folders[0];
        var rootGrowth = root.SizeBytes - before[root.Path];
        var current = root;
        var currentGrowth = rootGrowth;
        while (currentGrowth > 0)
        {
            var best = children[current.Path]
                .Where(c => before.ContainsKey(c.Path))
                .Select(c => (Folder: c, Growth: c.SizeBytes - before[c.Path]))
                .OrderByDescending(c => c.Growth)
                .FirstOrDefault();
            if (best.Folder is null || best.Growth <= 0 || best.Growth * 2 < currentGrowth)
            {
                break;
            }

            current = best.Folder;
            currentGrowth = best.Growth;
        }

        var days = Math.Round((latest.ReceivedAt - baseline.ReceivedAt).TotalDays, 1);
        return current == root
            ? new StorageGrowth(rootGrowth, days, null, 0)
            : new StorageGrowth(rootGrowth, days, current.Path, currentGrowth);
    }

    /// <summary>Bytes as GB (1024³, as Windows shows them), rounded to two decimals.</summary>
    public static double ToGigabytes(long bytes) => Math.Round(bytes / (1024d * 1024 * 1024), 2);

    /// <summary>The detail of a folder growth result: "+42.1 GB in 7 days, most in D:\SQL\Backup (+38.4 GB)".</summary>
    public static string GrowthDetail(StorageGrowth growth)
    {
        var text = $"{SignedGigabytes(growth.GrowthBytes)} GB in {growth.Days.ToString("0.#", CultureInfo.InvariantCulture)} days";
        return growth.Folder is null ? text : $"{text}, most in {growth.Folder} ({SignedGigabytes(growth.FolderGrowthBytes)} GB)";
    }

    private static string SignedGigabytes(long bytes)
    {
        var value = ToGigabytes(bytes);
        return (value > 0 ? "+" : string.Empty) + value.ToString("0.##", CultureInfo.InvariantCulture);
    }
}
