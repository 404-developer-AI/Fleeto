using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>The latest scan of one volume of an endpoint.</summary>
public sealed record StorageVolumeSummary(Guid ScanId, string Volume, string Filesystem, long TotalBytes, long FreeBytes, DateTime ScannedAt,
    bool Complete, string? Error, StorageScanMethod Method, int DurationMs, long FileCount, long FolderCount);

/// <summary>
/// One folder of a scan in tree order, with how much it changed since earlier scans. A change is null when the earlier scan
/// does not exist or did not list the folder.
/// </summary>
public sealed record StorageFolderRow(string Path, string Name, string? ParentPath, int Depth, bool HasChildren, long SizeBytes, long FileCount,
    long FolderCount, long? ChangeSincePrevious, long? ChangeWeek, long? ChangeMonth);

public sealed record StorageVolumeDetail(StorageVolumeSummary Summary, IReadOnlyList<StorageFolderRow> Folders, IReadOnlyList<StorageFileEntry> Files,
    DateTime? PreviousAt, DateTime? WeekAt, DateTime? MonthAt);

/// <param name="Managed">Storage analysis is for managed endpoints only.</param>
/// <param name="AgentSupports">The agent is new enough to scan storage.</param>
/// <param name="CanScan">The caller may start a scan now.</param>
/// <param name="RequestedAt">A scan was asked for and no scan arrived since.</param>
public sealed record EndpointStorageView(bool Managed, bool AgentSupports, bool CanScan, IReadOnlyList<StorageVolumeSummary> Volumes,
    StorageVolumeDetail? Selected, DateTime? RequestedAt, int ScanIntervalHours);

public sealed record StorageHistoryPoint(DateTime At, long SizeBytes, bool Complete);

/// <summary>
/// Storage analysis on the endpoint page (0.6.0): the largest folders and files of each drive from the agent's scans, how
/// they changed, the history of one folder, and "Scan now". Scoped by client like every endpoint read; managed endpoints only.
/// </summary>
public sealed class StorageService
{
    /// <summary>A drive whose latest scan is older than this is left out: it was removed or no longer scanned.</summary>
    private static readonly TimeSpan VolumeFreshness = TimeSpan.FromDays(30);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly TimeProvider _time;

    public StorageService(IFleetoDbContextFactory dbFactory, LicenseService licenses, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _time = time;
    }

