using Fleeto.Core.Domain;

namespace Fleeto.Core.Entities;

/// <summary>
/// One release of an operating system with its end-of-life dates (0.8.0 step 4, VULNERABILITIES.md §12.3), fetched from endoflife.date
/// and replaced per product by every successful fetch. Synology DSM is not stored: its dates are a list in the code
/// (<see cref="EndOfLife.SynologyDsmReleases"/>). Instance-wide, public data.
/// </summary>
public class EndOfLifeReleaseRecord
{
    public EndOfLifeProduct Product { get; set; }
    public string Release { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;

    /// <summary>The first day without security updates; null when none was announced.</summary>
    public DateOnly? EndOfLifeOn { get; set; }

    /// <summary>The first day a paid extension (ESU, Ubuntu Pro, Extended LTS) no longer covers; null when there is none.</summary>
    public DateOnly? ExtensionUntil { get; set; }

    /// <summary>Windows: the build of the release ("10.0.26200"), by which an endpoint is matched.</summary>
    public string? LatestBuild { get; set; }

    public DateTime FetchedAt { get; set; }

    public EndOfLifeRelease ToRelease() => new(Product, Release, Label, EndOfLifeOn, ExtensionUntil, LatestBuild);
}

/// <summary>The fetch state of one product of endoflife.date, like <see cref="VulnerabilityFeed"/>.</summary>
public class EndOfLifeSource
{
    public EndOfLifeProduct Product { get; set; }
    public DateTime? LastAttemptAt { get; set; }
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>When a fetch last changed a date, a label or the list of releases.</summary>
    public DateTime? LastChangedAt { get; set; }

    public DateTime? FailingSince { get; set; }
    public int ReleaseCount { get; set; }
    public string? Problem { get; set; }
}

/// <summary>
/// One run of the end-of-life report: on the schedule or by "Refresh now". Its rows hold the managed endpoints that are not supported
/// for long any more; <see cref="EndOfLifeReportCount"/> counts every managed endpoint per site and status. Kept 13 months.
/// </summary>
public class EndOfLifeReport
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>True for a run of the schedule, false for "Refresh now".</summary>
    public bool Scheduled { get; set; }

    /// <summary>The user behind "Refresh now"; null for the schedule.</summary>
    public Guid? RequestedByUserId { get; set; }

    public string? RequestedByName { get; set; }

    /// <summary>The day the statuses were decided, in the time zone of the schedule.</summary>
    public DateOnly Day { get; set; }

    /// <summary>The period of "Ending soon" this run used.</summary>
    public int PeriodMonths { get; set; }

    /// <summary>The oldest successful fetch of the products the run used; null when nothing was fetched yet.</summary>
    public DateTime? DataAsOf { get; set; }

    /// <summary>True once the message to the notification channels was written (scheduled runs only).</summary>
    public bool Notified { get; set; }
}

/// <summary>Per report, site and status the number of managed endpoints, so supported endpoints need no row of their own.</summary>
public class EndOfLifeReportCount
{
    public Guid ReportId { get; set; }
    public Guid ClientId { get; set; }
    public Guid SiteId { get; set; }
    public EndOfLifeStatus Status { get; set; }
    public int Endpoints { get; set; }
}

/// <summary>One managed endpoint of a report that is not <see cref="EndOfLifeStatus.Supported"/>, as it was on the day of the report.</summary>
public class EndOfLifeReportRow
{
    public Guid ReportId { get; set; }
    public Guid ClientId { get; set; }
    public Guid SiteId { get; set; }
    public Guid EndpointId { get; set; }
    public string Hostname { get; set; } = string.Empty;
    public string OsName { get; set; } = string.Empty;
    public EndOfLifeProduct? Product { get; set; }
    public string? Release { get; set; }
    public string? ReleaseLabel { get; set; }
    public EndOfLifeStatus Status { get; set; }
    public DateOnly? EndOfLifeOn { get; set; }
    public DateOnly? ExtensionUntil { get; set; }

    /// <summary>True when the extension counted: marked by a user, or Ubuntu Pro reported by the agent.</summary>
    public bool ExtensionCounted { get; set; }
}

/// <summary>"Refresh now" of the tab: written by web, run by the workers, which reach endoflife.date.</summary>
public class EndOfLifeRefreshRequest
{
    public Guid Id { get; set; }
    public DateTime RequestedAt { get; set; }
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public DateTime? HandledAt { get; set; }
    public Guid? ReportId { get; set; }
    public string? Problem { get; set; }
}
