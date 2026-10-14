using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Guarantees when a task runs (0.6.0): the occurrences of every kind of schedule in its time zone, daylight saving, the last day
/// of a shorter month, repeating during the day, the start and end date, the next occurrence, the random delay and validity of a
/// job, and the validation of a task. Web, the workers and fleeto-signer all use these rules.
/// </summary>
public sealed class AutomationTaskScheduleTests
{
    private static AutomationTask Task(TaskScheduleKind kind, string start = "2026-10-01", int minute = 120, string zone = "UTC",
        Action<AutomationTask>? configure = null)
    {
        var task = new AutomationTask
        {
            Name = "Inventory", ScheduleKind = kind, TimeZone = zone, StartDate = DateOnly.Parse(start), StartMinute = minute, Every = 1
        };
        configure?.Invoke(task);
        return task;
    }

    private static DateTime Utc(string value) => DateTime.SpecifyKind(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    [Fact]
    public void A_daily_schedule_runs_every_day_or_every_few_days_from_its_start_date()
    {
        var daily = Task(TaskScheduleKind.Daily);
        Assert.Equal([Utc("2026-10-01T02:00:00"), Utc("2026-10-02T02:00:00"), Utc("2026-10-03T02:00:00")],
            AutomationTasks.Occurrences(daily, Utc("2026-09-28T00:00:00"), Utc("2026-10-03T12:00:00")));

        var everyThird = Task(TaskScheduleKind.Daily, configure: t => t.Every = 3);
        Assert.Equal([Utc("2026-10-01T02:00:00"), Utc("2026-10-04T02:00:00"), Utc("2026-10-07T02:00:00")],
            AutomationTasks.Occurrences(everyThird, Utc("2026-10-01T00:00:00"), Utc("2026-10-08T00:00:00")));
    }

    [Fact]
    public void A_weekly_schedule_runs_on_the_chosen_days_of_every_nth_week()
    {
        // 2026-10-01 is a Thursday; its week (from Monday 2026-09-28) is week 0, so every second week skips 2026-10-05 to 2026-10-11.
        var weekly = Task(TaskScheduleKind.Weekly, configure: t =>
        {
            t.Every = 2;
            t.WeekDays = WeekDays.Monday | WeekDays.Thursday;
        });
        Assert.Equal([Utc("2026-10-01T02:00:00"), Utc("2026-10-12T02:00:00"), Utc("2026-10-15T02:00:00")],
            AutomationTasks.Occurrences(weekly, Utc("2026-09-30T00:00:00"), Utc("2026-10-20T00:00:00")));
    }

    [Fact]
    public void A_monthly_schedule_runs_on_the_last_day_of_a_shorter_month()
    {
        var monthly = Task(TaskScheduleKind.Monthly, start: "2027-01-01", configure: t => t.MonthDay = 31);
        Assert.Equal([Utc("2027-01-31T02:00:00"), Utc("2027-02-28T02:00:00"), Utc("2027-03-31T02:00:00"), Utc("2027-04-30T02:00:00")],
            AutomationTasks.Occurrences(monthly, Utc("2027-01-01T00:00:00"), Utc("2027-05-01T00:00:00")));

        var quarterly = Task(TaskScheduleKind.Monthly, start: "2027-01-01", configure: t => (t.MonthDay, t.Every) = (15, 3));
        Assert.Equal([Utc("2027-01-15T02:00:00"), Utc("2027-04-15T02:00:00")],
            AutomationTasks.Occurrences(quarterly, Utc("2027-01-01T00:00:00"), Utc("2027-06-01T00:00:00")));
    }

    [Fact]
    public void A_schedule_repeats_during_the_day_until_midnight_and_stops_after_its_end_date()
    {
        var repeating = Task(TaskScheduleKind.Daily, minute: 22 * 60, configure: t => (t.RepeatMinutes, t.EndDate) = (60, DateOnly.Parse("2026-10-02")));
        Assert.Equal([Utc("2026-10-01T22:00:00"), Utc("2026-10-01T23:00:00"), Utc("2026-10-02T22:00:00"), Utc("2026-10-02T23:00:00")],
            AutomationTasks.Occurrences(repeating, Utc("2026-09-30T00:00:00"), Utc("2026-10-05T00:00:00")));
        Assert.Null(AutomationTasks.Next(repeating, Utc("2026-10-02T23:00:00")));
    }

    [Fact]
    public void A_schedule_is_local_time_in_its_zone_and_daylight_saving_moves_a_missing_time_forward()
    {
        // Europe/Brussels: 02:00 CEST is 00:00 UTC; on 2027-03-28 the clock jumps from 02:00 to 03:00, so 02:30 starts at 03:00 CEST.
        var brussels = Task(TaskScheduleKind.Daily, start: "2027-03-27", minute: 150, zone: "Europe/Brussels");
        Assert.Equal([Utc("2027-03-27T01:30:00"), Utc("2027-03-28T01:00:00"), Utc("2027-03-29T00:30:00")],
            AutomationTasks.Occurrences(brussels, Utc("2027-03-26T00:00:00"), Utc("2027-03-29T12:00:00")));
    }

    [Fact]
    public void A_task_that_runs_once_runs_once_and_the_signer_recognises_exactly_its_occurrences()
    {
        var once = Task(TaskScheduleKind.Once, start: "2026-11-05", minute: 9 * 60 + 15);
        Assert.Equal([Utc("2026-11-05T09:15:00")], AutomationTasks.Occurrences(once, Utc("2026-11-01T00:00:00"), Utc("2026-12-01T00:00:00")));
        Assert.Equal(Utc("2026-11-05T09:15:00"), AutomationTasks.Next(once, Utc("2026-11-01T00:00:00")));
        Assert.Null(AutomationTasks.Next(once, Utc("2026-11-05T09:15:00")));

        Assert.True(AutomationTasks.IsOccurrence(once, Utc("2026-11-05T09:15:00")));
        Assert.False(AutomationTasks.IsOccurrence(once, Utc("2026-11-05T09:16:00")));
        Assert.False(AutomationTasks.IsOccurrence(Task(TaskScheduleKind.None), Utc("2026-11-05T09:15:00")));
    }

    [Fact]
    public void The_random_delay_is_the_same_for_a_run_and_endpoint_and_stays_within_the_delay()
    {
        var run = Guid.NewGuid();
        var delays = Enumerable.Range(0, 200).Select(_ => AutomationTasks.DelayFor(run, Guid.NewGuid(), 30)).ToList();
        Assert.All(delays, d => Assert.InRange(d, TimeSpan.Zero, TimeSpan.FromMinutes(30)));
        Assert.True(delays.Distinct().Count() > 150, "The delay spreads endpoints over the window.");

        var endpoint = Guid.NewGuid();
        Assert.Equal(AutomationTasks.DelayFor(run, endpoint, 30), AutomationTasks.DelayFor(run, endpoint, 30));
        Assert.Equal(TimeSpan.Zero, AutomationTasks.DelayFor(run, endpoint, 0));
    }

    [Fact]
    public void A_job_that_catches_up_waits_until_the_next_run_and_one_that_does_not_expires_after_an_hour()
    {
        var due = Utc("2026-10-01T02:00:00");
        var task = Task(TaskScheduleKind.Daily);
        Assert.Equal(Utc("2026-10-02T02:00:00"), AutomationTasks.ValidUntil(task, TaskTrigger.Schedule, due, Utc("2026-10-02T02:00:00")));
        Assert.Equal(due + ScriptRules.MaxValidity, AutomationTasks.ValidUntil(task, TaskTrigger.Schedule, due, Utc("2026-12-01T02:00:00")));
        Assert.Equal(due + AutomationTasks.ShortValidity, AutomationTasks.ValidUntil(task, TaskTrigger.Schedule, due, due.AddMinutes(15)));
        Assert.Equal(due + AutomationTasks.EventValidity, AutomationTasks.ValidUntil(task, TaskTrigger.CheckAlert, due, null));

        task.CatchUp = false;
        Assert.Equal(due + AutomationTasks.ShortValidity, AutomationTasks.ValidUntil(task, TaskTrigger.Schedule, due, Utc("2026-10-02T02:00:00")));
    }

    [Theory]
    [InlineData("weekly-no-days", "Choose at least one day of the week.")]
    [InlineData("repeat-too-often", "Repeat every 15 to 720 minutes")]
    [InlineData("once-repeats", "does not repeat during the day")]
    [InlineData("end-before-start", "The end date is before the start date")]
    [InlineData("unknown-zone", "Choose a time zone")]
    [InlineData("delay", "random delay must be between 0 and 720")]
    [InlineData("no-name", "Enter a name")]
    public void An_invalid_task_is_refused_with_its_cause(string problem, string expected)
    {
        var task = Task(TaskScheduleKind.Daily);
        switch (problem)
        {
            case "weekly-no-days": task.ScheduleKind = TaskScheduleKind.Weekly; break;
            case "repeat-too-often": task.RepeatMinutes = 5; break;
            case "once-repeats": (task.ScheduleKind, task.RepeatMinutes) = (TaskScheduleKind.Once, 60); break;
            case "end-before-start": task.EndDate = task.StartDate.AddDays(-1); break;
            case "unknown-zone": task.TimeZone = "Mars/Olympus"; break;
            case "delay": task.RandomDelayMinutes = 721; break;
            default: task.Name = " "; break;
        }

        Assert.Contains(AutomationTasks.Validate(task), p => p.Contains(expected, StringComparison.OrdinalIgnoreCase));
        Assert.Empty(AutomationTasks.Validate(Task(TaskScheduleKind.Daily)));
    }
}
