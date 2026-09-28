using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>A process in the process list of a CPU or memory usage result (0.6.0). The user is personal data.</summary>
/// <param name="CpuPercent">Share of the whole machine over the sample window; null in the list of a memory usage check.</param>
/// <param name="MemoryBytes">Physical memory in use (working set on Windows, resident set on Linux).</param>
public sealed record ProcessEntry(
    [property: JsonPropertyName("i")] int Pid,
    [property: JsonPropertyName("n")] string Name,
    [property: JsonPropertyName("u")] string User,
    [property: JsonPropertyName("c")] double? CpuPercent,
    [property: JsonPropertyName("m")] long MemoryBytes);

/// <summary>
/// Rules of the process list of the CPU and memory usage checks (0.6.0, ARCHITECTURE.md §4, Check result): the agent lists the
/// processes using the most CPU or memory when a result reaches the lowest threshold of the check, and on "Run now". The list
/// is stored with the result, shown in the check history, and its first processes (without users) go into the alert detail.
/// </summary>
public static class ProcessListRules
{
    /// <summary>Check parameter, set by the server only, from which a result carries its process list.</summary>
    public const string Parameter = "process_list_at";

    public const int MaxProcesses = 10;

    /// <summary>Processes named in the alert detail, which goes out by email and webhook: names and usage, never users.</summary>
    public const int AlertProcesses = 3;

    public const int MaxNameLength = 260;
    public const int MaxUserLength = 256;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>True for the check types whose results can carry a process list.</summary>
    public static bool Applies(CheckType type) => type is CheckType.CpuUsage or CheckType.MemoryUsage;

    /// <summary>The value from which a result carries its process list: the lowest threshold that is set; null without one.</summary>
    public static double? ListAt(double? warning, double? critical)
    {
        var thresholds = new[] { warning, critical }.Where(t => t is { } v && double.IsFinite(v)).Select(t => t!.Value).ToList();
        return thresholds.Count == 0 ? null : thresholds.Min();
    }

    public static string FormatParameter(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The stored form of a list; null for an empty one, so a result without a list stores nothing.</summary>
    public static string? Serialize(IReadOnlyCollection<ProcessEntry> processes) =>
        processes.Count == 0 ? null : JsonSerializer.Serialize(processes, Json);

    /// <summary>Reads a stored list; empty for a missing or damaged value.</summary>
    public static IReadOnlyList<ProcessEntry> Parse(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ProcessEntry>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// The alert detail of a CPU or memory usage result: its own detail followed by the processes using the most, e.g.
    /// "92.3 % average over 60 s. Top processes: sqlservr.exe 71.2%, MsMpEng.exe 12%, svchost.exe 3.1%."
    /// </summary>
    public static string AlertDetail(CheckType type, string detail, IReadOnlyList<ProcessEntry> processes)
    {
        if (!Applies(type) || processes.Count == 0)
        {
            return detail;
        }

        var top = processes.Take(AlertProcesses).Select(p => type == CheckType.CpuUsage
            ? $"{p.Name} {(p.CpuPercent ?? 0).ToString("0.#", CultureInfo.InvariantCulture)}%"
            : $"{p.Name} {Bytes(p.MemoryBytes)}");
        var list = $"Top processes: {string.Join(", ", top)}.";
        return string.IsNullOrEmpty(detail) ? list : $"{detail.TrimEnd('.')}. {list}";
    }

    public static string Bytes(long bytes) => ByteSize.Format(bytes);
}
