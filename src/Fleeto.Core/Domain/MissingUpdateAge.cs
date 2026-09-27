using System.Globalization;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// The rule of the missing updates check (0.6.0): the value is the number of days since the release of the oldest update
/// that counts, 0 when none does. An update counts when its severity reaches the chosen minimum, it has a release date
/// (an update without one cannot be aged, and guessing would make it look older or newer than it is) and it was not
/// declined in Action1 (declined is a decision not to install it, so it would only keep an alert open).
/// </summary>
public static class MissingUpdateAge
{
    /// <summary>At most this many updates are named in the detail; the rest is counted.</summary>
    public const int NamedInDetail = 5;

    public sealed record Update(string Name, string KbNumber, PatchSeverity Severity, DateOnly? ReleaseDate, string ApprovalStatus);

    public sealed record Result(double Value, string Detail);

    /// <summary>True when an update of this severity counts for the chosen minimum (<see cref="CheckCatalog.MissingUpdateSeverities"/>).</summary>
    public static bool Counts(PatchSeverity severity, string minimum) => minimum switch
    {
        "critical" => severity == PatchSeverity.Critical,
        "important" => severity >= PatchSeverity.Important,
        "moderate" => severity >= PatchSeverity.Moderate,
        _ => true
    };

    public static bool IsDeclined(string approvalStatus) => approvalStatus.Equals("Declined", StringComparison.OrdinalIgnoreCase);

    /// <param name="threshold">The lowest threshold that applies on the endpoint; the detail names the updates at or above it.</param>
    public static Result Evaluate(IEnumerable<Update> missing, string minimum, DateOnly today, double? threshold)
    {
        var counted = missing
            .Where(u => u.ReleaseDate is not null && !IsDeclined(u.ApprovalStatus) && Counts(u.Severity, minimum))
            .Select(u => (Update: u, Days: Math.Max(0, today.DayNumber - u.ReleaseDate!.Value.DayNumber)))
            .OrderByDescending(u => u.Days)
            .ThenBy(u => u.Update.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (counted.Count == 0)
        {
            return new Result(0, "No missing update counts for this check.");
        }

        var oldest = counted[0].Days;
        var over = threshold is { } days ? counted.Where(u => u.Days >= days).ToList() : [];
        if (over.Count == 0)
        {
            return new Result(oldest, $"{Plural(counted.Count, "missing update")} counted; the oldest was released {Plural(oldest, "day")} ago.");
        }

        var named = string.Join("; ", over.Take(NamedInDetail).Select(u =>
            $"{u.Update.Name}{(string.IsNullOrEmpty(u.Update.KbNumber) ? string.Empty : $" ({u.Update.KbNumber})")}, " +
            $"released {u.Update.ReleaseDate!.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}"));
        var more = over.Count > NamedInDetail ? $"; and {over.Count - NamedInDetail} more" : string.Empty;
        var thresholdText = threshold!.Value.ToString("0.##", CultureInfo.InvariantCulture);
        return new Result(oldest, $"{Plural(over.Count, "missing update")} released {thresholdText} days ago or longer: {named}{more}.");
    }

    private static string Plural(int count, string noun) => count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
