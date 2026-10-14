using System.Globalization;
using System.Security.Cryptography;
using Fleeto.Core.Entities;

namespace Fleeto.Core.Domain;

/// <summary>
/// Rules of tasks (0.6.0): validation, the occurrences of a schedule, the random delay and validity of a job, and the texts of
/// a run. Shared by web (the task page), the workers (starting runs) and fleeto-signer (checking a run Fleeto started), so the
/// three agree on when a task runs.
/// <para>
/// A schedule is local time in its time zone. Daylight saving follows <see cref="MaintenanceWindows"/>: a start time the
/// clock jumps over starts at the first valid minute after the jump, one that occurs twice starts at its first occurrence.
/// </para>
/// </summary>
public static class AutomationTasks
{
    public const int MaxNameLength = 100;
    public const int MaxDescriptionLength = 1000;
    public const int MaxEvery = 365;
    /// <summary>Not shorter than the interval at which fleeto-signer signs a run Fleeto started (15 minutes).</summary>
    public const int MinRepeatMinutes = 15;
    public const int MaxRepeatMinutes = 720;
    public const int MaxRandomDelayMinutes = 720;
    public const int MaxTriggerChecks = 20;

    /// <summary>An endpoint starts a task for an alert of its checks at most once in this time.</summary>
    public static readonly TimeSpan CheckAlertCooldown = TimeSpan.FromHours(1);

    /// <summary>fleeto-signer signs at most one job of a run Fleeto started per task and endpoint in this time.</summary>
    public static readonly TimeSpan AutomaticSigningInterval = TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(30);

    /// <summary>The validity of a job that does not catch up: an offline endpoint misses the run.</summary>
    public static readonly TimeSpan ShortValidity = TimeSpan.FromHours(1);

    /// <summary>The validity of a job that catches up, for a run that is not part of a schedule.</summary>
    public static readonly TimeSpan EventValidity = TimeSpan.FromHours(24);

    /// <summary>Longest stretch of days the workers look back for occurrences, after a long stop.</summary>
    public const int MaxLookbackDays = 400;

    /// <summary>Longest stretch of days searched for the next occurrence: every 12 months on day 31 still finds one.</summary>
    private const int MaxLookaheadDays = 366 * 31;

    /// <summary>The initiator of a job of a run Fleeto started; the same empty id as the automatic Action1 install.</summary>
    public static readonly Guid AutomaticInitiator = Action1AgentInstall.AutomaticInitiator;

    public static string AutomaticInitiatorName(string taskName, TaskTrigger trigger) => trigger switch
    {
        TaskTrigger.CheckAlert => $"Fleeto (task {taskName}, check alert)",
        TaskTrigger.Managed => $"Fleeto (task {taskName}, endpoint managed)",
        _ => $"Fleeto (task {taskName}, schedule)"
    };

