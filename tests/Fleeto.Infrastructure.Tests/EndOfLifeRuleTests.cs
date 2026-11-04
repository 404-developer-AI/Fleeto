using Fleeto.Core.Domain;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The rules of the end-of-life report (0.8.0 step 4, VULNERABILITIES.md §12): recognising the release from what the agent reports, the
/// status on a day, the schedule, and the promise that every operating system Fleeto supports has a source and a line in the list of
/// supported products.
/// </summary>
public class EndOfLifeRuleTests
{
    // Releases as endoflife.date listed them on 2026-11-04 (a selection).
    private static readonly IReadOnlyList<EndOfLifeRelease> Releases =
    [
        new(EndOfLifeProduct.Windows, "11-25h2-e", "11 25H2 (E)", D(2028, 10, 10), null, "10.0.26200"),
        new(EndOfLifeProduct.Windows, "11-25h2-w", "11 25H2 (W)", D(2027, 10, 12), null, "10.0.26200"),
        new(EndOfLifeProduct.Windows, "11-24h2-iot-lts", "11 24H2 IoT (LTS)", D(2034, 10, 10), null, "10.0.26100"),
        new(EndOfLifeProduct.Windows, "11-24h2-e-lts", "11 24H2 (E) (LTS)", D(2029, 10, 9), null, "10.0.26100"),
        new(EndOfLifeProduct.Windows, "11-24h2-e", "11 24H2 (E)", D(2027, 10, 12), null, "10.0.26100"),
        new(EndOfLifeProduct.Windows, "11-24h2-w", "11 24H2 (W)", D(2026, 10, 13), null, "10.0.26100"),
        new(EndOfLifeProduct.Windows, "10-22h2", "10 22H2", D(2025, 10, 14), D(2028, 10, 10), "10.0.19045"),
        new(EndOfLifeProduct.Windows, "10-21h2-e-lts", "10 21H2 (E) (LTS)", D(2027, 1, 12), D(2030, 1, 12), "10.0.19044"),
        new(EndOfLifeProduct.Windows, "10-21h2-w", "10 21H2 (W)", D(2023, 6, 13), null, "10.0.19044"),
        new(EndOfLifeProduct.Windows, "7-sp1", "7 SP1", D(2020, 1, 14), null, "6.1.7601"),
        new(EndOfLifeProduct.WindowsServer, "2025", "Windows Server 2025 (LTSC)", D(2034, 11, 14), null, "10.0.26100"),
        new(EndOfLifeProduct.WindowsServer, "2022", "Windows Server 2022 (LTSC)", D(2031, 10, 14), null, "10.0.20348"),
        new(EndOfLifeProduct.WindowsServer, "1809-sac", "Windows Server 1809 SAC", D(2020, 11, 10), null, "10.0.17763"),
        new(EndOfLifeProduct.WindowsServer, "2019", "Windows Server 2019 (LTSC)", D(2029, 1, 9), null, "10.0.17763"),
        new(EndOfLifeProduct.WindowsServer, "2016", "Windows Server 2016 (LTSC)", D(2027, 1, 12), D(2030, 1, 12), "10.0.14393"),
        new(EndOfLifeProduct.WindowsServer, "2012-r2", "Windows Server 2012 R2", D(2023, 10, 10), D(2026, 10, 13), "6.3.9600"),
        new(EndOfLifeProduct.WindowsServer, "2008-r2-sp1", "Windows Server 2008 R2 SP1", D(2020, 1, 14), D(2023, 1, 10), "6.1.7601"),
        new(EndOfLifeProduct.Ubuntu, "24.04", "24.04 'Noble Numbat' (LTS)", D(2029, 5, 31), D(2034, 4, 25), null),
        new(EndOfLifeProduct.Ubuntu, "22.04", "22.04 'Jammy Jellyfish' (LTS)", D(2027, 6, 1), D(2032, 4, 9), null),
        new(EndOfLifeProduct.Ubuntu, "20.04", "20.04 'Focal Fossa' (LTS)", D(2025, 5, 29), D(2030, 4, 2), null),
        new(EndOfLifeProduct.Debian, "13", "13 (Trixie)", D(2030, 6, 30), D(2035, 6, 30), null),
        new(EndOfLifeProduct.Debian, "12", "12 (Bookworm)", D(2028, 6, 30), D(2033, 6, 30), null),
        new(EndOfLifeProduct.AlmaLinux, "9", "9", D(2032, 5, 31), null, null),
        new(EndOfLifeProduct.ProxmoxVe, "9", "9", null, null, null),
        new(EndOfLifeProduct.ProxmoxVe, "8", "8", D(2026, 8, 31), null, null),
    ];

    private static readonly IReadOnlyList<EndOfLifeRelease> All = Releases.Concat(EndOfLife.SynologyDsmReleases).ToList();

