namespace Fleeto.Core.Performance;

/// <summary>What the performance monitor measures (0.7.0). Stored by name.</summary>
public enum PerfLevel
{
    /// <summary>Nothing is measured.</summary>
    Off,

    /// <summary>Aggregates per minute of every layer, safe to leave on: below 1 % overhead.</summary>
    Basic
}

/// <summary>
/// What a row of the minute table counts. Stored by name. The value columns hold milliseconds unless a kind says otherwise, and
/// the key is always a template, a type name or a fixed name, never a value (PERFORMANCE.md §4).
/// </summary>
public enum PerfKind
{
    /// <summary>A Blazor interaction. Key: the route template of the page. Sub key: <c>load</c> or <c>event</c>.</summary>
    Interaction,

    /// <summary>An HTTP request outside the circuit. Key: route template. Sub key: method and status class (<c>GET 2xx</c>).</summary>
    Http,

    /// <summary>Database commands. Key: the source (<c>interaction:/clients</c>, <c>worker:patch-sync</c>, <c>other</c>).</summary>
    Db,

    /// <summary>One run of a workers loop. Key: loop name. Sub key: its interval in seconds.</summary>
    WorkerRun,

    /// <summary>An outbound HTTP call. Key: host. Sub key: status class, or <c>429</c>, or <c>error</c>.</summary>
    External,

    /// <summary>A message from an agent or watchdog. Key: message type. Sub key: <c>agent</c> or <c>watchdog</c>. Bytes: payload.</summary>
    AgentMessage,

    /// <summary>A job delivered to an agent. Key: job type. Sub key: <c>delivered</c>. Value: time from creation to delivery.</summary>
    JobDispatch,

    /// <summary>The remote session relay. Key: <c>browser</c> or <c>endpoint</c>. Bytes: relayed. Value: connection time.</summary>
    Relay,

    /// <summary>A signing request. Key: request kind. Sub key: outcome, or <c>wait</c> for the time it waited to be claimed.</summary>
    Signing,

    /// <summary>A live update to open pages. Key: update kind. Value: the number of pages it reached, not milliseconds.</summary>
    Broadcast,

    /// <summary>A notification handled in this process. Key: channel. Value: wait in the subscriber queue plus handling.</summary>
    Notification,

    /// <summary>An exception thrown in this process. Key: its type name. Counts only.</summary>
    Exception,

    /// <summary>Agent and watchdog connections. Key: <c>accepted</c>, <c>closed</c>, <c>refused</c> or <c>duplicate</c>. Counts only.</summary>
    Connection,

    /// <summary>A Blazor circuit (web). Key: <c>opened</c>, <c>closed</c>, <c>connection-down</c> or <c>connection-up</c>. Counts only.</summary>
    Circuit,

    /// <summary>A flush of the gateway's last-seen batch. Key: <c>last-seen</c>. Bytes: rows written.</summary>
    Batch
}

/// <summary>The questions the page answers. Every finding, score and section belongs to exactly one (PERFORMANCE.md §3).</summary>
public enum PerfCategory
{
    Hosting,
    Network,
    Code,
    Database,
    Maintenance,
    Frontend,
    Background,
    Agents
}

public enum PerfSeverity
{
    Info,
    Warning,
    Critical
}

/// <summary>A category with its label and the question shown on its card.</summary>
/// <param name="Measured">False for a category whose collectors are not built yet: it gets no card, only a line under limitations.</param>
public sealed record PerfCategoryInfo(PerfCategory Category, string Key, string Label, string Question, bool Measured);

public static class PerfCategories
{
    public static readonly IReadOnlyList<PerfCategoryInfo> All =
    [
        new(PerfCategory.Hosting, "hosting", "Hosting / server", "Is the VPS the bottleneck: CPU, steal, memory or disk?", true),
        new(PerfCategory.Network, "network", "Network / internet", "Is time lost between the browser and the server?", false),
        new(PerfCategory.Code, "code", "Backend code", "Is our own code, an external API or the pipeline slow?", true),
        new(PerfCategory.Database, "database", "Database queries", "Which queries cost most, and why?", true),
        new(PerfCategory.Maintenance, "maintenance", "Database maintenance", "Is the database cleaning up and caching well?", true),
        new(PerfCategory.Frontend, "frontend", "Frontend / browser", "Is a page slow to load or sluggish to use?", false),
        new(PerfCategory.Background, "background", "Background & realtime", "Do workers or live updates slow users down?", true),
        new(PerfCategory.Agents, "agents", "Agent channel", "Does the gateway keep up with the agents?", true),
    ];

    public static PerfCategoryInfo Of(PerfCategory category) => All.First(c => c.Category == category);
}

/// <summary>Where to look in the repository: a page route template, an API route, a loop, a message type, a query, a table or a host.</summary>
/// <param name="Kind">page, api, loop, message, job, signing, query, table, host, setting or component.</param>
/// <param name="Detail">For a query its normalized SQL.</param>
public sealed record PerfCodeRef(string Kind, string Value, string? Detail = null);

public sealed record PerfEvidence(string Label, string Value);

/// <summary>
/// One explainable finding (PERFORMANCE.md §7): what it means, the evidence, the likely cause, what to change and where to look.
/// <paramref name="ImpactMs"/> is the extra waiting it caused in the period, the ranking key; null where waiting cannot be
/// estimated (a full disk, for example).
/// </summary>
public sealed record PerfFinding(
    string Id,
    PerfCategory Category,
    PerfSeverity Severity,
    string Title,
    string Explanation,
    IReadOnlyList<PerfEvidence> Evidence,
    string Cause,
    string Action,
    IReadOnlyList<PerfCodeRef> CodeRefs,
    double? ImpactMs);

/// <summary>The health of a category: a score from 0 to 100, or no data, never a fake 100.</summary>
public sealed record PerfCategoryScore(PerfCategoryInfo Info, int? Score, string Conclusion, int Findings);
