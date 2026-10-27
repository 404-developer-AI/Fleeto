using Fleeto.Core.Domain;

namespace Fleeto.Core.Dashboards;

/// <summary>The settings a tile type offers in the designer; validation clears every other one.</summary>
[Flags]
public enum TileOptions
{
    None = 0,

    /// <summary>The tile reads data of clients, so it follows a scope.</summary>
    Scope = 1 << 0,

    /// <summary>The scope can be limited to servers or workstations.</summary>
    Class = 1 << 1,

    /// <summary>Warning and critical from a value upward.</summary>
    ThresholdsAbove = 1 << 2,

    /// <summary>Warning and critical from a value downward (lower is worse).</summary>
    ThresholdsBelow = 1 << 3,

    Severity = 1 << 4,
    AlertKinds = 1 << 5,
    IncludeHeld = 1 << 6,
    Tier = 1 << 7,
    FailedOnly = 1 << 8,
    Days = 1 << 9,
    Grouping = 1 << 10,
    Text = 1 << 11,
    Url = 1 << 12,
    HideWhenEmpty = 1 << 13
}

/// <summary>The live events after which a tile reloads (the events of <c>LiveUpdates</c> in web).</summary>
[Flags]
public enum TileLiveEvents
{
    None = 0,
    EndpointStatus = 1 << 0,
    Alerts = 1 << 1,
    Jobs = 1 << 2,
    Tasks = 1 << 3,
    PatchDeployments = 1 << 4
}

public enum TileKind
{
    Counter,
    List,
    Overview,
    Chart,
    Content
}

/// <summary>
/// A tile type of the catalog. <paramref name="Permissions"/> are all needed to see the tile; an empty list is allowed only for
/// content tiles and for a tile marked <paramref name="ForEveryone"/>. <paramref name="Source"/> names the data source in web;
/// tiles with the same source and scope share one read.
/// </summary>
public sealed record TileType(
    string Key,
    TileKind Kind,
    string Group,
    string Name,
    string Description,
    IReadOnlyList<string> Permissions,
    TileSize Min,
    TileSize Max,
    TileSize Default,
    TileOptions Options,
    string? Source,
    TileLiveEvents LiveEvents,
    string Since,
    bool ForEveryone = false)
{
    public bool Has(TileOptions option) => (Options & option) == option;

    /// <summary>The tile reads data of the instance as a whole, never of a client: no scope, and its permission is instance-wide.</summary>
    public bool InstanceWide => !Has(TileOptions.Scope);
}

public readonly record struct TileSize(int W, int H);

/// <summary>
/// The catalog of tile types (0.8.0, DASHBOARDS.md §3), the only list of what a dashboard can show, in the way of
/// <see cref="Domain.Permissions"/>. Every new part of the dashboard is a tile type here, never markup in a page. Keys are stored
/// in layouts, so they never change; a removed type keeps its key reserved in <see cref="Retired"/>. A feature that adds a type
/// decides in <see cref="DefaultDashboard"/> whether the default dashboard shows it; a dashboard of the instance gets it only when
/// somebody adds it.
/// </summary>
public static class DashboardTiles
{
    public const string CountersGroup = "Counters";
    public const string ListsGroup = "Lists";
    public const string OverviewsGroup = "Overviews";
    public const string ChartsGroup = "Charts";
    public const string ContentGroup = "Content";

    public const string EndpointsOnline = "endpoints.online";
    public const string EndpointsOffline = "endpoints.offline";
    public const string EndpointsTotal = "endpoints.total";
    public const string EndpointsInMaintenance = "endpoints.maintenance";
    public const string AgentsOutOfDate = "agents.out-of-date";
    public const string AlertsOpen = "alerts.open";
    public const string AlertsOnHold = "alerts.on-hold";
    public const string PatchCompliance = "patches.compliance";
    public const string Vulnerabilities = "vulnerabilities.summary";
    public const string Licenses = "licenses.usage";
    public const string LicensesFree = "licenses.free";
    public const string InstanceHealth = "instance.health";
    public const string Security = "security.summary";
    public const string Backups = "backups.state";