    private static DateOnly D(int year, int month, int day) => new(year, month, day);

    private static string? Recognise(string platform, string name, string version) =>
        EndOfLife.ProductOf(platform, name) is { } product ? EndOfLife.ReleaseOf(product, name, version, All)?.Release : null;

    [Theory]
    // Names exactly as the agents of the test endpoints report them (screenshots of 2026-11-03, the Synology NAS of 2026-11-01).
    [InlineData("linux", "AlmaLinux 9.8 (Olive Jaguar)", "5.14.0-570.el9.x86_64", "9")]
    [InlineData("linux", "Proxmox VE 8.4.21 (Debian GNU/Linux 12 (bookworm))", "6.8.12-9-pve", "8")]
    [InlineData("linux", "Proxmox VE 9.2.3 (Debian GNU/Linux 13 (trixie))", "6.14.8-2-pve", "9")]
    [InlineData("linux", "Synology DSM 7.1.1-42962 Update 8", "4.4.180+", "7.1")]
    [InlineData("linux", "Synology DSM 7.2-64570", "4.4.302+", "7.2")]
    [InlineData("linux", "Ubuntu 24.04.1 LTS", "6.8.0-45-generic", "24.04")]
    [InlineData("linux", "Ubuntu 22.04 LTS", "5.15.0-1-generic", "22.04")]
    [InlineData("linux", "Debian GNU/Linux 12 (bookworm)", "6.1.0-25-amd64", "12")]
    [InlineData("windows", "Windows 11 Pro", "10.0.26200", "11-25h2-w")]
    [InlineData("windows", "Windows 11 Enterprise", "10.0.26200", "11-25h2-e")]
    [InlineData("windows", "Windows 11 Education", "10.0.26100", "11-24h2-e")]
    [InlineData("windows", "Windows 11 Pro Education", "10.0.26100", "11-24h2-w")]
    [InlineData("windows", "Windows 11 Enterprise LTSC 2024", "10.0.26100", "11-24h2-e-lts")]
    [InlineData("windows", "Windows 11 IoT Enterprise LTSC 2024", "10.0.26100", "11-24h2-iot-lts")]
    [InlineData("windows", "Windows 10 Pro", "10.0.19045", "10-22h2")]
    [InlineData("windows", "Windows 10 Enterprise LTSC 2021", "10.0.19044", "10-21h2-e-lts")]
    [InlineData("windows", "Windows 10 Pro", "10.0.19044", "10-21h2-w")]
    [InlineData("windows", "Windows 7 Professional", "6.1.7601", "7-sp1")]
    [InlineData("windows", "Windows Server 2022 Standard", "10.0.20348", "2022")]
    [InlineData("windows", "Windows Server 2025 Datacenter", "10.0.26100", "2025")]
    [InlineData("windows", "Windows Server 2019 Standard", "10.0.17763", "2019")]
    [InlineData("windows", "Windows Server 2012 R2 Standard", "6.3.9600", "2012-r2")]
    [InlineData("windows", "Windows Server 2008 R2 Enterprise", "6.1.7601", "2008-r2-sp1")]
    // Without its year a server is matched by build, never on the semi-annual release of the same build.
    [InlineData("windows", "Windows Server Standard", "10.0.17763", "2019")]
    public void The_release_is_recognised_from_what_the_agent_reports(string platform, string name, string version, string expected) =>
        Assert.Equal(expected, Recognise(platform, name, version));

    [Theory]
    [InlineData("linux", "Fedora Linux 41 (Workstation Edition)", "6.11.0")]
    [InlineData("linux", "Linux", "6.1.0")]
    [InlineData("linux", "Ubuntu 25.10", "6.17.0")]
    [InlineData("windows", "Windows 11 Pro", "10.0.99999")]
    [InlineData("windows", "Windows 11 Pro", "")]
    [InlineData("darwin", "macOS 15.1", "24.1.0")]
    public void An_unknown_operating_system_or_release_is_not_recognised(string platform, string name, string version) =>
        Assert.Null(Recognise(platform, name, version));

