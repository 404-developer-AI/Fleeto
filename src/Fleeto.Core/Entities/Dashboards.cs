namespace Fleeto.Core.Entities;

/// <summary>
/// A dashboard of the instance (0.8.0, DASHBOARDS.md §8), designed in Settings and linked to roles. Of the instance as a whole: the
/// layout holds no data, only tiles whose data is read in the scope of the viewer. A dashboard without a published layout is never
/// shown.
/// </summary>
public class Dashboard
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    /// <summary>The order: a user sees the first published dashboard linked to one of its roles. Unique.</summary>
    public int Priority { get; set; }

    /// <summary>The layout users see (jsonb); null until the first publish.</summary>
    public string? PublishedLayout { get; set; }

    /// <summary>The layout being designed (jsonb); null when there are no changes since the last publish.</summary>
    public string? DraftLayout { get; set; }

    public int PublishedVersion { get; set; }
    public DateTime? PublishedAt { get; set; }
    public string? PublishedBy { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }

    public ICollection<DashboardRole> Roles { get; set; } = new List<DashboardRole>();
}

/// <summary>A role whose users may see the dashboard; the order of the dashboards decides between several.</summary>
public class DashboardRole
{
    public Guid DashboardId { get; set; }
    public Guid RoleId { get; set; }
}

/// <summary>A published version of a dashboard, so an earlier one can be opened and published again. The last 20 are kept.</summary>
public class DashboardVersion
{
    public Guid DashboardId { get; set; }
    public int Version { get; set; }
    public string Layout { get; set; } = string.Empty;
    public DateTime PublishedAt { get; set; }
    public string PublishedBy { get; set; } = string.Empty;
}

/// <summary>
/// Alerts opened per client, day and severity, counted by the workers (0.8.0), so a chart over 90 days never scans the alerts.
/// The day is a UTC date.
/// </summary>
public class DashboardDailyCount
{
    public Guid ClientId { get; set; }
    public DateOnly Day { get; set; }
    public int Warning { get; set; }
    public int Critical { get; set; }
    public DateTime CountedAt { get; set; }
}
