using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Checks;

/// <summary>
/// Storage analysis in the workers (0.6.0):
/// <list type="bullet">
/// <item>Gives the folder growth checks their results after every storage scan. Fleeto evaluates these checks itself from the
/// stored scans, so the workers write a result as the gateway does for an agent, and check evaluation turns it into a state and
/// an alert like any other.</item>
/// <item>Asks for a scan of a managed endpoint whose Disk free check is in warning or critical, when it had no scan and no
/// request for <see cref="StorageRules.DiskFreeScanGap"/>, so the folder that fills the disk is known when somebody looks.</item>
/// </list>
/// Woken by <c>fleeto_storage_scans</c>, and polls every 5 minutes so a lost notification only delays the result.
/// </summary>
public sealed class StorageScanService : WorkerLoop
{
    internal const int BatchSize = 500;

    /// <summary>A scan stamped before the previous pass may commit after it; scans this far back are looked at again.</summary>
    private static readonly TimeSpan CommitOverlap = TimeSpan.FromMinutes(5);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly Dictionary<Guid, DateTime> _evaluated = [];
    private DateTime? _watermark;
    private IDisposable? _subscription;

    public StorageScanService(IFleetoDbContextFactory dbFactory, INotificationBus bus, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<StorageScanService> logger)
        : base("storage-scans", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.StorageScans, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var more = await EvaluateNewScansAsync(cancellationToken) >= BatchSize;
        await RequestScansForDiskFreeAsync(cancellationToken);
        return more;
    }

    /// <summary>
    /// Writes folder growth results for the scans stored since the previous pass, each scan once. Returns the number of scans
    /// looked at.
    /// </summary>
    public async Task<int> EvaluateNewScansAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var since = (_watermark ?? now.AddHours(-1)) - CommitOverlap;
        foreach (var old in _evaluated.Where(e => e.Value < since).Select(e => e.Key).ToList())
        {
            _evaluated.Remove(old);
        }

        await using var db = _dbFactory.CreateSystem();
        var scans = await db.StorageScans.AsNoTracking()
            .Where(s => s.ReceivedAt > since)
            .OrderBy(s => s.ReceivedAt)
            .Select(s => new { s.Id, s.EndpointId, s.Volume, s.ReceivedAt })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        var fresh = scans.Where(s => !_evaluated.ContainsKey(s.Id)).ToList();
        if (fresh.Count > 0)
        {
            var written = await FolderGrowthChecks.WriteResultsAsync(db,
                fresh.Select(s => (s.EndpointId, s.Volume)).Distinct().ToList(), now, cancellationToken);
            if (written.Count > 0)
            {
                await db.SaveChangesAsync(cancellationToken);
                foreach (var endpointId in written)
                {
                    await _bus.PublishAsync(NotificationChannels.CheckResults, endpointId.ToString(), cancellationToken);
                }
            }

            foreach (var scan in fresh)
            {
                _evaluated[scan.Id] = scan.ReceivedAt;
            }
        }

        _watermark = scans.Count >= BatchSize ? scans[^1].ReceivedAt : now;
        return scans.Count;
    }

    /// <summary>
    /// Asks for a scan of every managed endpoint whose Disk free check is in warning or critical and that had no scan and no
    /// request within <see cref="StorageRules.DiskFreeScanGap"/>, if its agent scans storage. Returns the number of requests.
    /// </summary>
    public async Task<int> RequestScansForDiskFreeAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var since = now - StorageRules.DiskFreeScanGap;
        await using var db = _dbFactory.CreateSystem();
        var candidates = await db.Endpoints.AsNoTracking()
            .Where(e => e.Tier == EndpointTier.Managed &&
                        db.CheckStates.Any(s => s.EndpointId == e.Id && (s.Status == CheckStatus.Warning || s.Status == CheckStatus.Critical) &&
                                                db.CheckDefinitions.Any(d => d.Id == s.CheckDefinitionId && d.Type == CheckType.DiskFree)) &&
                        !db.StorageScans.Any(s => s.EndpointId == e.Id && s.ReceivedAt > since) &&
                        !db.StorageScanRequests.Any(r => r.EndpointId == e.Id && r.RequestedAt > since))
            .Select(e => new { e.Id, e.ClientId, e.AgentVersion })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var requests = candidates
            .Where(e => StorageRules.AgentSupportsStorageScan(e.AgentVersion))
            .Select(e => new StorageScanRequest
            {
                Id = Guid.CreateVersion7(),
                ClientId = e.ClientId,
                EndpointId = e.Id,
                Reason = StorageScanReason.DiskFree,
                RequestedByName = "Fleeto",
                RequestedAt = now,
                ExpiresAt = now + StorageRules.DiskFreeScanGap
            })
            .ToList();
        if (requests.Count == 0)
        {
            return 0;
        }

        db.StorageScanRequests.AddRange(requests);
        await db.SaveChangesAsync(cancellationToken);
        Logger.LogInformation("Asked {Count} endpoints with low disk space for a storage scan", requests.Count);
        return requests.Count;
    }
}

