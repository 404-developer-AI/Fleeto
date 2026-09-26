using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// The rules of patch policies (0.6.0). Every option and value here is one an Action1 automation accepts (its OpenAPI
/// document 3.1, schemas <c>DeployUpdate</c> and <c>AutomationSchedulePayload</c>): Fleeto offers nothing the product
/// cannot do. Options Action1 has only in its console (such as switching Windows Update off) are left out on purpose.
/// </summary>
public static class PatchPolicyRules
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1000;
    public const int MaxRebootMessageLength = 500;
    public const int MaxInstallDelayDays = 90;
    public const int MinRebootTimeoutMinutes = 1;
    public const int MaxRebootTimeoutMinutes = 24 * 60;
    public const int MinRetryHours = 1;
    public const int MaxRetryHours = 7 * 24;

    /// <summary>Name patterns and vendor patterns a policy may leave out, each.</summary>
    public const int MaxExclusions = 20;

    public const int MaxPatternLength = 100;

    /// <summary>Update sources as Action1 names them (filter <c>update_sources</c>); "Applications" are third-party updates.</summary>
    public static readonly IReadOnlyList<string> UpdateSources = ["Windows - Mandatory", "Windows - Optional", "Applications"];

    /// <summary>Update types as Action1 names them (filter <c>update_types</c>).</summary>
    public static readonly IReadOnlyList<string> UpdateTypes =
    [
        "Security Updates", "Critical - Non-Security", "Regular Updates", "Update Rollups", "Definition Updates", "Feature Updates",
        "Service Packs", "Drivers", "Tools", "Upgrades", "Unknown"
    ];

    /// <summary>Security severities as Action1 names them (filter <c>update_security_severities</c>).</summary>
    public static readonly IReadOnlyList<string> Severities = ["Critical", "Important", "Moderate", "Low", "Unspecified"];

    /// <summary>The message a signed-in user sees before a restart when the policy has none of its own.</summary>
    public const string DefaultRebootMessage = PatchRules.RebootMessage;

    /// <summary>Null when the policy is valid; otherwise the problem, stated for the person who filled it in.</summary>
    public static string? Validate(PatchPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(policy.Name) || policy.Name.Trim().Length > MaxNameLength)
        {
            return $"Enter a patch policy name of at most {MaxNameLength} characters.";
        }

        if (policy.Description is { Length: > MaxDescriptionLength })
        {
            return $"The description can be at most {MaxDescriptionLength} characters.";
        }

        if (!Enum.IsDefined(policy.AppliesTo))
        {
            return "Choose all endpoints, servers or workstations.";
        }

        if (!Enum.IsDefined(policy.ScheduleKind))
        {
            return "Choose a weekly or a monthly schedule.";
        }

        switch (policy.ScheduleKind)
        {
            case PatchScheduleKind.Weekly when (policy.WeekDays & WeekDays.All) == WeekDays.None:
                return "Choose at least one day of the week.";
            case PatchScheduleKind.MonthlyDay when policy.MonthDay is < 1 or > 31:
                return "Choose a day of the month between 1 and 31.";
            case PatchScheduleKind.MonthlyWeekday when policy.MonthWeek is < 1 or > 4 || !Enum.IsDefined(policy.MonthWeekday):
                return "Choose the first, second, third or fourth weekday of the month.";
        }

        if (policy.StartMinute is < 0 or > 1439)
        {
            return "Choose a start time between 00:00 and 23:59.";
        }

        if (!Enum.IsDefined(policy.Scope))
        {
            return "Choose all updates or the updates that match the filters.";
        }

        if (policy.Scope == PatchUpdateScope.Filtered)
        {
            if ((Unknown(policy.UpdateSources, UpdateSources) ?? Unknown(policy.UpdateTypes, UpdateTypes) ??
                 Unknown(policy.Severities, Severities)) is { } unknown)
            {
                return $"{unknown} is not a filter value Action1 knows.";
            }

            if (policy.UpdateSources.Count == 0 && policy.UpdateTypes.Count == 0 && policy.Severities.Count == 0 &&
                policy.ExcludedNames.Count == 0 && policy.ExcludedVendors.Count == 0)
            {
                return "Choose at least one filter, or install all updates.";
            }

            if ((Pattern(policy.ExcludedNames, "update names") ?? Pattern(policy.ExcludedVendors, "vendors")) is { } problem)
            {
                return problem;
            }
        }

        if (policy.InstallDelayDays is < 0 or > MaxInstallDelayDays)
        {
            return $"The delay after release must be between 0 and {MaxInstallDelayDays} days.";
        }

        if (policy.RebootTimeoutMinutes is < MinRebootTimeoutMinutes or > MaxRebootTimeoutMinutes)
        {
            return $"The time before the restart must be between {MinRebootTimeoutMinutes} and {MaxRebootTimeoutMinutes} minutes.";
        }

        if (policy.RebootMessageText is { Length: > MaxRebootMessageLength })
        {
            return $"The restart message can be at most {MaxRebootMessageLength} characters.";
        }

        if (policy.RetryHours is < MinRetryHours or > MaxRetryHours)
        {
            return $"Retrying missed endpoints must last between {MinRetryHours} hour and {MaxRetryHours / 24} days.";
        }

        return null;
    }

    /// <summary>The schedule in words, for lists and the audit log: "Every Wednesday at 20:00 (endpoint time)".</summary>
    public static string DescribeSchedule(PatchPolicy policy)
    {
        var at = $"{policy.StartMinute / 60:00}:{policy.StartMinute % 60:00}";
        var zone = policy.EndpointLocalTime ? "endpoint time" : "UTC";
        var when = policy.ScheduleKind switch
        {
            PatchScheduleKind.Weekly when (policy.WeekDays & WeekDays.All) == WeekDays.All => "Every day",
            PatchScheduleKind.Weekly => "Every " + string.Join(", ", Days(policy.WeekDays)),
            PatchScheduleKind.MonthlyDay => $"Monthly on day {policy.MonthDay}",
            _ => $"Monthly on the {Ordinal(policy.MonthWeek)} {policy.MonthWeekday}"
        };
        return $"{when} at {at} ({zone})";
    }

    /// <summary>The days of a mask in week order, Monday first.</summary>
    public static IEnumerable<DayOfWeek> Days(WeekDays days)
    {
        DayOfWeek[] order = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday, DayOfWeek.Saturday, DayOfWeek.Sunday];
        return order.Where(d => (days & Flag(d)) != 0);
    }

    public static WeekDays Flag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekDays.Monday,
        DayOfWeek.Tuesday => WeekDays.Tuesday,
        DayOfWeek.Wednesday => WeekDays.Wednesday,
        DayOfWeek.Thursday => WeekDays.Thursday,
        DayOfWeek.Friday => WeekDays.Friday,
        DayOfWeek.Saturday => WeekDays.Saturday,
        _ => WeekDays.Sunday
    };

    public static string Ordinal(int week) => week switch { 1 => "first", 2 => "second", 3 => "third", _ => "fourth" };

    private static string? Unknown(IEnumerable<string> values, IReadOnlyList<string> allowed) =>
        values.FirstOrDefault(v => !allowed.Contains(v, StringComparer.Ordinal));

    private static string? Pattern(IReadOnlyCollection<string> patterns, string what)
    {
        if (patterns.Count > MaxExclusions)
        {
            return $"Leave out at most {MaxExclusions} {what}.";
        }

        return patterns.Any(p => string.IsNullOrWhiteSpace(p) || p.Length > MaxPatternLength)
            ? $"Every pattern of {what} to leave out needs 1 to {MaxPatternLength} characters."
            : null;
    }
}