    [Fact]
    public void The_status_follows_the_dates_the_period_and_a_paid_extension()
    {
        var today = D(2026, 11, 4);
        EndOfLifeRelease Of(EndOfLifeProduct product, string release) => All.Single(r => r.Product == product && r.Release == release);

        Assert.Equal(EndOfLifeStatus.Supported, EndOfLife.Judge(EndOfLifeProduct.Ubuntu, Of(EndOfLifeProduct.Ubuntu, "24.04"), today, 6, null, false).Status);
        // 22.04 ends 2027-06-01: seven months away, so ending soon with a period of 12 months, not with 6.
        Assert.Equal(EndOfLifeStatus.Supported, EndOfLife.Judge(EndOfLifeProduct.Ubuntu, Of(EndOfLifeProduct.Ubuntu, "22.04"), today, 6, null, false).Status);
        Assert.Equal(EndOfLifeStatus.EndingSoon, EndOfLife.Judge(EndOfLifeProduct.Ubuntu, Of(EndOfLifeProduct.Ubuntu, "22.04"), today, 12, null, false).Status);
        // The end-of-life day itself has no updates any more.
        Assert.Equal(EndOfLifeStatus.EndOfLife, EndOfLife.Judge(EndOfLifeProduct.ProxmoxVe, Of(EndOfLifeProduct.ProxmoxVe, "8"), D(2026, 8, 31), 6, null, false).Status);
        Assert.Equal(EndOfLifeStatus.EndingSoon, EndOfLife.Judge(EndOfLifeProduct.ProxmoxVe, Of(EndOfLifeProduct.ProxmoxVe, "8"), D(2026, 8, 30), 6, null, false).Status);
        Assert.Equal(EndOfLifeStatus.NoDate, EndOfLife.Judge(EndOfLifeProduct.ProxmoxVe, Of(EndOfLifeProduct.ProxmoxVe, "9"), today, 6, null, false).Status);
        Assert.Equal(EndOfLifeStatus.NotRecognised, EndOfLife.Judge(null, null, today, 6, null, false).Status);
        Assert.Equal(EndOfLifeStatus.EndOfLife, EndOfLife.Judge(EndOfLifeProduct.SynologyDsm, Of(EndOfLifeProduct.SynologyDsm, "7.1"), today, 6, null, false).Status);

        // Windows 10 22H2 is end of life, extended only when the client bought ESU for this release, and only until the ESU ends.
        var windows10 = Of(EndOfLifeProduct.Windows, "10-22h2");
        Assert.Equal(EndOfLifeStatus.EndOfLife, EndOfLife.Judge(EndOfLifeProduct.Windows, windows10, today, 6, null, false).Status);
        var marked = EndOfLife.Judge(EndOfLifeProduct.Windows, windows10, today, 6, "10-22h2", false);
        Assert.Equal((EndOfLifeStatus.Extended, true), (marked.Status, marked.ExtensionCounted));
        Assert.Equal(EndOfLifeStatus.EndOfLife, EndOfLife.Judge(EndOfLifeProduct.Windows, windows10, today, 6, "10-21h2-e-lts", false).Status);
        Assert.Equal(EndOfLifeStatus.EndOfLife, EndOfLife.Judge(EndOfLifeProduct.Windows, windows10, D(2028, 10, 10), 6, "10-22h2", false).Status);
        // A release without an extension cannot be extended by a mark.
        Assert.Equal(EndOfLifeStatus.EndOfLife,
            EndOfLife.Judge(EndOfLifeProduct.Windows, Of(EndOfLifeProduct.Windows, "7-sp1"), today, 6, "7-sp1", false).Status);

        // Ubuntu Pro reported by the agent extends an Ubuntu release by itself; it never extends another product.
        var focal = Of(EndOfLifeProduct.Ubuntu, "20.04");
        Assert.Equal(EndOfLifeStatus.EndOfLife, EndOfLife.Judge(EndOfLifeProduct.Ubuntu, focal, today, 6, null, false).Status);
        Assert.Equal(EndOfLifeStatus.Extended, EndOfLife.Judge(EndOfLifeProduct.Ubuntu, focal, today, 6, null, true).Status);
        Assert.Equal(EndOfLifeStatus.EndOfLife, EndOfLife.Judge(EndOfLifeProduct.Windows, windows10, today, 6, null, true).Status);
    }

    [Fact]
    public void The_dates_of_Synology_DSM_end_after_the_last_month_of_the_policy()
    {
        var byRelease = EndOfLife.SynologyDsmReleases.ToDictionary(r => r.Release);
        Assert.Equal(D(2024, 7, 1), byRelease["7.1"].EndOfLifeOn);
        Assert.Equal(D(2028, 11, 1), byRelease["7.3"].EndOfLifeOn);
        Assert.Equal(D(2026, 1, 1), byRelease["7.2"].EndOfLifeOn);
        Assert.Equal(D(2024, 10, 1), byRelease["6.2"].EndOfLifeOn);
        Assert.All(EndOfLife.SynologyDsmReleases, r => Assert.Equal(EndOfLifeProduct.SynologyDsm, r.Product));
        Assert.Equal(EndOfLife.SynologyDsmReleases.Count, byRelease.Count);
    }

