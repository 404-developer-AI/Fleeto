using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Policy maintenance windows (0.2.0): occurrences on the chosen days at the local start time, an occurrence that already runs
/// is included, daylight saving moves the UTC start but keeps the real duration, a start time the clock skips starts after the
/// jump, validation refuses what cannot be scheduled, and stored JSON that cannot be read counts as no windows.
/// </summary>
public class MaintenanceWindowTests
{
    private const string Brussels = "Europe/Brussels";

    private static MaintenanceWindow Window(WeekDays days, string start, int minutes, string zone = Brussels, CheckAppliesTo appliesTo = CheckAppliesTo.All) =>
        new("Patch night", days, start, minutes, zone, appliesTo);

    [Fact]
    public void Occurrences_follow_days_and_local_time()
    {
        // Monday 2026-09-14 00:00 UTC to Monday 2026-09-21. Brussels is UTC+2 in September.
        var from = new DateTime(2026, 9, 14, 0, 0, 0, DateTimeKind.Utc);
        var spans = MaintenanceWindows.Occurrences([Window(WeekDays.Wednesday | WeekDays.Sunday, "02:00", 240)], from, from.AddDays(7));

        Assert.Equal(2, spans.Count);
        Assert.Equal(new DateTime(2026, 9, 16, 0, 0, 0, DateTimeKind.Utc), spans[0].StartsAt);
        Assert.Equal(new DateTime(2026, 9, 16, 4, 0, 0, DateTimeKind.Utc), spans[0].EndsAt);
        Assert.Equal(new DateTime(2026, 9, 20, 0, 0, 0, DateTimeKind.Utc), spans[1].StartsAt);
        Assert.All(spans, s => Assert.Equal("Patch night", s.Name));
    }

    [Fact]
    public void A_running_occurrence_that_started_before_the_range_is_included()
    {
        var now = new DateTime(2026, 9, 16, 1, 0, 0, DateTimeKind.Utc);
        var span = Assert.Single(MaintenanceWindows.Occurrences([Window(WeekDays.Wednesday, "02:00", 240)], now, now.AddHours(2)));

        Assert.True(span.StartsAt < now && span.EndsAt > now);
    }

    [Fact]
    public void Daylight_saving_keeps_the_local_start_and_the_real_duration()
    {
        // Last Sunday of October 2026 (the 25th): 03:00 CEST becomes 02:00 CET. A week earlier Brussels is UTC+2, a week later UTC+1.
        var from = new DateTime(2026, 10, 17, 0, 0, 0, DateTimeKind.Utc);
        var spans = MaintenanceWindows.Occurrences([Window(WeekDays.Sunday, "04:00", 120)], from, from.AddDays(14));

        Assert.Equal([new DateTime(2026, 10, 18, 2, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 25, 3, 0, 0, DateTimeKind.Utc)],
            spans.Select(s => s.StartsAt).ToArray());
        Assert.All(spans, s => Assert.Equal(TimeSpan.FromHours(2), s.EndsAt - s.StartsAt));

        // 02:30 on 2026-10-25 exists twice: the first one (still CEST, 00:30 UTC) is used.
        var ambiguous = Assert.Single(MaintenanceWindows.Occurrences([Window(WeekDays.Sunday, "02:30", 60)], new DateTime(2026, 10, 24, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 10, 25, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 10, 25, 0, 30, 0, DateTimeKind.Utc), ambiguous.StartsAt);

        // 02:30 on 2026-03-29 does not exist (02:00 CET jumps to 03:00 CEST): the window starts at 03:00 CEST, 01:00 UTC.
        var skipped = Assert.Single(MaintenanceWindows.Occurrences([Window(WeekDays.Sunday, "02:30", 60)], new DateTime(2026, 3, 28, 12, 0, 0, DateTimeKind.Utc),
            new DateTime(2026, 3, 29, 12, 0, 0, DateTimeKind.Utc)));
        Assert.Equal(new DateTime(2026, 3, 29, 1, 0, 0, DateTimeKind.Utc), skipped.StartsAt);
    }

    [Fact]
    public void Validation_refuses_what_cannot_be_scheduled()
    {
        Assert.Null(MaintenanceWindows.Validate([Window(WeekDays.All, "23:30", 60, "UTC", CheckAppliesTo.Server)]));
        Assert.NotNull(MaintenanceWindows.Validate([Window(WeekDays.None, "02:00", 60)]));
        Assert.NotNull(MaintenanceWindows.Validate([Window(WeekDays.Monday, "2:00", 60)]));
        Assert.NotNull(MaintenanceWindows.Validate([Window(WeekDays.Monday, "25:00", 60)]));
        Assert.NotNull(MaintenanceWindows.Validate([Window(WeekDays.Monday, "02:00", 5)]));
        Assert.NotNull(MaintenanceWindows.Validate([Window(WeekDays.Monday, "02:00", MaintenanceWindows.MaxDurationMinutes + 1)]));
        Assert.NotNull(MaintenanceWindows.Validate([Window(WeekDays.Monday, "02:00", 60, "Mars/Olympus")]));
        Assert.NotNull(MaintenanceWindows.Validate([Window((WeekDays)512, "02:00", 60)]));
        Assert.NotNull(MaintenanceWindows.Validate(Enumerable.Repeat(Window(WeekDays.Monday, "02:00", 60), MaintenanceWindows.MaxWindows + 1).ToList()));
        Assert.Equal("Weekdays 02:00, 4 hours, Europe/Brussels, servers",
            MaintenanceWindows.Describe(Window(WeekDays.Monday | WeekDays.Tuesday | WeekDays.Wednesday | WeekDays.Thursday | WeekDays.Friday, "02:00", 240,
                appliesTo: CheckAppliesTo.Server)));
    }

    [Fact]
    public void Stored_windows_round_trip_and_unreadable_json_means_none()
    {
        IReadOnlyList<MaintenanceWindow> windows = [Window(WeekDays.Saturday | WeekDays.Sunday, "01:15", 90, appliesTo: CheckAppliesTo.Workstation)];

        var json = MaintenanceWindowSchedule.Serialize(windows);

        Assert.Equal(windows, MaintenanceWindowSchedule.Parse(json));
        Assert.Empty(MaintenanceWindowSchedule.Parse("{not json"));
        Assert.Empty(MaintenanceWindowSchedule.Parse("[]"));
    }
}