    public const string AlertList = "alerts.list";
    public const string RecentActivity = "activity.recent";
    public const string RecentJobs = "jobs.recent";
    public const string Advisories = "advisories.list";
    public const string OfflineList = "endpoints.offline-list";
    public const string OutOfDateList = "agents.out-of-date-list";
    public const string TaskRuns = "tasks.runs";

    public const string ClientStatus = "clients.status";

    public const string AlertsPerDay = "charts.alerts-per-day";
    public const string EndpointsChart = "charts.endpoints";
    public const string TopClients = "charts.top-clients";

    public const string Heading = "content.heading";
    public const string Text = "content.text";
    public const string Link = "content.link";

    // Data sources in web; tile types with the same source and scope share one read.
    public const string EndpointCountsSource = "endpoint-counts";
    public const string AlertCountsSource = "alert-counts";
    public const string PatchSource = "patch-compliance";
    public const string VulnerabilitySource = "vulnerabilities";
    public const string LicenseSource = "license";
    public const string HealthSource = "instance-health";
    public const string SecuritySource = "security";
    public const string BackupSource = "backups";
    public const string AlertListSource = "alert-list";
    public const string ActivitySource = "activity";
    public const string JobsSource = "jobs";
    public const string OfflineListSource = "offline-list";
    public const string OutOfDateListSource = "out-of-date-list";
    public const string TaskRunsSource = "task-runs";
    public const string ClientStatusSource = "client-status";
    public const string AlertsPerDaySource = "alerts-per-day";
    public const string EndpointsChartSource = "endpoints-chart";
    public const string TopClientsSource = "top-clients";

    private const TileOptions ScopeAndClass = TileOptions.Scope | TileOptions.Class;

    private static readonly TileSize Small = new(2, 2);
    private static readonly TileSize CounterMax = new(4, 3);