/// <summary>
/// The folder growth check (0.6.0): per drive, how much the used space grew over the period of the check, measured on the
/// storage scans, with the folder that grew most as the detail. Only complete scans count: an incomplete one undercounts.
/// </summary>
public static class FolderGrowthChecks
{
    /// <summary>
    /// Writes a result for every folder growth check that applies to one of the scanned volumes. Returns the endpoints that got
    /// one, so check evaluation can be woken for them. The caller saves.
    /// </summary>
    public static async Task<IReadOnlyList<Guid>> WriteResultsAsync(FleetoDbContext db, IReadOnlyCollection<(Guid EndpointId, string Volume)> scanned,
        DateTime now, CancellationToken cancellationToken)
    {
        if (scanned.Count == 0 || !await db.CheckDefinitions.AnyAsync(d => d.Type == CheckType.FolderGrowth && d.Enabled, cancellationToken))
        {
            return [];
        }

        var ids = scanned.Select(s => s.EndpointId).Distinct().ToList();
        var endpoints = await db.Endpoints.AsNoTracking()
            .Where(e => ids.Contains(e.Id) && e.Tier == EndpointTier.Managed)
            .ToListAsync(cancellationToken);
        var written = new List<Guid>();
        foreach (var endpoint in endpoints)
        {
            var checks = (await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: false, cancellationToken))
                .Where(c => c.Type == CheckType.FolderGrowth)
                .ToList();
            if (checks.Count == 0)
            {
                continue;
            }

            var any = false;
            foreach (var volume in scanned.Where(s => s.EndpointId == endpoint.Id).Select(s => s.Volume).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var history = await db.StorageScans.AsNoTracking()
                    .Where(s => s.EndpointId == endpoint.Id && s.Volume == volume && s.Complete)
                    .OrderByDescending(s => s.ReceivedAt)
                    .Select(s => new ScanStamp(s.Id, s.ReceivedAt))
                    .Take(500)
                    .ToListAsync(cancellationToken);
                if (history.Count == 0)
                {
                    continue;
                }

                var latest = history[0];
                var folders = new Dictionary<Guid, IReadOnlyList<StorageFolderEntry>>();
                foreach (var check in checks.Where(c => MatchesDrive(c, volume)))
                {
                    var parameters = CheckParameters.Parse(check.Definition.ParametersJson);
                    var days = int.TryParse(CheckCatalog.ParameterOrDefault(CheckType.FolderGrowth, parameters, "period_days"), out var d) ? d : 7;
                    if (StorageRules.Baseline(history.Skip(1), s => s.ReceivedAt, latest.ReceivedAt, TimeSpan.FromDays(days)) is not { } baseline)
                    {
                        continue;
                    }

                    var growth = StorageRules.Growth(
                        new StorageScanPoint(latest.ReceivedAt, await FoldersAsync(db, folders, latest.Id, cancellationToken)),
                        new StorageScanPoint(baseline.ReceivedAt, await FoldersAsync(db, folders, baseline.Id, cancellationToken)));
                    if (growth is null)
                    {
                        continue;
                    }

                    var detail = StorageRules.GrowthDetail(growth);
                    db.CheckResults.Add(new CheckResult
                    {
                        Time = now,
                        AgentTime = latest.ReceivedAt,
                        ClientId = endpoint.ClientId,
                        EndpointId = endpoint.Id,
                        CheckDefinitionId = check.Id,
                        Target = volume.Length <= 256 ? volume : volume[..256],
                        Value = StorageRules.ToGigabytes(growth.GrowthBytes),
                        Detail = detail.Length <= 1000 ? detail : detail[..1000]
                    });
                    any = true;
                }
            }

            if (any)
            {
                written.Add(endpoint.Id);
            }
        }

        return written;
    }

    /// <summary>True when the drive parameter of the check is * or names this volume (C:, c, C:\, /var, /var/).</summary>
    public static bool MatchesDrive(EffectiveCheck check, string volume)
    {
        var drive = CheckCatalog.ParameterOrDefault(CheckType.FolderGrowth, CheckParameters.Parse(check.Definition.ParametersJson), "drive");
        return drive.Trim() == "*" || string.Equals(NormalizeDrive(drive), NormalizeDrive(volume), StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeDrive(string drive)
    {
        var text = drive.Trim();
        if (text.Length is 1 or 2 or 3 && char.IsAsciiLetter(text[0]) && (text.Length == 1 || text[1] == ':') && (text.Length < 3 || text[2] is '\\' or '/'))
        {
            return char.ToUpperInvariant(text[0]) + ":";
        }

        return text.Length > 1 ? text.TrimEnd('/', '\\') : text;
    }

    private static async Task<IReadOnlyList<StorageFolderEntry>> FoldersAsync(FleetoDbContext db, Dictionary<Guid, IReadOnlyList<StorageFolderEntry>> cache,
        Guid scanId, CancellationToken cancellationToken)
    {
        if (!cache.TryGetValue(scanId, out var folders))
        {
            var json = await db.StorageScans.AsNoTracking().Where(s => s.Id == scanId).Select(s => s.FoldersJson).SingleAsync(cancellationToken);
            folders = StorageRules.ParseFolders(json);
            cache[scanId] = folders;
        }

        return folders;
    }

    private sealed record ScanStamp(Guid Id, DateTime ReceivedAt);
}
