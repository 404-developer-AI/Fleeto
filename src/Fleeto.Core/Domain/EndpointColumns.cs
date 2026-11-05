namespace Fleeto.Core.Domain;

/// <summary>
/// One column the endpoint list can show (0.8.0, decided 2026-11-04).
/// </summary>
/// <param name="Key">Stable key stored in a column layout; never renamed once released.</param>
/// <param name="Permission">A permission the viewer needs to see the column, besides viewing clients; null when none.</param>
/// <param name="Required">The column is in every layout (the host name names the row).</param>
public sealed record EndpointColumnInfo(string Key, string Name, string Description, string? Permission = null, bool Required = false);

/// <summary>
/// The columns of the endpoint list (0.8.0, decided 2026-11-04): the only list of columns. A column layout of the instance holds keys of
/// this catalog in the order they show; an admin builds it in Settings, Column layouts, and links it to roles. A key this release does
/// not know (written by a newer one) is skipped when shown and dropped when the layout is saved again.
/// </summary>
public static class EndpointColumns
{
    public const string Hostname = "hostname";
    public const string Description = "description";
    public const string Client = "client";
    public const string Site = "site";
    public const string OperatingSystem = "os";
    public const string User = "user";
    public const string Tier = "tier";
    public const string Status = "status";
    public const string LastSeen = "last_seen";
    public const string IpAddress = "ip_address";
    public const string PublicIpAddress = "public_ip";
    public const string AgentVersion = "agent_version";
    public const string OpenAlerts = "open_alerts";
    public const string Patches = "patches";
    public const string Cpu = "cpu";
    public const string Memory = "memory";
    public const string DiskFree = "disk_free";
    public const string Uptime = "uptime";

    /// <summary>Every column, in the order the editor offers them.</summary>
    public static readonly IReadOnlyList<EndpointColumnInfo> All =
    [
        new(Hostname, "Hostname", "The host name of the endpoint. Always shown.", Required: true),
        new(Description, "Description", "The description a technician gave the endpoint."),
        new(Client, "Client", "The client of the endpoint. Only in the list of all clients."),
        new(Site, "Site", "The site of the endpoint. Not in the list of one site."),
        new(OperatingSystem, "Operating system", "The name of the operating system."),
        new(User, "User", "The user signed in on the endpoint."),
        new(Tier, "Tier", "Managed or agent-only."),
        new(Status, "Status", "Online or offline."),
        new(LastSeen, "Last seen", "When the agent last sent a message."),
        new(IpAddress, "IP address", "The first IPv4 address of the network interfaces the agent reports; every address on hover."),
        new(PublicIpAddress, "Public IP address", "The address the agent connected to the instance from last time."),
        new(AgentVersion, "Agent version", "The version of the Fleeto agent."),
        new(OpenAlerts, "Open alerts", "Unresolved alerts, not counting alerts on hold.", Permissions.AlertsView),
        new(Patches, "Patches", "Missing updates as Action1 or a scan of the Fleeto agent reports them, or that the endpoint has no patch state.", Permissions.PatchesView),
        new(Cpu, "CPU", "The processor and its number of cores."),
        new(Memory, "Memory", "The installed memory."),
        new(DiskFree, "Disk free", "The free space of the fullest drive, as a percentage."),
        new(Uptime, "Uptime", "How long the endpoint has run since it last started."),
    ];

    /// <summary>The columns of the built-in default layout: the list as it was before 0.8.0, with the description after the host name.</summary>
    public static readonly IReadOnlyList<string> Default =
        [Hostname, Description, Client, Site, OperatingSystem, User, Tier, Status, LastSeen];

    private static readonly Dictionary<string, EndpointColumnInfo> ByKey = All.ToDictionary(c => c.Key, StringComparer.Ordinal);

    public static EndpointColumnInfo? Find(string key) => ByKey.GetValueOrDefault(key);

    /// <summary>
    /// The columns a viewer sees of a layout, in its order: known columns only, without the ones its permissions do not give, and
    /// without Client outside the list of all clients and Site in the list of one site. The host name is always there.
    /// </summary>
    public static IReadOnlyList<EndpointColumnInfo> Visible(IEnumerable<string> layout, Func<string, bool> has, bool allClients, bool oneSite)
    {
        var result = new List<EndpointColumnInfo>();
        foreach (var key in layout.Distinct(StringComparer.Ordinal))
        {
            if (Find(key) is not { } column ||
                (column.Permission is { } permission && !has(permission)) ||
                (column.Key == Client && !allClients) ||
                (column.Key == Site && oneSite))
            {
                continue;
            }

            result.Add(column);
        }

        if (result.All(c => c.Key != Hostname))
        {
            result.Insert(0, ByKey[Hostname]);
        }

        return result;
    }
}

/// <summary>A column layout as the rule that picks one for a user sees it: its order and the roles it is linked to.</summary>
public sealed record ColumnLayoutChoice(Guid Id, int Priority, IReadOnlyCollection<Guid> RoleIds);

/// <summary>
/// Rules of column layouts (0.8.0, decided 2026-11-04): which layout a user sees and what a layout may hold. The server checks every
/// layout here on save, whatever the editor sent.
/// </summary>
public static class ColumnLayoutRules
{
    public const int MaxNameLength = 80;
    public const int MaxDescriptionLength = 500;
    public const int MaxLayouts = 100;

    /// <summary>
    /// The layout a user sees: the first in priority order that is linked to one of its roles, or null for the built-in default
    /// (<see cref="EndpointColumns.Default"/>). The same rule as dashboards.
    /// </summary>
    public static Guid? Pick(IEnumerable<ColumnLayoutChoice> layouts, IReadOnlyCollection<Guid> roleIds) =>
        layouts.Where(l => l.RoleIds.Any(roleIds.Contains))
            .OrderBy(l => l.Priority)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefault();

    public static string? NameProblem(string? name)
    {
        var value = name?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return "Enter a name for the column layout.";
        }

        return value.Length > MaxNameLength ? $"A column layout name has at most {MaxNameLength} characters." : null;
    }

    /// <summary>
    /// Checks the columns of a layout and returns them cleaned, or the problem to show: known columns only, each once, the host name
    /// included.
    /// </summary>
    public static (IReadOnlyList<string>? Columns, string? Problem) Validate(IReadOnlyList<string>? columns)
    {
        if (columns is null || columns.Count == 0)
        {
            return (null, "Choose at least the host name column.");
        }

        if (columns.FirstOrDefault(c => EndpointColumns.Find(c) is null) is { } unknown)
        {
            return (null, $"The column \"{(unknown.Length > 40 ? unknown[..40] : unknown)}\" does not exist in this version of Fleeto.");
        }

        if (columns.Distinct(StringComparer.Ordinal).Count() != columns.Count)
        {
            return (null, "A column can be in a layout only once.");
        }

        if (!columns.Contains(EndpointColumns.Hostname))
        {
            return (null, "The host name column is in every layout.");
        }

        return (columns.ToList(), null);
    }
}

/// <summary>Limits of the description of an endpoint (0.8.0).</summary>
public static class EndpointDescriptionRules
{
    public const int MaxLength = 200;

    /// <summary>The description to store (trimmed, one line, null when empty), or the problem to show.</summary>
    public static (string? Value, string? Problem) Clean(string? description)
    {
        var value = string.Join(' ', (description ?? string.Empty).Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (value.Length == 0)
        {
            return (null, null);
        }

        return value.Length > MaxLength ? (null, $"A description has at most {MaxLength} characters.") : (value, null);
    }
}
