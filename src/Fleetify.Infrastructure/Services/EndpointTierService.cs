using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Infrastructure.Services;

public sealed record TierChangeResult(bool Success, string? Problem, int Changed)
{
    public static TierChangeResult Fail(string problem) => new(false, problem, 0);
}

/// <summary>Who performs an operation, for authorization, audit and client scoping.</summary>
public sealed record Actor(Guid UserId, string Name, IClientScope Scope, string? IpAddress = null);

/// <summary>
/// Switches endpoints between agent-only and managed. License allocation is serialized: the pool check and the
/// tier change run in one transaction holding a transaction-scoped advisory lock, so concurrent requests can
/// never both take the last free license. A bulk switch is all or nothing.
/// </summary>
public sealed class EndpointTierService
{
    /// <summary>Advisory lock key for license allocation. Any constant works; it only has to be the same everywhere.</summary>
    internal const long LicenseAllocationLockKey = 0x466C656574696679; // "Fleetify"

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;

    public EndpointTierService(IFleetifyDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _time = time;
    }

    public async Task<TierChangeResult> SetTierAsync(IReadOnlyCollection<Guid> endpointIds, EndpointTier tier, Actor actor,
        CancellationToken cancellationToken = default)
    {
        if (endpointIds.Count == 0)
        {
            return new TierChangeResult(true, null, 0);
        }

        await using var db = _dbFactory.Create(actor.Scope);
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({LicenseAllocationLockKey})", cancellationToken);

            // Scoped query: endpoints outside the caller's clients are simply not found.
            var endpoints = await db.Endpoints.Where(e => endpointIds.Contains(e.Id)).ToListAsync(cancellationToken);
            if (endpoints.Count != endpointIds.Count)
            {
                return TierChangeResult.Fail("One or more endpoints no longer exist. Refresh the page and try again.");
            }

            var toChange = endpoints.Where(e => e.Tier != tier).ToList();
            if (toChange.Count == 0)
            {
                return new TierChangeResult(true, null, 0);
            }

            if (tier == EndpointTier.Managed)
            {
                // Count across every client with the system scope: the pool belongs to the instance.
                await using var systemDb = _dbFactory.CreateSystem();
                var status = await _licenses.GetStatusAsync(systemDb, cancellationToken);
                if (!status.AllowsManaged)
                {
                    return TierChangeResult.Fail(status.State == LicenseState.Missing
                        ? "No license is loaded. Load a license in Settings, Licensing before switching endpoints to managed."
                        : "The license has expired. Load a new license in Settings, Licensing before switching endpoints to managed.");
                }

                // Same transaction and connection for the count, so the advisory lock covers it.
                var inUse = await db.Endpoints.IgnoreQueryFilters().CountAsync(e => e.Tier == EndpointTier.Managed, cancellationToken);
                var available = status.Capacity - inUse;
                if (toChange.Count > available)
                {
                    var missing = toChange.Count - Math.Max(0, available);
                    return TierChangeResult.Fail(
                        $"Not enough licenses: {inUse} of {status.Capacity} are in use and this change needs {toChange.Count}. " +
                        $"Add {missing} license{(missing == 1 ? string.Empty : "s")} or switch other endpoints to agent-only first.");
                }
            }

            var now = _time.GetUtcNow().UtcDateTime;
            foreach (var endpoint in toChange)
            {
                var previous = endpoint.Tier;
                endpoint.Tier = tier;
                endpoint.UpdatedAt = now;
                db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Endpoint, ScopeId = endpoint.Id, CreatedAt = now });
                db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.EndpointTierChanged, "Endpoint", endpoint.Id.ToString(),
                    endpoint.ClientId, AuditActorType.User, actor.UserId.ToString(), actor.Name,
                    new { endpoint.Hostname, From = previous.ToString(), To = tier.ToString() }, actor.IpAddress), now));
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new TierChangeResult(true, null, toChange.Count);
        });

        if (result.Success)
        {
            foreach (var id in endpointIds)
            {
                await _bus.PublishAsync(NotificationChannels.EndpointStatus, id.ToString(), cancellationToken);
            }
        }

        return result;
    }
}
