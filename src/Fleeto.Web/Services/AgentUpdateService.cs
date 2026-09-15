using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record RingOverview(UpdateRing Ring, DateTime AvailableAt, bool Available, int Endpoints);

public sealed record UpdateProblem(Guid EndpointId, string Hostname, string ClientCode, AgentComponent Component, string Version, ComponentUpdateState State,
    string Detail, DateTime? At);

/// <param name="Release">Null when no agent release was ever loaded by the gateway.</param>
public sealed record AgentUpdateOverview(AgentRelease? Release, IReadOnlyList<RingOverview> Rings, int AgentsCurrent, int AgentsOlder, int AgentsOther,
    int WatchdogsInstalled, int WatchdogsOnline, IReadOnlyList<UpdateProblem> Problems);

/// <summary>
/// Agent updates for admins (0.2.1): the release this instance offers, when each update ring gets it, how far the endpoints are, updates
/// that failed or were rolled back, and the controls to pause a release or release it to every ring at once. The gateway follows the
/// controls within a minute; the endpoints verify every release against the Steaan release keys themselves.
/// </summary>
public sealed class AgentUpdateService
{
    public const int MaxProblems = 50;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentUpdateService> _logger;

    public AgentUpdateService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time, ILogger<AgentUpdateService> logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public async Task<AgentUpdateOverview> GetAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        var release = await db.AgentReleases.AsNoTracking().SingleOrDefaultAsync(r => r.IsCurrent, cancellationToken);

        var defaultRing = await db.Policies.AsNoTracking().Where(p => p.IsDefault).Select(p => (UpdateRing?)p.UpdateRing).FirstOrDefaultAsync(cancellationToken)
                          ?? UpdateRing.Standard;
        var ringCounts = await db.Endpoints.AsNoTracking()
            .Where(e => e.Source == EndpointSource.Agent)
            .GroupBy(e => db.SitePolicies.Where(l => l.SiteId == e.SiteId).Select(l => (UpdateRing?)l.Policy!.UpdateRing).FirstOrDefault())
            .Select(g => new { Ring = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var rings = Enum.GetValues<UpdateRing>().Select(ring =>
        {
            var count = ringCounts.Where(c => (c.Ring ?? defaultRing) == ring).Sum(c => c.Count);
            var availableAt = release is null ? DateTime.MaxValue : UpdateRings.AvailableAt(release, ring);
            return new RingOverview(ring, availableAt, release is not null && UpdateRings.IsAllowed(release, ring, now), count);
        }).ToList();

        var versions = await db.Endpoints.AsNoTracking().Where(e => e.Source == EndpointSource.Agent)
            .GroupBy(e => e.AgentVersion).Select(g => new { Version = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        int current = 0, older = 0, other = 0;
        foreach (var version in versions)
        {
            if (release is not null && version.Version == release.Version)
            {
                current += version.Count;
            }
            else if (release is not null && SemanticVersion.IsOlder(version.Version, release.Version))
            {
                older += version.Count;
            }
            else
            {
                other += version.Count;
            }
        }

        var watchdogs = await db.Endpoints.AsNoTracking().Where(e => e.WatchdogVersion != string.Empty)
            .GroupBy(_ => 1).Select(g => new { Installed = g.Count(), Online = g.Count(e => e.WatchdogOnline) })
            .FirstOrDefaultAsync(cancellationToken);

        IReadOnlyList<UpdateProblem> problems = [];
        if (release is not null)
        {
            problems = await db.EndpointComponentStates.AsNoTracking()
                .Where(s => s.UpdateVersion == release.Version && (s.UpdateState == ComponentUpdateState.Failed || s.UpdateState == ComponentUpdateState.RolledBack))
                .OrderByDescending(s => s.UpdateAt)
                .Take(MaxProblems)
                .Join(db.Endpoints, s => s.EndpointId, e => e.Id, (s, e) => new UpdateProblem(e.Id, e.Hostname,
                    db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Code).FirstOrDefault() ?? string.Empty, s.Component, s.UpdateVersion,
                    s.UpdateState!.Value, s.UpdateDetail, s.UpdateAt))
                .ToListAsync(cancellationToken);
        }

        return new AgentUpdateOverview(release, rings, current, older, other, watchdogs?.Installed ?? 0, watchdogs?.Online ?? 0, problems);
    }

    /// <summary>Stops endpoints from starting to install the current release. Endpoints that already installed it keep it.</summary>
    public Task<ServiceResult> PauseAsync(Caller caller, string version, CancellationToken cancellationToken = default) =>
        ChangeAsync(caller, version, AuditActions.AgentReleasePaused, (release, now) =>
        {
            if (release.PausedAt is not null)
            {
                return "This release is already paused.";
            }

            release.PausedAt = now;
            release.PausedByUserId = caller.UserId;
            release.PausedByName = Truncate(caller.Name);
            return null;
        }, cancellationToken);

    public Task<ServiceResult> ResumeAsync(Caller caller, string version, CancellationToken cancellationToken = default) =>
        ChangeAsync(caller, version, AuditActions.AgentReleaseResumed, (release, _) =>
        {
            if (release.PausedAt is null)
            {
                return "This release is not paused.";
            }

            release.PausedAt = null;
            release.PausedByUserId = null;
            release.PausedByName = null;
            return null;
        }, cancellationToken);

    /// <summary>Lets every ring install the current release now, without waiting for the ring delays.</summary>
    public Task<ServiceResult> ReleaseToAllAsync(Caller caller, string version, CancellationToken cancellationToken = default) =>
        ChangeAsync(caller, version, AuditActions.AgentReleaseReleasedToAll, (release, now) =>
        {
            if (release.ReleasedToAllAt is not null)
            {
                return "This release is already released to every ring.";
            }

            if (release.PausedAt is not null)
            {
                return "This release is paused. Resume it first, then release it to every ring.";
            }

            release.ReleasedToAllAt = now;
            release.ReleasedToAllByUserId = caller.UserId;
            release.ReleasedToAllByName = Truncate(caller.Name);
            return null;
        }, cancellationToken);

    private async Task<ServiceResult> ChangeAsync(Caller caller, string version, string action, Func<AgentRelease, DateTime, string?> change,
        CancellationToken cancellationToken)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var release = await db.AgentReleases.SingleOrDefaultAsync(r => r.Version == version, cancellationToken);
        if (release is null || !release.IsCurrent)
        {
            return ServiceResult.Fail("This release is no longer the current release of the instance. Refresh the page.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        if (change(release, now) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(action, "AgentRelease", release.Version, null, new { release.Version }), now));
        await db.SaveChangesAsync(cancellationToken);
        try
        {
            await _bus.PublishAsync(NotificationChannels.AgentReleases, release.Version, cancellationToken);
        }
        catch (Exception ex)
        {
            // The gateway also reads the controls every minute.
            _logger.LogWarning(ex, "Could not notify the gateway of the change to agent release {Version}", release.Version);
        }

        return ServiceResult.Ok();
    }

    private static string Truncate(string value) => value.Length <= 200 ? value : value[..200];
}
