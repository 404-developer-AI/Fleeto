using Fleetify.Core.Entities;

namespace Fleetify.Core.Domain;

[Flags]
public enum WeekDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
    All = 127
}

/// <summary>
/// A recurring maintenance window of a policy (0.2.0): on the chosen days at a local start time in a time zone, for a duration,
/// for all endpoints or only servers or workstations. Stored as JSON on the policy.
/// </summary>
/// <param name="Name">Optional label, shown as the reason of the maintenance ("Patch night").</param>
/// <param name="Start">Local start time, "HH:mm".</param>
/// <param name="TimeZone">IANA time zone id, for example Europe/Brussels.</param>
public sealed record MaintenanceWindow(string? Name, WeekDays Days, string Start, int DurationMinutes, string TimeZone, CheckAppliesTo AppliesTo);

/// <summary>One occurrence of a window, in UTC.</summary>
public sealed record MaintenanceWindowSpan(int WindowIndex, DateTime StartsAt, DateTime EndsAt, CheckAppliesTo AppliesTo, string? Name);

/// <summary>
/// Rules for maintenance windows. Occurrences are computed here, once, with the time zone rules of .NET, and stored ahead as
/// <see cref="MaintenanceWindowOccurrence"/> rows, so the maintenance rule in C#, EF Core and SQL stays a plain time comparison.
/// <para>
/// Daylight saving: a start time that does not exist on a day (the clock jumps over it) starts at the first valid time after the
/// jump; a start time that exists twice starts at its first occurrence. The duration is real elapsed time.
/// </para>
/// </summary>
public static class MaintenanceWindows
{
    public const int MaxWindows = 20;
    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 7 * 24 * 60;
    public const int MaxNameLength = 100;

    /// <summary>How far ahead occurrences are stored; the workers extend it every hour.</summary>
    public static readonly TimeSpan Horizon = TimeSpan.FromDays(8);

    public static bool TryParseStart(string? value, out TimeOnly start) =>
        TimeOnly.TryParseExact(value, "HH:mm", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out start);

    public static TimeZoneInfo? FindTimeZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= 64 && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) ? zone : null;

    /// <summary>Returns a problem (cause and next step), or null when every window is acceptable.</summary>
    public static string? Validate(IReadOnlyList<MaintenanceWindow> windows)
    {
        if (windows.Count > MaxWindows)
        {
            return $"A policy can have at most {MaxWindows} maintenance windows.";
        }

        for (var i = 0; i < windows.Count; i++)
        {
            var window = windows[i];
            var label = $"Maintenance window {i + 1}";
            if (window.Name is { Length: > MaxNameLength })
            {
                return $"{label}: the name can be at most {MaxNameLength} characters.";
            }

            if (window.Days == WeekDays.None || (window.Days & ~WeekDays.All) != 0)
            {
                return $"{label}: choose at least one day.";
            }

            if (!TryParseStart(window.Start, out _))
            {
                return $"{label}: enter the start time as hours and minutes, for example 02:00.";
            }

            if (window.DurationMinutes is < MinDurationMinutes or > MaxDurationMinutes)
            {
                return $"{label}: the duration must be between 15 minutes and 7 days.";
            }

            if (FindTimeZone(window.TimeZone) is null)
            {
                return $"{label}: choose a time zone from the list.";
            }

            if (!Enum.IsDefined(window.AppliesTo))
            {
                return $"{label}: choose which endpoints the window applies to.";
            }
        }

        return null;
    }

    /// <summary>Occurrences of all windows that overlap [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), ordered by start.</summary>
    public static IReadOnlyList<MaintenanceWindowSpan> Occurrences(IReadOnlyList<MaintenanceWindow> windows, DateTime fromUtc, DateTime toUtc)
    {
        var spans = new List<MaintenanceWindowSpan>();
        for (var index = 0; index < windows.Count; index++)
        {
            var window = windows[index];
            if (FindTimeZone(window.TimeZone) is not { } zone || !TryParseStart(window.Start, out var start) || window.Days == WeekDays.None)
            {
                continue;
            }

            var duration = TimeSpan.FromMinutes(Math.Clamp(window.DurationMinutes, MinDurationMinutes, MaxDurationMinutes));
            // Start early enough to catch an occurrence that began before the range and still lasts.
            var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc - duration, zone)).AddDays(-1);
            var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(toUtc, zone)).AddDays(1);
            var seen = new HashSet<DateTime>();
            for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
            {
                if ((window.Days & Flag(day.DayOfWeek)) == 0)
                {
                    continue;
                }

                var startsAt = ToUtc(day.ToDateTime(start), zone);
                var endsAt = startsAt + duration;
                if (startsAt < toUtc && endsAt > fromUtc && seen.Add(startsAt))
                {
                    spans.Add(new MaintenanceWindowSpan(index, startsAt, endsAt, window.AppliesTo, window.Name));
                }
            }
        }

        return spans.OrderBy(s => s.StartsAt).ThenBy(s => s.WindowIndex).ToList();
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

    /// <summary>A short description: "Sun 02:00, 4 hours, Europe/Brussels, servers".</summary>
    public static string Describe(MaintenanceWindow window)
    {
        var days = window.Days == WeekDays.All ? "Every day"
            : window.Days == (WeekDays.Monday | WeekDays.Tuesday | WeekDays.Wednesday | WeekDays.Thursday | WeekDays.Friday) ? "Weekdays"
            : string.Join(", ", Enum.GetValues<WeekDays>().Where(d => d is not WeekDays.None and not WeekDays.All && window.Days.HasFlag(d))
                .Select(d => d.ToString()[..3]));
        var duration = window.DurationMinutes % 60 == 0
            ? $"{window.DurationMinutes / 60} hour{(window.DurationMinutes == 60 ? "" : "s")}"
            : $"{window.DurationMinutes} minutes";
        var appliesTo = window.AppliesTo switch
        {
            CheckAppliesTo.Server => ", servers",
            CheckAppliesTo.Workstation => ", workstations",
            _ => string.Empty
        };
        return $"{days} {window.Start}, {duration}, {window.TimeZone}{appliesTo}";
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            // The clock jumps over this time: start at the first valid minute after the jump.
            var probe = local;
            while (zone.IsInvalidTime(probe))
            {
                probe = probe.AddMinutes(1);
            }

            local = probe;
        }

        if (zone.IsAmbiguousTime(local))
        {
            // The time occurs twice: the first occurrence has the larger offset.
            var offset = zone.GetAmbiguousTimeOffsets(local).Max();
            return DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        }

        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }
}