    /// <summary>The storage of an endpoint, with <paramref name="volume"/> (or the first drive) in detail. Null when the endpoint is not visible.</summary>
    public async Task<EndpointStorageView?> GetAsync(Caller caller, Guid endpointId, string? volume = null, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var managed = TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) == EndpointTier.Managed;
        var supports = StorageRules.AgentSupportsStorageScan(endpoint.AgentVersion);
        var interval = (await EffectivePolicies.LoadAsync(db, endpointId, cancellationToken))?.StorageScanIntervalHours ?? StorageRules.DefaultScanIntervalHours;
        if (!managed)
        {
            return new EndpointStorageView(false, supports, false, [], null, null, interval);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var fresh = now - VolumeFreshness;
        var latest = await db.StorageScans.AsNoTracking()
            .Where(s => s.EndpointId == endpointId && s.ReceivedAt > fresh)
            .GroupBy(s => s.Volume)
            .Select(g => g.OrderByDescending(s => s.ReceivedAt).Select(s => new StorageVolumeSummary(s.Id, s.Volume, s.Filesystem, s.TotalBytes,
                s.FreeBytes, s.ReceivedAt, s.Complete, s.Error, s.Method, s.DurationMs, s.FileCount, s.FolderCount)).First())
            .ToListAsync(cancellationToken);
        var volumes = latest.OrderBy(v => v.Volume, StringComparer.OrdinalIgnoreCase).ToList();

        var lastScan = volumes.Count == 0 ? (DateTime?)null : volumes.Max(v => v.ScannedAt);
        var requestedAt = await db.StorageScanRequests.AsNoTracking()
            .Where(r => r.EndpointId == endpointId && r.RequestedAt > now.AddHours(-1) && (lastScan == null || r.RequestedAt > lastScan))
            .OrderByDescending(r => r.RequestedAt)
            .Select(r => (DateTime?)r.RequestedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var summary = volumes.FirstOrDefault(v => string.Equals(v.Volume, volume, StringComparison.OrdinalIgnoreCase)) ?? volumes.FirstOrDefault();
        var detail = summary is null ? null : await DetailAsync(db, endpointId, summary, cancellationToken);
        return new EndpointStorageView(true, supports, caller.CanManage && supports, volumes, detail, requestedAt, interval);
    }

    private static async Task<StorageVolumeDetail> DetailAsync(FleetoDbContext db, Guid endpointId, StorageVolumeSummary summary,
        CancellationToken cancellationToken)
    {
        var scan = await db.StorageScans.AsNoTracking().Where(s => s.Id == summary.ScanId)
            .Select(s => new { s.FoldersJson, s.FilesJson }).SingleAsync(cancellationToken);
        var folders = StorageRules.ParseFolders(scan.FoldersJson);
        var files = StorageRules.ParseFiles(scan.FilesJson);

        // Earlier complete scans of the drive, newest first, to compare with.
        var older = await db.StorageScans.AsNoTracking()
            .Where(s => s.EndpointId == endpointId && s.Volume == summary.Volume && s.Complete && s.ReceivedAt < summary.ScannedAt)
            .OrderByDescending(s => s.ReceivedAt)
            .Select(s => new { s.Id, s.ReceivedAt })
            .Take(400)
            .ToListAsync(cancellationToken);
        var previous = older.FirstOrDefault();
        var week = older.FirstOrDefault(s => s.ReceivedAt <= summary.ScannedAt - TimeSpan.FromDays(7) + StorageRules.BaselineTolerance);
        var month = older.FirstOrDefault(s => s.ReceivedAt <= summary.ScannedAt - TimeSpan.FromDays(30) + StorageRules.BaselineTolerance);

        var compare = new Dictionary<Guid, Dictionary<string, long>>();
        foreach (var id in new[] { previous?.Id, week?.Id, month?.Id }.OfType<Guid>().Distinct())
        {
            var json = await db.StorageScans.AsNoTracking().Where(s => s.Id == id).Select(s => s.FoldersJson).SingleAsync(cancellationToken);
            var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (var folder in StorageRules.ParseFolders(json))
            {
                sizes.TryAdd(folder.Path, folder.SizeBytes);
            }

            compare[id] = sizes;
        }

        long? Change(Guid? scanId, StorageFolderEntry folder) =>
            scanId is { } id && compare[id].TryGetValue(folder.Path, out var before) ? folder.SizeBytes - before : null;

        var rows = TreeOrder(folders)
            .Select(t => new StorageFolderRow(t.Folder.Path, StorageRules.FolderName(t.Folder.Path), t.Parent, t.Depth, t.HasChildren,
                t.Folder.SizeBytes, t.Folder.FileCount, t.Folder.FolderCount,
                Change(previous?.Id, t.Folder), Change(week?.Id, t.Folder), Change(month?.Id, t.Folder)))
            .ToList();
        return new StorageVolumeDetail(summary, rows, files, previous?.ReceivedAt, week?.ReceivedAt, month?.ReceivedAt);
    }

    /// <summary>
    /// The folders of a scan in tree order: the root, then each folder followed by its children, largest first. A folder whose
    /// parent is not listed hangs below the root.
    /// </summary>
    internal static IReadOnlyList<(StorageFolderEntry Folder, string? Parent, int Depth, bool HasChildren)> TreeOrder(IReadOnlyList<StorageFolderEntry> folders)
    {
        if (folders.Count == 0)
        {
            return [];
        }

        var root = folders[0];
        var known = new HashSet<string>(folders.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var children = folders.Skip(1)
            .GroupBy(f => StorageRules.ParentPath(f.Path) is { } parent && known.Contains(parent) && !string.Equals(parent, f.Path, StringComparison.OrdinalIgnoreCase)
                ? parent
                : root.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(f => f.SizeBytes).ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList(),
                StringComparer.OrdinalIgnoreCase);

        var result = new List<(StorageFolderEntry, string?, int, bool)>(folders.Count);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(StorageFolderEntry Folder, string? Parent, int Depth)>();
        stack.Push((root, null, 0));
        while (stack.Count > 0)
        {
            var (folder, parent, depth) = stack.Pop();
            if (!visited.Add(folder.Path))
            {
                continue;
            }

            var own = children.TryGetValue(folder.Path, out var list) ? list : [];
            result.Add((folder, parent, depth, own.Count > 0));
            for (var i = own.Count - 1; i >= 0; i--)
            {
                stack.Push((own[i], folder.Path, depth + 1));
            }
        }

        return result;
    }

    /// <summary>The size of one folder in every stored scan of its drive, oldest first. Empty when the endpoint is not visible.</summary>
    public async Task<IReadOnlyList<StorageHistoryPoint>> FolderHistoryAsync(Caller caller, Guid endpointId, string volume, string path,
        CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        if (!await db.Endpoints.AnyAsync(e => e.Id == endpointId, cancellationToken))
        {
            return [];
        }

        // The endpoint passed the client scope above; the scans are read for that endpoint only.
        var rows = await db.Database.SqlQuery<StorageHistoryRow>($"""
            SELECT s."ReceivedAt" AS "At", (f.value ->> 's')::bigint AS "SizeBytes", s."Complete" AS "Complete"
            FROM "StorageScans" s, jsonb_array_elements(s."FoldersJson") AS f(value)
            WHERE s."EndpointId" = {endpointId} AND s."Volume" = {volume} AND f.value ->> 'p' = {path}
            ORDER BY s."ReceivedAt"
            """).ToListAsync(cancellationToken);
        return rows.Select(r => new StorageHistoryPoint(DateTime.SpecifyKind(r.At, DateTimeKind.Utc), r.SizeBytes, r.Complete)).ToList();
    }

    /// <summary>
    /// Asks the agent to scan its drives now. Admins and technicians, managed endpoints with an agent that scans storage, once per
    /// <see cref="StorageRules.RequestGap"/> per endpoint; audited. Returns whether the endpoint is online right now.
    /// </summary>
    public async Task<ServiceResult<bool>> RequestScanAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<bool>.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == endpointId, cancellationToken);
        if (endpoint is null)
        {
            return ServiceResult<bool>.NotFound("endpoint");
        }

        if (TierRules.EffectiveTier(endpoint.Tier, await _licenses.GetStatusAsync(db, cancellationToken)) != EndpointTier.Managed)
        {
            return ServiceResult<bool>.Fail("Storage analysis needs a managed endpoint. Switch the endpoint to managed first.");
        }

        if (!StorageRules.AgentSupportsStorageScan(endpoint.AgentVersion))
        {
            return ServiceResult<bool>.Fail(
                $"The agent on this endpoint cannot scan storage yet. It needs version {StorageRules.MinimumAgentVersion} or newer; it updates on its own.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var gap = now - StorageRules.RequestGap;
        if (await db.StorageScanRequests.AnyAsync(r => r.EndpointId == endpointId && r.RequestedAt > gap, cancellationToken))
        {
            return ServiceResult<bool>.Fail("A scan of this endpoint was asked for less than 15 minutes ago. Wait for its result, then try again.");
        }

        var request = new StorageScanRequest
        {
            Id = Guid.CreateVersion7(),
            ClientId = endpoint.ClientId,
            EndpointId = endpointId,
            Reason = StorageScanReason.Technician,
            RequestedByUserId = caller.UserId,
            RequestedByName = caller.Name.Length <= 200 ? caller.Name : caller.Name[..200],
            RequestedAt = now,
            ExpiresAt = now + StorageRules.TechnicianRequestLifetime
        };
        db.StorageScanRequests.Add(request);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.StorageScanRequested, "Endpoint", endpointId.ToString(), endpoint.ClientId,
            new { endpoint.Hostname, RequestId = request.Id }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(endpoint.IsOnline);
    }

    private sealed record StorageHistoryRow(DateTime At, long SizeBytes, bool Complete);
}
