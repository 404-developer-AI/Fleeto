using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The rules of rounds of a patch policy of engine Fleeto (0.9.0 step 4): their occurrences in every kind of schedule and time zone with
/// daylight saving, the endpoint's time zone from what the agent reports, the spread and validity of a round's jobs, the restart (never a
/// Proxmox VE host), what a round installs (filters, wildcards, deferral by release date or first seen) and the validation per engine.
/// The workers, fleeto-signer and web all use these rules.
/// </summary>
public sealed class PatchRoundRuleTests
{
    private static DateTime Utc(string value) =>
        DateTime.SpecifyKind(DateTime.Parse(value, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    private static PatchPolicy Policy(Action<PatchPolicy>? configure = null)
    {
        var policy = new PatchPolicy
        {
            Name = "Servers", Engine = PatchEngine.Fleeto, ScheduleKind = PatchScheduleKind.Weekly, WeekDays = WeekDays.Wednesday,
            StartMinute = 20 * 60, EndpointLocalTime = false, RetryHours = 24
        };
        configure?.Invoke(policy);
        return policy;
    }

    [Fact]
    public void A_weekly_round_runs_on_the_chosen_days_at_the_start_time()
    {
        var policy = Policy(p => p.WeekDays = WeekDays.Monday | WeekDays.Wednesday);
        Assert.Equal([Utc("2026-11-09T20:00:00"), Utc("2026-11-11T20:00:00")],
            PatchRounds.Occurrences(policy, TimeZoneInfo.Utc, Utc("2026-11-08T00:00:00"), Utc("2026-11-12T00:00:00")));
        Assert.Equal(Utc("2026-11-16T20:00:00"), PatchRounds.Next(policy, TimeZoneInfo.Utc, Utc("2026-11-11T20:00:00")));
    }

    [Fact]
    public void A_monthly_round_runs_on_its_day_or_the_nth_weekday_and_a_month_without_the_day_skips_it()
    {
        var day31 = Policy(p => (p.ScheduleKind, p.MonthDay) = (PatchScheduleKind.MonthlyDay, 31));
        Assert.Equal([Utc("2026-12-31T20:00:00")], PatchRounds.Occurrences(day31, TimeZoneInfo.Utc, Utc("2026-11-01T00:00:00"), Utc("2027-01-01T00:00:00")));

        var patchTuesday = Policy(p => (p.ScheduleKind, p.MonthWeek, p.MonthWeekday) = (PatchScheduleKind.MonthlyWeekday, 2, DayOfWeek.Tuesday));
        Assert.Equal(Utc("2026-11-10T20:00:00"), PatchRounds.Next(patchTuesday, TimeZoneInfo.Utc, Utc("2026-11-01T00:00:00")));
        Assert.True(PatchRounds.IsOccurrence(patchTuesday, TimeZoneInfo.Utc, Utc("2026-11-10T20:00:00")));
        Assert.False(PatchRounds.IsOccurrence(patchTuesday, TimeZoneInfo.Utc, Utc("2026-11-03T20:00:00")));
    }

    [Fact]
    public void A_round_in_endpoint_time_follows_daylight_saving_of_the_endpoint()
    {
        var brussels = PatchRounds.FindEndpointZone("Europe/Brussels")!;
        var sunday = Policy(p => (p.WeekDays, p.StartMinute, p.EndpointLocalTime) = (WeekDays.Sunday, 150, true));

        // 02:30 occurs twice on 2026-10-25: the first one, still summer time.
        Assert.Equal([Utc("2026-10-25T00:30:00")], PatchRounds.Occurrences(sunday, brussels, Utc("2026-10-24T00:00:00"), Utc("2026-10-26T00:00:00")));
        // 02:30 does not exist on 2027-03-28: the first valid minute after the jump, 03:00 summer time.
        Assert.Equal([Utc("2027-03-28T01:00:00")], PatchRounds.Occurrences(sunday, brussels, Utc("2027-03-27T00:00:00"), Utc("2027-03-29T00:00:00")));
        // Winter time otherwise.
        Assert.Equal(Utc("2026-11-08T01:30:00"), PatchRounds.Next(sunday, brussels, Utc("2026-11-01T12:00:00")));
    }

    [Fact]
    public void The_zone_of_a_round_is_the_endpoint_zone_when_the_policy_asks_for_it_and_utc_otherwise()
    {
        var local = Policy(p => p.EndpointLocalTime = true);
        Assert.Equal("Europe/Brussels", PatchRounds.ZoneOf(local, "Europe/Brussels").Id);
        Assert.Equal(TimeZoneInfo.Utc, PatchRounds.ZoneOf(local, ""));
        Assert.Equal(TimeZoneInfo.Utc, PatchRounds.ZoneOf(local, "Mars/Olympus_Mons"));
        Assert.Equal(TimeZoneInfo.Utc, PatchRounds.ZoneOf(Policy(), "Europe/Brussels"));

        // Windows reports its own names.
        var romance = PatchRounds.FindEndpointZone("Romance Standard Time");
        Assert.NotNull(romance);
        Assert.Equal(TimeSpan.FromHours(1), romance!.BaseUtcOffset);
    }

    [Fact]
    public void The_time_zone_an_agent_reports_is_kept_only_in_the_form_of_a_zone_name()
    {
        Assert.Equal("Europe/Brussels", PatchRounds.NormalizeTimeZone(" Europe/Brussels "));
        Assert.Equal("Romance Standard Time", PatchRounds.NormalizeTimeZone("Romance Standard Time"));
        Assert.Equal("Etc/GMT+1", PatchRounds.NormalizeTimeZone("Etc/GMT+1"));
        Assert.Equal(string.Empty, PatchRounds.NormalizeTimeZone("<script>"));
        Assert.Equal(string.Empty, PatchRounds.NormalizeTimeZone(new string('a', 65)));
        Assert.Equal(string.Empty, PatchRounds.NormalizeTimeZone(null));
    }

    [Fact]
    public void Occurrences_look_back_no_further_than_the_retry_window_allows()
    {
        var daily = Policy(p => p.WeekDays = WeekDays.All);
        var occurrences = PatchRounds.Occurrences(daily, TimeZoneInfo.Utc, Utc("2025-01-01T00:00:00"), Utc("2026-11-11T21:00:00"));
        Assert.Equal(PatchPolicyRules.MaxRetryHours / 24, occurrences.Count);
        Assert.Equal(Utc("2026-11-11T20:00:00"), occurrences[^1]);
    }

    [Fact]
    public void An_endpoint_starts_within_the_random_delay_at_its_own_moment_and_keeps_half_the_retry_window()
    {
        var policy = Policy(p => (p.RandomDelayMinutes, p.RetryHours) = (720, 2));
        var roundAt = Utc("2026-11-11T20:00:00");
        var deployment = Guid.NewGuid();
        var endpoint = Guid.NewGuid();
        var due = PatchRounds.DueAt(policy, deployment, endpoint, roundAt);
        Assert.InRange(due, roundAt, roundAt.AddMinutes(60));
        Assert.Equal(due, PatchRounds.DueAt(policy, deployment, endpoint, roundAt));
        Assert.Equal(roundAt.AddHours(2), PatchRounds.ValidUntil(policy, roundAt));
        Assert.Equal(roundAt, PatchRounds.DueAt(Policy(p => p.RandomDelayMinutes = 0), deployment, endpoint, roundAt));
    }

    [Fact]
    public void A_round_never_restarts_a_Proxmox_VE_host_and_a_server_without_message_restarts_after_a_minute()
    {
        var policy = Policy(p => (p.AutoReboot, p.RebootTimeoutMinutes) = (true, 15));
        Assert.True(PatchRounds.Restarts(policy, "linux", "Ubuntu 24.04.1 LTS"));
        Assert.True(PatchRounds.Restarts(policy, "windows", "Windows Server 2022 Standard"));
        Assert.False(PatchRounds.Restarts(policy, "linux", "Proxmox VE 8.4.1"));
        Assert.False(PatchRounds.Restarts(Policy(), "linux", "Ubuntu 24.04.1 LTS"));

        Assert.Equal(15 * 60, PatchRounds.NoticeSeconds(policy, EndpointClass.Workstation));
        Assert.Equal(15 * 60, PatchRounds.NoticeSeconds(policy, EndpointClass.Server));
        policy.RebootMessage = false;
        Assert.Equal(60, PatchRounds.NoticeSeconds(policy, EndpointClass.Server));
        // The notice on a workstation is never skipped (decided 2026-11-04); the agent uses its own words.
        Assert.Equal(15 * 60, PatchRounds.NoticeSeconds(policy, EndpointClass.Workstation));
        Assert.Equal(string.Empty, PatchRounds.NoticeMessage(policy));
        Assert.Equal(PatchPolicyRules.DefaultRebootMessage, PatchRounds.NoticeMessage(Policy()));
    }

    private static EndpointMissingUpdate Update(string name, string type = "Security Updates", PatchSeverity severity = PatchSeverity.Important,
        DateOnly? released = null, DateTime? firstSeen = null, Action<EndpointMissingUpdate>? configure = null)
    {
        var update = new EndpointMissingUpdate
        {
            ExternalUpdateId = name + ":amd64", Name = name, UpdateType = type, Severity = severity, ReleaseDate = released,
            FirstSeenAt = firstSeen ?? Utc("2026-11-01T00:00:00"), UpdatedAt = Utc("2026-11-10T00:00:00")
        };
        configure?.Invoke(update);
        return update;
    }

    [Fact]
    public void A_round_installs_what_the_policy_allows_and_never_held_removed_or_feature_updates()
    {
        var now = Utc("2026-11-11T20:00:00");
        var all = Policy();
        Assert.False(PatchRounds.Restricts(all));
        Assert.True(PatchRounds.Allows(all, Update("curl", "Regular Updates"), "linux", now));
        Assert.False(PatchRounds.Allows(all, Update("curl", configure: u => u.Held = true), "linux", now));
        Assert.False(PatchRounds.Allows(all, Update("libfoo", configure: u => u.Removal = true), "linux", now));
        Assert.False(PatchRounds.Allows(all, Update("Windows 11, version 25H2", configure: u => u.IsFeatureUpdate = true), "windows", now));
        Assert.False(PatchRounds.Allows(all, Update("Intel - Display", "Drivers", configure: u => u.IsDriver = true), "windows", now));
        Assert.True(PatchRounds.Allows(Policy(p => p.IncludeDrivers = true), Update("Intel - Display", "Drivers", configure: u => u.IsDriver = true), "windows", now));

        var securityOnly = Policy(p => p.LinuxSecurityOnly = true);
        Assert.False(PatchRounds.Allows(securityOnly, Update("curl", "Regular Updates"), "linux", now));
        Assert.True(PatchRounds.Allows(securityOnly, Update("openssl"), "linux", now));
    }

    [Fact]
    public void Filters_of_a_round_match_types_severities_and_names_with_wildcards()
    {
        var now = Utc("2026-11-11T20:00:00");
        var filtered = Policy(p =>
        {
            p.Scope = PatchUpdateScope.Filtered;
            p.UpdateTypes = ["Security Updates"];
            p.Severities = ["Critical", "Important"];
            p.ExcludedNames = ["linux-image*", "*Preview*"];
        });
        Assert.True(PatchRounds.Restricts(filtered));
        Assert.True(PatchRounds.Allows(filtered, Update("openssl"), "linux", now));
        Assert.False(PatchRounds.Allows(filtered, Update("openssl", severity: PatchSeverity.Low), "linux", now));
        Assert.False(PatchRounds.Allows(filtered, Update("curl", "Regular Updates"), "linux", now));
        Assert.False(PatchRounds.Allows(filtered, Update("linux-image-6.8.0-49-generic"), "linux", now));
        Assert.False(PatchRounds.Allows(filtered, Update("2026-11 Cumulative Update Preview for Windows 11"), "windows", now));
        Assert.True(PatchRounds.Matches("LINUX-image*", "linux-image-generic"));
        Assert.False(PatchRounds.Matches("curl", "libcurl4"));
    }

    [Fact]
    public void A_wait_after_release_counts_from_the_release_date_or_else_from_when_Fleeto_first_saw_the_update()
    {
        var now = Utc("2026-11-11T20:00:00");
        var week = Policy(p => p.InstallDelayDays = 7);
        Assert.True(PatchRounds.Restricts(week));
        Assert.True(PatchRounds.Allows(week, Update("old", released: new DateOnly(2026, 11, 4)), "windows", now));
        Assert.False(PatchRounds.Allows(week, Update("new", released: new DateOnly(2026, 11, 10)), "windows", now));
        Assert.True(PatchRounds.Allows(week, Update("seen-long-ago", firstSeen: Utc("2026-11-01T00:00:00")), "linux", now));
        Assert.False(PatchRounds.Allows(week, Update("seen-yesterday", firstSeen: Utc("2026-11-10T08:00:00")), "linux", now));
    }

    [Fact]
    public void A_policy_of_engine_Fleeto_refuses_the_options_only_Action1_has()
    {
        Assert.Null(PatchPolicyRules.Validate(Policy()));
        Assert.Contains("vendors", PatchPolicyRules.Validate(Policy(p => (p.Scope, p.ExcludedVendors) = (PatchUpdateScope.Filtered, ["Mozilla*"]))));
        Assert.Contains("sources", PatchPolicyRules.Validate(Policy(p => (p.Scope, p.UpdateSources) = (PatchUpdateScope.Filtered, ["Applications"]))));
        Assert.Contains("Action1 console", PatchPolicyRules.Validate(Policy(p => p.RequireApproval = true)));
        Assert.Contains("Action1 agent", PatchPolicyRules.Validate(Policy(p => p.AutoInstallAgent = true)));
        Assert.Contains("feature updates", PatchPolicyRules.Validate(Policy(p => (p.Scope, p.UpdateTypes) = (PatchUpdateScope.Filtered, ["Feature Updates"]))));
        Assert.Contains("installation may take", PatchPolicyRules.Validate(Policy(p => p.InstallTimeoutMinutes = 10)));
        Assert.Contains("random delay", PatchPolicyRules.Validate(Policy(p => p.RandomDelayMinutes = 721)));

        // The same options stay valid for Action1, which ignores the Fleeto ones.
        Assert.Null(PatchPolicyRules.Validate(Policy(p => (p.Engine, p.RequireApproval, p.AutoInstallAgent, p.InstallTimeoutMinutes) =
            (PatchEngine.Action1, true, true, 10))));
    }

    [Fact]
    public void Turning_Windows_Update_off_follows_the_engine_of_the_policy()
    {
        var fleeto = Policy(p => p.TurnOffWindowsUpdate = true);
        Assert.True(WindowsUpdateRules.Applies(EndpointTier.Managed, "windows", fleeto, null, EndpointPatchEngine.Fleeto));
        Assert.False(WindowsUpdateRules.Applies(EndpointTier.Managed, "windows", fleeto, Action1AgentStatus.Installed, EndpointPatchEngine.Action1));
        Assert.False(WindowsUpdateRules.Applies(EndpointTier.AgentOnly, "windows", fleeto, null, EndpointPatchEngine.Fleeto));

        var action1 = Policy(p => (p.Engine, p.TurnOffWindowsUpdate) = (PatchEngine.Action1, true));
        Assert.True(WindowsUpdateRules.Applies(EndpointTier.Managed, "windows", action1, Action1AgentStatus.Installed, EndpointPatchEngine.Action1));
        Assert.False(WindowsUpdateRules.Applies(EndpointTier.Managed, "windows", action1, null, EndpointPatchEngine.Fleeto));
    }
}
