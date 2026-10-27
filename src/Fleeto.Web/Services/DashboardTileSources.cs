using Fleeto.Core.Dashboards;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;

namespace Fleeto.Web.Services;

/// <summary>What one tile asks of its source: its type, settings, the scope it reads in and how many rows fit.</summary>
public sealed record TileRequest(TileType Type, TileSettings Settings, TileReadScope Scope, int Rows);

/// <summary>
/// The data source of one or more tile types (DASHBOARDS.md §6). Tiles with the same source, scope and <see cref="ShareKey"/> share
/// one read.
/// </summary>
public interface IDashboardTileSource
{
    string Key { get; }

    /// <summary>What besides the scope makes two reads differ (a filter or the number of rows); empty when nothing does.</summary>
    string ShareKey(TileRequest request);

    Task<object?> LoadAsync(TileRequest request, CancellationToken cancellationToken);
}

/// <summary>
/// Every data source of the catalog. A test fails when a tile type names a source that is not here. Each read is measured by the
/// performance monitor under the source key (a fixed name, never a value).
/// </summary>
public sealed class DashboardTileSources
{
    private readonly Dictionary<string, IDashboardTileSource> _sources;
    private readonly PerfRecorder? _perf;

    public DashboardTileSources(DashboardService data, PatchService patches, VulnerabilityService vulnerabilities, InstanceHealthService health,
        SecurityService security, JobService jobs, ClientService clients, PerfRecorder? perf = null)
    {
        _perf = perf;
        IDashboardTileSource[] all =
        [
            new Source(DashboardTiles.EndpointCountsSource, _ => string.Empty,
                (r, ct) => Box(data.GetEndpointCountsAsync(r.Scope.Caller, r.Scope.Class, ct))),
            new Source(DashboardTiles.AlertCountsSource, _ => string.Empty,
                (r, ct) => Box(data.GetAlertCountsAsync(r.Scope.Caller, r.Scope.Class, ct))),
            new Source(DashboardTiles.PatchSource, _ => string.Empty,
                async (r, ct) => r.Scope.Caller.Has(Core.Domain.Permissions.ClientsView)
                    ? await patches.GetComplianceAsync(r.Scope.Caller, cancellationToken: ct)
                    : null),
            new Source(DashboardTiles.VulnerabilitySource, _ => string.Empty, (r, ct) => Box(vulnerabilities.GetTileAsync(r.Scope.Caller, ct))),
            new Source(DashboardTiles.LicenseSource, _ => string.Empty, (r, ct) => Box(data.GetLicenseAsync(r.Scope.Caller, ct))),
            new Source(DashboardTiles.HealthSource, _ => string.Empty, (r, ct) => Box(health.GetTileAsync(r.Scope.Caller, ct))),
            new Source(DashboardTiles.SecuritySource, _ => string.Empty, (r, ct) => Box(security.GetTileAsync(r.Scope.Caller, ct))),
            new Source(DashboardTiles.BackupSource, _ => string.Empty, (_, ct) => Box(data.GetBackupsAsync(ct))),
            new Source(DashboardTiles.AlertListSource,
                r => $"{r.Settings.Severity}|{string.Join(',', r.Settings.AlertKinds ?? [])}|{r.Settings.IncludeHeld}|{r.Rows}",
                (r, ct) => Box(data.GetOpenAlertsAsync(r.Scope.Caller, r.Scope.Class, r.Settings.Severity, r.Settings.AlertKinds,
                    r.Settings.IncludeHeld, r.Rows, ct))),
            new Source(DashboardTiles.ActivitySource, r => r.Rows.ToString(), (r, ct) => Box(data.GetRecentActivityAsync(r.Scope.Caller, r.Rows, ct))),
            new Source(DashboardTiles.JobsSource, r => $"{r.Settings.FailedOnly}|{r.Rows}",
                async (r, ct) => r.Scope.Caller.Has(Core.Domain.Permissions.JobsView)
                    ? await jobs.ListRecentAsync(r.Scope.Caller, r.Rows, ct, r.Settings.FailedOnly)
                    : null),
            new Source(DashboardTiles.OfflineListSource, r => $"{r.Settings.Tier}|{r.Rows}",
                (r, ct) => Box(data.GetOfflineEndpointsAsync(r.Scope.Caller, r.Scope.Class, r.Settings.Tier, r.Rows, ct))),
            new Source(DashboardTiles.OutOfDateListSource, r => r.Rows.ToString(),
                (r, ct) => Box(data.GetOutOfDateEndpointsAsync(r.Scope.Caller, r.Scope.Class, r.Rows, ct))),
            new Source(DashboardTiles.TaskRunsSource, r => $"{r.Settings.FailedOnly}|{r.Rows}",
                (r, ct) => Box(data.GetTaskRunsAsync(r.Scope.Caller, r.Settings.FailedOnly, r.Rows, ct))),
            new Source(DashboardTiles.ClientStatusSource, _ => string.Empty,
                async (r, ct) => r.Scope.Caller.Has(Core.Domain.Permissions.ClientsView)
                    ? await clients.ListTreeAsync(r.Scope.Caller, cancellationToken: ct)
                    : null),
            new Source(DashboardTiles.AlertsPerDaySource, r => (r.Settings.Days ?? 30).ToString(),
                (r, ct) => Box(data.GetAlertsPerDayAsync(r.Scope.Caller, r.Settings.Days ?? 30, ct))),
            new Source(DashboardTiles.EndpointsChartSource, r => r.Settings.Grouping?.ToString() ?? string.Empty,
                (r, ct) => Box(data.GetEndpointGroupsAsync(r.Scope.Caller, r.Scope.Class, r.Settings.Grouping ?? EndpointGrouping.OperatingSystem, ct))),
            new Source(DashboardTiles.TopClientsSource, r => r.Rows.ToString(),
                (r, ct) => Box(data.GetTopClientsAsync(r.Scope.Caller, r.Scope.Class, r.Rows, ct))),
        ];
        _sources = all.ToDictionary(s => s.Key, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> Keys => _sources.Keys;

    public IDashboardTileSource? Find(string? key) => key is null ? null : _sources.GetValueOrDefault(key);

    /// <summary>The key under which tiles share one read.</summary>
    public string ReadKey(TileRequest request) =>
        $"{request.Type.Source}|{request.Scope.Key}|{_sources[request.Type.Source!].ShareKey(request)}";

    /// <summary>Reads one source, measured by the performance monitor.</summary>
    public async Task<object?> LoadAsync(TileRequest request, CancellationToken cancellationToken)
    {
        var source = _sources[request.Type.Source!];
        var measured = _perf?.Collects(PerfCollector.Interactions) == true;
        var scope = measured ? PerfContext.Begin(PerfKeys.InteractionSource("/") + "#" + source.Key) : null;
        var failed = false;
        try
        {
            return await source.LoadAsync(request, cancellationToken);
        }
        catch
        {
            failed = true;
            throw;
        }
        finally
        {
            if (scope is not null)
            {
                _perf!.Complete(PerfCollector.Interactions, PerfKind.Interaction, "/", "tile:" + source.Key, scope, failed);
            }
        }
    }

    private static async Task<object?> Box<T>(Task<T> task) => await task;

    private sealed class Source(string key, Func<TileRequest, string> share, Func<TileRequest, CancellationToken, Task<object?>> load)
        : IDashboardTileSource
    {
        public string Key => key;
        public string ShareKey(TileRequest request) => share(request);
        public Task<object?> LoadAsync(TileRequest request, CancellationToken cancellationToken) => load(request, cancellationToken);
    }
}
