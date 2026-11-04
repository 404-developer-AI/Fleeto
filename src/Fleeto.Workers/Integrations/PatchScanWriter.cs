using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Turns the patch scans of the Fleeto agent into patch state (0.9.0, <c>PATCHING.md</c> §7): per endpoint the newest scan becomes its
/// <see cref="EndpointPatchState"/> of engine Fleeto and its <see cref="EndpointMissingUpdate"/> rows, on Linux with the severity, CVEs
/// and advisory of the open vulnerability findings each update fixes. The "Missing updates" checks get their results, "Scan now"
/// requests are completed, and the patch state alert opens when the scans of an online endpoint are older than
/// <see cref="PatchScanRules.StaleAfter"/>.
/// <para>
/// A scan that read nothing because of a problem changes neither the missing updates nor the time of the last scan, so an endpoint
/// whose scans keep failing never looks compliant and turns stale. Woken by <c>fleeto_patch_scans</c>; every minute it also catches up
/// on scans a lost notification left unprocessed, and every 15 minutes it looks at the state of every endpoint scanned by Fleeto.
/// </para>
/// </summary>
public sealed class PatchScanWriter : WorkerLoop
{
    internal const int BatchSize = 200;

    public const string GoneReason = "Fleeto no longer patches this endpoint.";

    public const string FreshReason = "The Fleeto agent scanned this endpoint again.";

    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(15);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly AlertNotificationService _notifier;
    private readonly LicenseService _licenses;
    private IDisposable? _subscription;
    private DateTime _sweptAt = DateTime.MinValue;

    // Endpoints whose configuration this process already asked to sign again because their engine became Fleeto; the signer skips a
    // configuration that did not change, so asking again after a restart costs nothing.
    private readonly HashSet<Guid> _nudged = [];

    public PatchScanWriter(IFleetoDbContextFactory dbFactory, INotificationBus bus, AlertNotificationService notifier, LicenseService licenses,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<PatchScanWriter> logger)
        : base("patch-scans", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _notifier = notifier;
        _licenses = licenses;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.PatchScans, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var more = await ProcessAsync(cancellationToken) >= BatchSize;
        var now = Time.GetUtcNow().UtcDateTime;
        if (now - _sweptAt >= SweepInterval)
        {
            await SweepAsync(cancellationToken);
            _sweptAt = now;
        }

        return more;
    }

    /// <summary>Processes the scans that wait, oldest first. Returns the number looked at.</summary>
    public async Task<int> ProcessAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var scans = await db.PatchScans
            .Where(s => s.ProcessedAt == null)
            .OrderBy(s => s.ReceivedAt)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (scans.Count == 0)
        {
            return 0;
        }

        var now = Time.GetUtcNow().UtcDateTime;
        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var transitions = new List<AlertTransition>();
        var changed = new List<Guid>();
        var gone = new List<Guid>();
        foreach (var group in scans.GroupBy(s => s.EndpointId))
        {
            // Only the newest scan of an endpoint counts; the older ones of the batch are superseded.
            var newest = group.MaxBy(s => s.ReceivedAt)!;
            switch (await WriteAsync(db, newest, license, transitions, now, cancellationToken))
            {
                case WriteOutcome.Written:
                    changed.Add(group.Key);
                    break;
                case WriteOutcome.Forgotten:
                    gone.Add(group.Key);
                    break;
            }

            foreach (var scan in group)
            {
                scan.ProcessedAt = now;
            }

            // A scan answers every request that was delivered before it arrived.
            await db.PatchScanRequests
                .Where(r => r.EndpointId == group.Key && r.CompletedAt == null && r.DeliveredAt != null && r.DeliveredAt <= newest.ReceivedAt)
                .ExecuteUpdateAsync(u => u.SetProperty(r => r.CompletedAt, now), cancellationToken);
        }