    [Fact]
    public void Every_operating_system_has_a_source_and_a_line_in_the_list_of_supported_products()
    {
        foreach (var product in Enum.GetValues<EndOfLifeProduct>())
        {
            var info = Assert.Single(EndOfLife.Products, p => p.Product == product);
            Assert.True(info.Source == EndOfLifeSourceKind.Builtin || !string.IsNullOrEmpty(info.Slug), $"{product} has no source");
            Assert.StartsWith("https://", info.SourceLink);
            Assert.Single(SupportedProducts.All, p => p.Product == product);
        }

        // A product with its dates in the code has them.
        Assert.All(EndOfLife.Products.Where(p => p.Source == EndOfLifeSourceKind.Builtin), p =>
            Assert.Contains(EndOfLife.SynologyDsmReleases, r => r.Product == p.Product));
    }

    [Fact]
    public void A_schedule_runs_at_its_hour_in_its_time_zone_and_once_after_a_missed_run()
    {
        var monthly = new EndOfLifeSchedule(EndOfLifeScheduleKind.Monthly, DayOfWeek.Monday, 1, 7, "Europe/Brussels");
        // 1 December 2026 07:00 in Brussels is 06:00 UTC (winter time).
        Assert.Equal(new DateTime(2026, 12, 1, 6, 0, 0, DateTimeKind.Utc), EndOfLife.NextRun(monthly, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc)));
        // In summer it is 05:00 UTC.
        Assert.Equal(new DateTime(2027, 7, 1, 5, 0, 0, DateTimeKind.Utc), EndOfLife.NextRun(monthly, new DateTime(2027, 6, 1, 6, 0, 0, DateTimeKind.Utc)));

        var weekly = monthly with { Kind = EndOfLifeScheduleKind.Weekly, WeekDay = DayOfWeek.Friday, Hour = 9, TimeZone = "UTC" };
        Assert.Equal(new DateTime(2026, 11, 6, 9, 0, 0, DateTimeKind.Utc), EndOfLife.NextRun(weekly, new DateTime(2026, 11, 4, 12, 0, 0, DateTimeKind.Utc)));
        var daily = weekly with { Kind = EndOfLifeScheduleKind.Daily };
        Assert.Equal(new DateTime(2026, 11, 5, 9, 0, 0, DateTimeKind.Utc), EndOfLife.NextRun(daily, new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc)));

        Assert.Null(EndOfLife.NextRun(monthly with { Kind = EndOfLifeScheduleKind.Off }, DateTime.UtcNow));
        Assert.Null(EndOfLife.NextRun(monthly with { TimeZone = "Mars/Olympus" }, DateTime.UtcNow));
    }

    [Fact]
    public void A_schedule_is_checked_on_save_and_falls_back_to_the_default_when_unreadable()
    {
        var schedule = EndOfLifeSchedule.Default;
        Assert.Null(EndOfLife.ScheduleProblem(schedule));
        Assert.NotNull(EndOfLife.ScheduleProblem(schedule with { MonthDay = 29 }));
        Assert.NotNull(EndOfLife.ScheduleProblem(schedule with { Hour = 24 }));
        Assert.NotNull(EndOfLife.ScheduleProblem(schedule with { TimeZone = "Nowhere/Land" }));
        Assert.NotNull(EndOfLife.ScheduleProblem(schedule with { Kind = (EndOfLifeScheduleKind)9 }));

        var weekly = schedule with { Kind = EndOfLifeScheduleKind.Weekly, WeekDay = DayOfWeek.Thursday };
        Assert.Equal(weekly, EndOfLife.ParseSchedule(EndOfLife.ScheduleJson(weekly)));
        Assert.Equal(EndOfLifeSchedule.Default, EndOfLife.ParseSchedule(null));
        Assert.Equal(EndOfLifeSchedule.Default, EndOfLife.ParseSchedule("{not json"));
        Assert.Equal(EndOfLifeSchedule.Default, EndOfLife.ParseSchedule(EndOfLife.ScheduleJson(schedule with { Hour = 40 })));

        Assert.Equal(EndOfLife.DefaultPeriodMonths, EndOfLife.PeriodMonths(null));
        Assert.Equal(EndOfLife.MaxPeriodMonths, EndOfLife.PeriodMonths("99"));
        Assert.Equal(3, EndOfLife.PeriodMonths("3"));
    }

    [Fact]
    public void The_headline_of_a_message_counts_what_needs_attention()
    {
        EndOfLifeNotice Notice(int endOfLife, int soon) => new(D(2026, 12, 1), 6, [new EndOfLifeNoticeClient("ACME", "Acme", endOfLife, soon, 0, 0, [])]);
        Assert.Equal("No endpoint at or near end of life", Notice(0, 0).Headline);
        Assert.Equal("1 endpoint at end of life", Notice(1, 0).Headline);
        Assert.Equal("3 endpoints at end of life, 2 ending soon", Notice(3, 2).Headline);
        Assert.Equal("1 endpoint ending soon", Notice(0, 1).Headline);
    }
}