    public static readonly IReadOnlyList<TileType> All =
    [
        new(EndpointsOnline, TileKind.Counter, CountersGroup, "Endpoints online", "Endpoints connected to the gateway, of all endpoints.",
            [Permissions.ClientsView], new(2, 2), CounterMax, Small, ScopeAndClass | TileOptions.ThresholdsBelow, EndpointCountsSource,
            TileLiveEvents.EndpointStatus, "0.8.0"),
        new(EndpointsOffline, TileKind.Counter, CountersGroup, "Endpoints offline", "Endpoints not connected to the gateway.",
            [Permissions.ClientsView], new(2, 2), CounterMax, Small, ScopeAndClass | TileOptions.ThresholdsAbove, EndpointCountsSource,
            TileLiveEvents.EndpointStatus, "0.8.0"),
        new(EndpointsTotal, TileKind.Counter, CountersGroup, "Endpoints", "Endpoints, optionally only managed or agent-only ones.",
            [Permissions.ClientsView], new(2, 2), CounterMax, Small, ScopeAndClass | TileOptions.Tier, EndpointCountsSource,
            TileLiveEvents.EndpointStatus, "0.8.0"),
        new(EndpointsInMaintenance, TileKind.Counter, CountersGroup, "Endpoints in maintenance",
            "Endpoints in maintenance by hand or by a maintenance window.", [Permissions.ClientsView], new(2, 2), CounterMax, Small,
            ScopeAndClass | TileOptions.ThresholdsAbove, EndpointCountsSource, TileLiveEvents.EndpointStatus, "0.8.0"),
        new(AgentsOutOfDate, TileKind.Counter, CountersGroup, "Agents out of date", "Agents older than the release this instance offers.",
            [Permissions.ClientsView], new(2, 2), CounterMax, Small, ScopeAndClass | TileOptions.ThresholdsAbove, EndpointCountsSource,
            TileLiveEvents.EndpointStatus, "0.8.0"),
        new(AlertsOpen, TileKind.Counter, CountersGroup, "Open alerts", "Open alerts by severity, without alerts on hold.",
            [Permissions.AlertsView], new(2, 2), CounterMax, Small, ScopeAndClass | TileOptions.ThresholdsAbove | TileOptions.Severity,
            AlertCountsSource, TileLiveEvents.Alerts, "0.8.0"),
        new(AlertsOnHold, TileKind.Counter, CountersGroup, "Alerts on hold", "Unresolved alerts on hold until a time.",
            [Permissions.AlertsView], new(2, 2), CounterMax, Small, ScopeAndClass | TileOptions.ThresholdsAbove, AlertCountsSource,
            TileLiveEvents.Alerts, "0.8.0"),
        new(PatchCompliance, TileKind.Counter, CountersGroup, "Patch compliance",
            "Managed Windows endpoints that Action1 reports as up to date.", [Permissions.ClientsView], new(2, 2), CounterMax, Small,
            TileOptions.Scope | TileOptions.ThresholdsBelow | TileOptions.HideWhenEmpty, PatchSource, TileLiveEvents.PatchDeployments, "0.8.0"),
        new(Vulnerabilities, TileKind.Counter, CountersGroup, "Vulnerabilities",
            "Linux endpoints missing a critical fix or with an actively exploited vulnerability.", [Permissions.VulnerabilitiesView],
            new(2, 2), CounterMax, Small, TileOptions.Scope | TileOptions.ThresholdsAbove | TileOptions.HideWhenEmpty, VulnerabilitySource,
            TileLiveEvents.None, "0.8.0"),
        new(Licenses, TileKind.Counter, CountersGroup, "Licenses", "Managed licenses in use and the state of the license.",
            [Permissions.LicensingManage], new(2, 2), CounterMax, Small, TileOptions.None, LicenseSource, TileLiveEvents.EndpointStatus, "0.8.0"),
        new(LicensesFree, TileKind.Counter, CountersGroup, "Licenses free", "Managed licenses still free in the pool of the instance.",
            [Permissions.LicensingManage], new(2, 2), CounterMax, Small, TileOptions.ThresholdsBelow, LicenseSource,
            TileLiveEvents.EndpointStatus, "0.8.0"),
        new(InstanceHealth, TileKind.Counter, CountersGroup, "Instance health", "Problems of the VPS, the database and the instance.",
            [Permissions.InstanceHealthView], new(2, 2), CounterMax, Small, TileOptions.None, HealthSource, TileLiveEvents.None, "0.8.0"),
        new(Security, TileKind.Counter, CountersGroup, "Security", "Open security incidents and blocked addresses.",
            [Permissions.SecurityView], new(2, 2), CounterMax, Small, TileOptions.None, SecuritySource, TileLiveEvents.None, "0.8.0"),
        new(Backups, TileKind.Counter, CountersGroup, "Backups", "The last successful backup of the instance.",
            [], new(2, 2), CounterMax, Small, TileOptions.None, BackupSource, TileLiveEvents.None, "0.8.0", ForEveryone: true),

        new(AlertList, TileKind.List, ListsGroup, "Open alerts list", "The newest open alerts, with their actions.",
            [Permissions.AlertsView], new(4, 3), new(12, 16), new(7, 8),
            ScopeAndClass | TileOptions.Severity | TileOptions.AlertKinds | TileOptions.IncludeHeld, AlertListSource, TileLiveEvents.Alerts,
            "0.8.0"),
        new(RecentActivity, TileKind.List, ListsGroup, "Recent activity", "The newest entries of the audit log the user may see.",
            [Permissions.ClientsView], new(3, 3), new(12, 16), new(5, 5), TileOptions.Scope, ActivitySource,
            TileLiveEvents.Alerts | TileLiveEvents.Jobs, "0.8.0"),
        new(RecentJobs, TileKind.List, ListsGroup, "Recent jobs", "Scripts started on endpoints, newest first.",
            [Permissions.JobsView], new(3, 3), new(12, 16), new(5, 5), TileOptions.Scope | TileOptions.FailedOnly, JobsSource,
            TileLiveEvents.Jobs, "0.8.0"),
        new(Advisories, TileKind.List, ListsGroup, "Security advisories", "The newest security advisories of the CCB.",
            [Permissions.AdvisoriesView], new(3, 3), new(12, 16), new(5, 4), TileOptions.None, null, TileLiveEvents.None, "0.8.0"),
        new(OfflineList, TileKind.List, ListsGroup, "Endpoints offline list", "Endpoints not connected, longest offline first.",
            [Permissions.ClientsView], new(3, 3), new(12, 16), new(4, 5), ScopeAndClass | TileOptions.Tier, OfflineListSource,
            TileLiveEvents.EndpointStatus, "0.8.0"),
        new(OutOfDateList, TileKind.List, ListsGroup, "Agents out of date list", "Endpoints whose agent is older than the current release.",
            [Permissions.ClientsView], new(3, 3), new(12, 16), new(4, 5), ScopeAndClass, OutOfDateListSource, TileLiveEvents.EndpointStatus,
            "0.8.0"),
        new(TaskRuns, TileKind.List, ListsGroup, "Task runs", "Running and failed runs of tasks, newest first.",
            [Permissions.TasksView], new(3, 3), new(12, 16), new(5, 5), TileOptions.Scope | TileOptions.FailedOnly, TaskRunsSource,
            TileLiveEvents.Tasks, "0.8.0"),

        new(ClientStatus, TileKind.Overview, OverviewsGroup, "Client status",
            "A cell per client colored by its worst state, with its open alerts.", [Permissions.ClientsView], new(3, 2), new(12, 16),
            new(6, 4), TileOptions.Scope, ClientStatusSource, TileLiveEvents.EndpointStatus | TileLiveEvents.Alerts, "0.8.0"),

        new(AlertsPerDay, TileKind.Chart, ChartsGroup, "Alerts opened per day", "Alerts opened per day, by severity.",
            [Permissions.AlertsView], new(4, 3), new(12, 10), new(6, 4), TileOptions.Scope | TileOptions.Days, AlertsPerDaySource,
            TileLiveEvents.None, "0.8.0"),
        new(EndpointsChart, TileKind.Chart, ChartsGroup, "Endpoints by group", "Endpoints by operating system, class or tier.",
            [Permissions.ClientsView], new(3, 3), new(12, 10), new(4, 4), ScopeAndClass | TileOptions.Grouping, EndpointsChartSource,
            TileLiveEvents.None, "0.8.0"),
        new(TopClients, TileKind.Chart, ChartsGroup, "Top clients by open alerts", "The clients with the most open alerts.",
            [Permissions.AlertsView], new(3, 3), new(12, 10), new(4, 4), ScopeAndClass, TopClientsSource, TileLiveEvents.Alerts, "0.8.0"),

        new(Heading, TileKind.Content, ContentGroup, "Heading", "A title across the grid, to make sections.",
            [], new(2, 1), new(12, 1), new(12, 1), TileOptions.None, null, TileLiveEvents.None, "0.8.0"),
        new(Text, TileKind.Content, ContentGroup, "Text", "A note in markdown: no HTML and no images.",
            [], new(2, 1), new(12, 12), new(4, 3), TileOptions.Text, null, TileLiveEvents.None, "0.8.0"),
        new(Link, TileKind.Content, ContentGroup, "Link", "A link to a page of Fleeto or an https address.",
            [], new(2, 1), new(6, 3), new(2, 1), TileOptions.Url, null, TileLiveEvents.None, "0.8.0"),
    ];

    /// <summary>Keys of tile types that were removed; never used again.</summary>
    public static readonly IReadOnlyList<string> Retired = [];

    public static readonly IReadOnlyList<string> Groups = [CountersGroup, ListsGroup, OverviewsGroup, ChartsGroup, ContentGroup];

    private static readonly Dictionary<string, TileType> ByKey = All.ToDictionary(t => t.Key, StringComparer.Ordinal);

    public static TileType? Find(string? key) => key is null ? null : ByKey.GetValueOrDefault(key);

    /// <summary>The user holds every permission the tile type needs.</summary>
    public static bool CanSee(TileType type, Func<string, bool> has) => type.Permissions.All(has);
}