        transitions.AddRange(await MissingUpdateChecks.ForgetAsync(db, gone, now, cancellationToken));
        await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId), cancellationToken);
        await WriteCheckResultsAsync(db, changed, now, cancellationToken);
        foreach (var endpointId in changed.Concat(gone))
        {
            await _bus.PublishAsync(NotificationChannels.PatchRequests, endpointId.ToString(), cancellationToken);
        }

        return scans.Count;
    }

    /// <summary>
    /// Writes the patch state of one endpoint from its newest scan. An endpoint whose engine is not Fleeto (it became agent-only, or
    /// got an Action1 agent) gets nothing, and a Fleeto state it had is forgotten.
    /// </summary>
    private async Task<WriteOutcome> WriteAsync(FleetoDbContext db, PatchScan scan, LicenseStatus license, List<AlertTransition> transitions, DateTime now,
        CancellationToken cancellationToken)
    {
        var endpoint = await db.Endpoints.AsNoTracking().SingleOrDefaultAsync(e => e.Id == scan.EndpointId, cancellationToken);
        if (endpoint is null)
        {
            return WriteOutcome.Nothing;
        }

        var inventory = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(i => i.EndpointId == endpoint.Id, cancellationToken);
        var state = await db.EndpointPatchStates.SingleOrDefaultAsync(s => s.EndpointId == endpoint.Id, cancellationToken);
        var engine = PatchEngineRules.EngineFor(endpoint, license, inventory, state);
        if (engine.Engine != EndpointPatchEngine.Fleeto)
        {
            if (state is { Engine: PatchEngine.Fleeto })
            {
                await PatchStateWriter.ForgetAsync(db, state, transitions, now, GoneReason, cancellationToken);
                // The configuration stops asking for scans.
                db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Endpoint, ScopeId = endpoint.Id, CreatedAt = now });
                return WriteOutcome.Forgotten;
            }

            return WriteOutcome.Nothing;
        }

        if (state is null)
        {
            state = new EndpointPatchState { EndpointId = endpoint.Id, ClientId = endpoint.ClientId, Engine = PatchEngine.Fleeto };
            db.EndpointPatchStates.Add(state);
        }

        state.Engine = PatchEngine.Fleeto;
        state.ExternalEndpointId = string.Empty;
        state.ExternalTenantId = string.Empty;
        state.Coverage = PatchCoverage.Active;
        state.ScanTool = scan.Tool;
        state.ScanProblem = scan.Problem;
        state.RebootRequired = scan.RestartRequired;
        state.ProductAgentVersion = scan.AgentVersion;
        state.UpdatedAt = now;

        var updates = PatchScanRules.Parse(scan.UpdatesJson);
        var readNothing = scan.Problem is not null && updates.Count == 0;
        if (readNothing)
        {
            state.ScanFailingSince ??= scan.ReceivedAt;
        }
        else
        {
            state.ScanFailingSince = null;
            // Only a scan that read the updates moves the time of the last scan; one that failed leaves the state to turn stale.
            state.ProductLastSeenAt = scan.ReceivedAt;
            var (critical, other) = await ReplaceUpdatesAsync(db, endpoint, scan.Tool, updates, now, cancellationToken);
            state.MissingCritical = critical;
            state.MissingOther = other;
        }

        await AlertAsync(db, endpoint, state, transitions, now, cancellationToken);
        return WriteOutcome.Written;
    }

    private enum WriteOutcome
    {
        Nothing,
        Written,
        Forgotten
    }

    /// <summary>
    /// Replaces the missing updates of the endpoint with those of the scan. A row the scan still reports keeps its id and
    /// <see cref="EndpointMissingUpdate.FirstSeenAt"/>, so the deferral of a patch policy and a chosen update survive a scan. Returns
    /// the counts for the patch state: critical, and everything else; a removal is not a missing update.
    /// </summary>
    private static async Task<(int Critical, int Other)> ReplaceUpdatesAsync(FleetoDbContext db, Endpoint endpoint, string tool,
        IReadOnlyList<PatchScanUpdate> updates, DateTime now, CancellationToken cancellationToken)
    {
        var existing = await db.EndpointMissingUpdates.Where(u => u.EndpointId == endpoint.Id).ToListAsync(cancellationToken);
        var byId = existing.GroupBy(u => u.ExternalUpdateId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var findings = await FindingsAsync(db, endpoint.Id, cancellationToken);
        var kept = new HashSet<EndpointMissingUpdate>();
        var critical = 0;
        var other = 0;
        foreach (var update in updates.DistinctBy(u => u.Id, StringComparer.Ordinal))
        {
            var fix = PatchScanRules.FixOf(update, tool, findings);
            if (!byId.TryGetValue(update.Id, out var row))
            {
                row = new EndpointMissingUpdate
                {
                    Id = Guid.NewGuid(),
                    EndpointId = endpoint.Id,
                    ClientId = endpoint.ClientId,
                    ExternalUpdateId = update.Id,
                    FirstSeenAt = now
                };
                db.EndpointMissingUpdates.Add(row);
            }

            row.FirstSeenAt ??= now;
            row.Name = update.Name;
            row.Vendor = string.Empty;
            row.Version = update.Version;
            row.InstalledVersion = update.InstalledVersion;
            row.KbNumber = string.Empty;
            row.Severity = fix?.Severity ?? PatchSeverity.Unspecified;
            row.ReleaseDate = fix?.ReleaseDate;
            row.UpdateType = update.Security || fix is { Cves.Count: > 0 } ? PatchScanRules.SecurityType : PatchScanRules.RegularType;
            row.ApprovalStatus = string.Empty;
            row.Cves = [.. fix?.Cves ?? []];
            row.Advisory = fix?.Advisory ?? update.Advisory;
            row.Origin = update.Origin;
            row.Held = update.Held;
            row.Removal = update.Removal;
            row.UpdatedAt = now;
            kept.Add(row);
            if (update.Removal)
            {
                continue;
            }

            if (row.Severity == PatchSeverity.Critical)
            {
                critical++;
            }
            else
            {
                other++;
            }
        }

        db.EndpointMissingUpdates.RemoveRange(existing.Where(u => !kept.Contains(u)));
        return (critical, other);
    }

    /// <summary>The open findings of the endpoint with a fix, with the published date of their advisory where the feed has one.</summary>
    private static async Task<IReadOnlyList<PatchFinding>> FindingsAsync(FleetoDbContext db, Guid endpointId, CancellationToken cancellationToken)
    {
        var findings = await db.EndpointVulnerabilities.AsNoTracking()
            .Where(v => v.EndpointId == endpointId && v.EndedAt == null && v.FixedVersion != null && v.State == VulnerabilityFixState.Fixable)
            .Select(v => new { v.Package, v.Binaries, v.FixedVersion, v.Cve, v.Severity, v.Advisory })
            .ToListAsync(cancellationToken);
        if (findings.Count == 0)
        {
            return [];
        }

        var advisories = findings.Where(f => f.Advisory != null).Select(f => f.Advisory!).Distinct().ToList();
        var published = advisories.Count == 0
            ? new Dictionary<string, DateTime>()
            : await db.VulnerabilityFeedRecords.AsNoTracking()
                .Where(r => r.Advisory != null && advisories.Contains(r.Advisory) && r.PublishedAt != null)
                .GroupBy(r => r.Advisory!)
                .Select(g => new { Advisory = g.Key, PublishedAt = g.Min(r => r.PublishedAt!.Value) })
                .ToDictionaryAsync(r => r.Advisory, r => r.PublishedAt, cancellationToken);
        return [.. findings.Select(f => new PatchFinding(f.Package, f.Binaries, f.FixedVersion, f.Cve, f.Severity, f.Advisory,
            f.Advisory != null && published.TryGetValue(f.Advisory, out var at) ? at : null))];
    }

    /// <summary>
    /// Opens or resolves the patch state alert of a Fleeto endpoint: its last scan that read the updates is older than
    /// <see cref="PatchScanRules.StaleAfter"/> while the endpoint is online. Missing updates are not an alert; they are the state.
    /// </summary>
    private static async Task AlertAsync(FleetoDbContext db, Endpoint endpoint, EndpointPatchState state, List<AlertTransition> transitions, DateTime now,
        CancellationToken cancellationToken)
    {
        // Counted from the last scan that read the updates, or, while none ever did, from the first that failed.
        var stale = endpoint.IsOnline && (state.ProductLastSeenAt ?? state.ScanFailingSince) is { } since0 && now - since0 > PatchScanRules.StaleAfter;
        var open = await db.Alerts.FirstOrDefaultAsync(
            a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState && a.State != AlertState.Resolved, cancellationToken);
        if (!stale)
        {
            if (open is not null)
            {
                open.State = AlertState.Resolved;
                open.ResolvedAt = now;
                open.ResolvedReason = FreshReason;
                open.UpdatedAt = now;
                transitions.Add(new AlertTransition(open.Id, NotificationEvent.Resolved));
            }

            return;
        }

        if (open is not null || (await PatchStateWriter.InMaintenanceAsync(db, endpoint.ClientId, now, cancellationToken)).Contains(endpoint.Id))
        {
            // An endpoint in maintenance opens no alerts (CLAUDE.md, Maintenance mode); the state is stored either way.
            return;
        }

        var since = state.ProductLastSeenAt is { } last ? $"since {last:yyyy-MM-dd}" : "yet";
        var cause = state.ScanProblem is { } problem ? $" The last scan said: {problem}" : " Check that the Fleeto agent runs and can reach its package repositories.";
        var alert = new Alert
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            Kind = AlertKind.PatchState,
            Target = "stale",
            Severity = AlertSeverity.Warning,
            State = AlertState.Open,
            Title = Cut($"The Fleeto agent has not read the missing updates of {endpoint.Hostname} {since}.", 500),
            Detail = Cut($"Its patch state is too old to rely on.{cause}", 2000),
            OpenedAt = now,
            UpdatedAt = now
        };
        db.Alerts.Add(alert);
        transitions.Add(new AlertTransition(alert.Id, NotificationEvent.Opened));
    }

    /// <summary>
    /// Looks at every patch state of engine Fleeto: one whose endpoint no longer has the Fleeto engine (agent-only, an Action1 agent,
    /// another operating system) is forgotten with its missing updates and checks, and one that turned stale opens its alert. Also
    /// ages out old scans and requests. Returns the number of states forgotten.
    /// </summary>
    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var states = await db.EndpointPatchStates.Where(s => s.Engine == PatchEngine.Fleeto).ToListAsync(cancellationToken);
        var transitions = new List<AlertTransition>();
        var gone = new List<Guid>();
        foreach (var chunk in states.Chunk(500))
        {
            var ids = chunk.Select(s => s.EndpointId).ToList();
            var endpoints = await db.Endpoints.AsNoTracking().Where(e => ids.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
            var inventories = await db.InventorySnapshots.AsNoTracking().Where(i => ids.Contains(i.EndpointId)).ToDictionaryAsync(i => i.EndpointId, cancellationToken);
            foreach (var state in chunk)
            {
                if (!endpoints.TryGetValue(state.EndpointId, out var endpoint))
                {
                    continue;
                }

                var engine = PatchEngineRules.EngineFor(endpoint, license, inventories.GetValueOrDefault(endpoint.Id), state);
                if (engine.Engine != EndpointPatchEngine.Fleeto)
                {
                    await PatchStateWriter.ForgetAsync(db, state, transitions, now, GoneReason, cancellationToken);
                    gone.Add(endpoint.Id);
                    // The configuration stops asking for scans.
                    db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Endpoint, ScopeId = endpoint.Id, CreatedAt = now });
                    _nudged.Remove(endpoint.Id);
                }
                else
                {
                    await AlertAsync(db, endpoint, state, transitions, now, cancellationToken);
                }
            }
        }

        // An endpoint whose engine became Fleeto (its Action1 agent left, its agent was updated, the server was updated) and that has no
        // Fleeto patch state yet gets its configuration signed again, so it carries the scan interval.
        var candidates = await (from e in db.Endpoints.AsNoTracking()
                                join engine in PatchEngines.Query(db, license.AllowsManaged) on e.Id equals engine.EndpointId
                                where engine.Engine == EndpointPatchEngine.Fleeto && e.OsPlatform == "linux" &&
                                      !db.EndpointPatchStates.Any(s => s.EndpointId == e.Id && s.Engine == PatchEngine.Fleeto)
                                select new { e.Id, e.AgentVersion })
            .ToListAsync(cancellationToken);
        foreach (var candidate in candidates.Where(c => PatchScanRules.AgentScans(c.AgentVersion) && _nudged.Add(c.Id)))
        {
            db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Endpoint, ScopeId = candidate.Id, CreatedAt = now });
        }

        transitions.AddRange(await MissingUpdateChecks.ForgetAsync(db, gone, now, cancellationToken));
        await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId), cancellationToken);
        // The newest scan of every endpoint stays; older processed ones go after the retention, requests after a day.
        var cutoff = now - PatchScanRules.Retention;
        await db.PatchScans
            .Where(s => s.ProcessedAt != null && s.ReceivedAt < cutoff &&
                        db.PatchScans.Any(n => n.EndpointId == s.EndpointId && n.ReceivedAt > s.ReceivedAt))
            .ExecuteDeleteAsync(cancellationToken);
        var requestCutoff = now.AddDays(-1);
        await db.PatchScanRequests.Where(r => r.RequestedAt < requestCutoff).ExecuteDeleteAsync(cancellationToken);
        return gone.Count;
    }

    /// <summary>Gives the missing updates checks their results from the patch state just stored, then wakes check evaluation.</summary>
    private async Task WriteCheckResultsAsync(FleetoDbContext db, IReadOnlyCollection<Guid> endpointIds, DateTime now, CancellationToken cancellationToken)
    {
        var written = await MissingUpdateChecks.WriteResultsAsync(db, endpointIds, now, cancellationToken);
        if (written.Count == 0)
        {
            return;
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var endpointId in written)
        {
            await _bus.PublishAsync(NotificationChannels.CheckResults, endpointId.ToString(), cancellationToken);
        }
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