    /// <summary>Returns the problems of a task (cause and next step), empty when it can be saved.</summary>
    public static IReadOnlyList<string> Validate(AutomationTask task)
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(task.Name))
        {
            problems.Add("Enter a name for the task.");
        }
        else if (task.Name.Length > MaxNameLength)
        {
            problems.Add($"The name can be at most {MaxNameLength} characters.");
        }

        if (task.Description?.Length > MaxDescriptionLength)
        {
            problems.Add($"The description can be at most {MaxDescriptionLength} characters.");
        }

        if (!Enum.IsDefined(task.RunAs))
        {
            problems.Add("Choose the account the script runs as.");
        }

        if (!Enum.IsDefined(task.AppliesTo))
        {
            problems.Add("Choose the endpoints the task is for.");
        }

        if (task.AlertSeverity is not (AlertSeverity.Warning or AlertSeverity.Critical))
        {
            problems.Add("Choose warning or critical for the alert of a failed run.");
        }

        if (!Enum.IsDefined(task.Overlap))
        {
            problems.Add("Choose what happens when the previous run still runs.");
        }

        if (task.RandomDelayMinutes is < 0 or > MaxRandomDelayMinutes)
        {
            problems.Add($"The random delay must be between 0 and {MaxRandomDelayMinutes} minutes.");
        }

        if (task.TriggerCheckIds.Count > MaxTriggerChecks)
        {
            problems.Add($"A task can start on the alerts of at most {MaxTriggerChecks} checks.");
        }

        problems.AddRange(ValidateSchedule(task));
        return problems;
    }

    private static IEnumerable<string> ValidateSchedule(AutomationTask task)
    {
        if (!Enum.IsDefined(task.ScheduleKind))
        {
            yield return "Choose how often the task runs.";
            yield break;
        }

        if (task.ScheduleKind == TaskScheduleKind.None)
        {
            yield break;
        }

        if (MaintenanceWindows.FindTimeZone(task.TimeZone) is null)
        {
            yield return "Choose a time zone for the schedule.";
        }

        if (task.StartMinute is < 0 or > 1439)
        {
            yield return "Choose a start time between 00:00 and 23:59.";
        }

        if (task.EndDate is { } end && end < task.StartDate)
        {
            yield return "The end date is before the start date. Choose an end date on or after the start date.";
        }

        if (task.ScheduleKind != TaskScheduleKind.Once && task.Every is < 1 or > MaxEvery)
        {
            yield return $"Choose an interval between 1 and {MaxEvery}.";
        }

        if (task.ScheduleKind == TaskScheduleKind.Weekly && (task.WeekDays == WeekDays.None || (task.WeekDays & ~WeekDays.All) != 0))
        {
            yield return "Choose at least one day of the week.";
        }

        if (task.ScheduleKind == TaskScheduleKind.Monthly && task.MonthDay is < 1 or > 31)
        {
            yield return "Choose a day of the month between 1 and 31.";
        }

        if (task.RepeatMinutes is { } repeat)
        {
            if (task.ScheduleKind == TaskScheduleKind.Once)
            {
                yield return "A task that runs once does not repeat during the day. Choose daily to repeat it.";
            }
            else if (repeat is < MinRepeatMinutes or > MaxRepeatMinutes)
            {
                yield return $"Repeat every {MinRepeatMinutes} to {MaxRepeatMinutes} minutes, or not at all.";
            }
        }
    }

    /// <summary>
    /// The occurrences of the schedule after <paramref name="fromUtc"/> up to and including <paramref name="toUtc"/>, oldest first.
    /// Empty without a schedule or with an unknown time zone; looks back at most <see cref="MaxLookbackDays"/> days from <paramref name="toUtc"/>.
    /// </summary>
    public static IReadOnlyList<DateTime> Occurrences(AutomationTask task, DateTime fromUtc, DateTime toUtc)
    {
        if (task.ScheduleKind == TaskScheduleKind.None || MaintenanceWindows.FindTimeZone(task.TimeZone) is not { } zone || toUtc <= fromUtc)
        {
            return [];
        }

        var earliest = toUtc.AddDays(-MaxLookbackDays);
        if (fromUtc < earliest)
        {
            fromUtc = earliest;
        }

        // One day of margin on both sides covers every offset of a time zone.
        var firstDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(fromUtc, zone)).AddDays(-1);
        var lastDay = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(toUtc, zone)).AddDays(1);
        var result = new List<DateTime>();
        for (var day = firstDay; day <= lastDay; day = day.AddDays(1))
        {
            foreach (var occurrence in OccurrencesOn(task, zone, day))
            {
                if (occurrence > fromUtc && occurrence <= toUtc)
                {
                    result.Add(occurrence);
                }
            }
        }

        result.Sort();
        return result;
    }

    /// <summary>The first occurrence after <paramref name="afterUtc"/>, or null when the schedule has ended or has none.</summary>
    public static DateTime? Next(AutomationTask task, DateTime afterUtc)
    {
        if (task.ScheduleKind == TaskScheduleKind.None || MaintenanceWindows.FindTimeZone(task.TimeZone) is not { } zone)
        {
            return null;
        }

        var day = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(afterUtc, zone)).AddDays(-1);
        if (day < task.StartDate.AddDays(-1))
        {
            day = task.StartDate.AddDays(-1);
        }

        for (var i = 0; i < MaxLookaheadDays; i++, day = day.AddDays(1))
        {
            if (task.EndDate is { } end && day > end.AddDays(1))
            {
                return null;
            }

            foreach (var occurrence in OccurrencesOn(task, zone, day))
            {
                if (occurrence > afterUtc)
                {
                    return occurrence;
                }
            }
        }

        return null;
    }

    /// <summary>True when <paramref name="occurrenceUtc"/> is exactly an occurrence of the schedule (fleeto-signer checks a run with it).</summary>
    public static bool IsOccurrence(AutomationTask task, DateTime occurrenceUtc) =>
        Occurrences(task, occurrenceUtc.AddMinutes(-1), occurrenceUtc).Contains(occurrenceUtc);

    /// <summary>A text that changes whenever the schedule does, so the workers start counting again after an edit.</summary>
    public static string ScheduleHash(AutomationTask task) => string.Join('|', task.ScheduleKind, task.TimeZone,
        task.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), task.StartMinute,
        task.EndDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), task.Every, (int)task.WeekDays, task.MonthDay, task.RepeatMinutes);

    private static IEnumerable<DateTime> OccurrencesOn(AutomationTask task, TimeZoneInfo zone, DateOnly day)
    {
        if (day < task.StartDate || (task.EndDate is { } end && day > end) || !RunsOn(task, day))
        {
            yield break;
        }

        var step = task.ScheduleKind == TaskScheduleKind.Once ? null : task.RepeatMinutes;
        for (var minute = task.StartMinute; minute < 1440; minute += step ?? 1440)
        {
            yield return MaintenanceWindows.ToUtc(day.ToDateTime(new TimeOnly(minute / 60, minute % 60)), zone);
        }
    }

    private static bool RunsOn(AutomationTask task, DateOnly day)
    {
        var every = Math.Max(1, task.Every);
        switch (task.ScheduleKind)
        {
            case TaskScheduleKind.Once:
                return day == task.StartDate;
            case TaskScheduleKind.Daily:
                return (day.DayNumber - task.StartDate.DayNumber) % every == 0;
            case TaskScheduleKind.Weekly:
            {
                if ((task.WeekDays & DayFlag(day.DayOfWeek)) == 0)
                {
                    return false;
                }

                // Weeks start on Monday; the week of the start date is week 0.
                var weeks = (WeekStart(day).DayNumber - WeekStart(task.StartDate).DayNumber) / 7;
                return weeks % every == 0;
            }
            case TaskScheduleKind.Monthly:
            {
                var months = (day.Year - task.StartDate.Year) * 12 + day.Month - task.StartDate.Month;
                var target = Math.Min(task.MonthDay, DateTime.DaysInMonth(day.Year, day.Month));
                return months % every == 0 && day.Day == target;
            }
            default:
                return false;
        }
    }

    private static DateOnly WeekStart(DateOnly day) => day.AddDays(-(((int)day.DayOfWeek + 6) % 7));

    public static WeekDays DayFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Monday => WeekDays.Monday,
        DayOfWeek.Tuesday => WeekDays.Tuesday,
        DayOfWeek.Wednesday => WeekDays.Wednesday,
        DayOfWeek.Thursday => WeekDays.Thursday,
        DayOfWeek.Friday => WeekDays.Friday,
        DayOfWeek.Saturday => WeekDays.Saturday,
        _ => WeekDays.Sunday
    };

    /// <summary>
    /// This endpoint's share of the random delay of a run: the same for the same run and endpoint, spread evenly over the delay,
    /// so a retry of the workers never moves it.
    /// </summary>
    public static TimeSpan DelayFor(Guid runId, Guid endpointId, int randomDelayMinutes)
    {
        if (randomDelayMinutes <= 0)
        {
            return TimeSpan.Zero;
        }

        Span<byte> input = stackalloc byte[32];
        runId.TryWriteBytes(input[..16]);
        endpointId.TryWriteBytes(input[16..]);
        var hash = SHA256.HashData(input);
        var value = BitConverter.ToUInt32(hash, 0);
        return TimeSpan.FromSeconds(value % (uint)(randomDelayMinutes * 60));
    }

    /// <summary>
    /// Until when the job of a run may start: with catching up until the next occurrence (between an hour and 7 days), or a day for
    /// a run that is not scheduled; without it an hour, so an endpoint that is offline misses the run.
    /// </summary>
    public static DateTime ValidUntil(AutomationTask task, TaskTrigger trigger, DateTime dueAt, DateTime? nextOccurrence)
    {
        if (!task.CatchUp)
        {
            return dueAt + ShortValidity;
        }

        if (trigger != TaskTrigger.Schedule)
        {
            return dueAt + EventValidity;
        }

        var until = nextOccurrence ?? dueAt + ScriptRules.MaxValidity;
        var span = until - dueAt;
        return dueAt + (span < ShortValidity ? ShortValidity : span > ScriptRules.MaxValidity ? ScriptRules.MaxValidity : span);
    }

    /// <summary>"Every 2 weeks on Monday and Thursday at 02:00 (Europe/Brussels)" and the like, for lists.</summary>
    public static string Describe(AutomationTask task)
    {
        if (task.ScheduleKind == TaskScheduleKind.None)
        {
            return "No schedule";
        }

        var time = $"{task.StartMinute / 60:00}:{task.StartMinute % 60:00}";
        var repeat = task.RepeatMinutes is { } minutes ? $", then every {Minutes(minutes)} until midnight" : string.Empty;
        var every = Math.Max(1, task.Every);
        var text = task.ScheduleKind switch
        {
            TaskScheduleKind.Once => $"Once on {task.StartDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)} at {time}",
            TaskScheduleKind.Daily => $"{(every == 1 ? "Every day" : $"Every {every} days")} at {time}{repeat}",
            TaskScheduleKind.Weekly => $"{(every == 1 ? "Every week" : $"Every {every} weeks")} on {DayNames(task.WeekDays)} at {time}{repeat}",
            _ => $"{(every == 1 ? "Every month" : $"Every {every} months")} on day {task.MonthDay} at {time}{repeat}"
        };
        var end = task.EndDate is { } endDate ? $", until {endDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}" : string.Empty;
        return $"{text} ({task.TimeZone}){end}";
    }

    private static string Minutes(int minutes) => minutes % 60 == 0 ? $"{minutes / 60} hour{(minutes == 60 ? string.Empty : "s")}" : $"{minutes} minutes";

    public static string DayNames(WeekDays days)
    {
        var names = Enum.GetValues<WeekDays>().Where(d => d is not (WeekDays.None or WeekDays.All) && days.HasFlag(d)).Select(d => d.ToString()).ToList();
        return names.Count switch
        {
            0 => "no day",
            7 => "every day",
            1 => names[0],
            _ => string.Join(", ", names[..^1]) + " and " + names[^1]
        };
    }

    /// <summary>The title of the alert of a failed run, cause and next step (branding §8).</summary>
    public static string FailureTitle(string taskName, string hostname, JobState state, int? exitCode, string? reason)
    {
        var cause = state switch
        {
            JobState.Refused => $"was refused: {reason}",
            JobState.Lost => "started but did not report back within its timeout",
            _ when exitCode is { } code => $"ended with exit code {code}",
            _ => $"failed{(string.IsNullOrWhiteSpace(reason) ? string.Empty : $": {reason}")}"
        };
        var text = $"Task {taskName} {cause} on {hostname}. Open the run on the task to see its output.";
        return text.Length <= 500 ? text : text[..500];
    }

    /// <summary>True when a job that ended in this state counts as a failed run; an expired job is a missed run, a cancelled one nothing.</summary>
    public static bool IsFailure(JobState state) => state is JobState.Failed or JobState.Lost or JobState.Refused;
}
