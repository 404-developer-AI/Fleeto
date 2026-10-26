using System.Security.Cryptography;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Performance;

/// <summary>
/// Critical findings of the performance monitor as problems of Instance health (0.7.0 step 3, spec §12, Alerts): the workers judge
/// the last hour every 15 minutes, and a critical finding opens a problem that goes to the channels an admin chose for instance
/// health, as a warning: Fleeto is slow, not down. A finding whose problem resolved stays quiet for the cooldown of the settings, so
/// a flow around its threshold does not mail every hour, and at most <see cref="MaxProblems"/> are open at once.
/// </summary>
public static class PerformanceHealth
{
    public const string KeyPrefix = "performance:";

    public const int MaxProblems = 5;

    /// <summary>How often the workers judge the performance of the instance for its health.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);

    /// <summary>The period judged: the last hour.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(1);

    /// <summary>The key of the problem of a finding: stable for the finding, and short enough for any finding id.</summary>
    public static string KeyOf(string findingId) =>
        KeyPrefix + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(findingId)))[..16];

    /// <summary>
    /// The problems for the critical findings of the period, the most costly first, leaving out the keys in
    /// <paramref name="coolingDown"/>: findings whose problem resolved within the cooldown and did not open again since.
    /// </summary>
    public static IReadOnlyList<HealthFinding> Problems(IEnumerable<PerfFinding> findings, IReadOnlySet<string> coolingDown) =>
        findings
            .Where(f => f.Severity == PerfSeverity.Critical)
            .Select(f => (Key: KeyOf(f.Id), Finding: f))
            .Where(f => !coolingDown.Contains(f.Key))
            .DistinctBy(f => f.Key)
            .Take(MaxProblems)
            .Select(f => new HealthFinding(f.Key, HealthComponent.Performance, AlertSeverity.Warning,
                Cut($"Performance: {f.Finding.Title}", 500),
                Cut($"{f.Finding.Explanation} {f.Finding.Action} Settings, Performance shows the evidence and where to look.", 2000)))
            .ToList();

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..(max - 1)] + "…";
}
