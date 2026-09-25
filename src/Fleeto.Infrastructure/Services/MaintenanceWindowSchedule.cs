using System.Text.Json;
using System.Text.Json.Serialization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Services;

/// <summary>
/// Stores the occurrences of policy maintenance windows ahead (<see cref="MaintenanceWindows.Horizon"/>): web replaces them when a
/// policy is saved, the workers refresh every policy every hour. Occurrences are computed in C# only
/// (<see cref="MaintenanceWindows.Occurrences"/>), so time zones and daylight saving never reach SQL.
/// </summary>
public static class MaintenanceWindowSchedule
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Serialize(IReadOnlyList<MaintenanceWindow> windows) => JsonSerializer.Serialize(windows, JsonOptions);

    /// <summary>The windows of a policy; an unreadable value counts as no windows, so it never suppresses alerts by accident.</summary>
    public static IReadOnlyList<MaintenanceWindow> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<MaintenanceWindow>>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>
    /// Replaces the stored occurrences of one policy with those that run now or start within the horizon. Deletes immediately and
    /// adds the new rows to <paramref name="db"/>; the caller saves, inside a transaction.
    /// </summary>
    public static async Task ReplaceAsync(FleetoDbContext db, Guid policyId, IReadOnlyList<MaintenanceWindow> windows, DateTime now,
        CancellationToken cancellationToken)
    {
        await db.MaintenanceWindowOccurrences.Where(o => o.PolicyId == policyId).ExecuteDeleteAsync(cancellationToken);
        foreach (var span in MaintenanceWindows.Occurrences(windows, now, now + MaintenanceWindows.Horizon))
        {
            db.MaintenanceWindowOccurrences.Add(new MaintenanceWindowOccurrence
            {
                PolicyId = policyId,
                WindowIndex = span.WindowIndex,
                StartsAt = span.StartsAt,
                EndsAt = span.EndsAt,
                AppliesTo = span.AppliesTo,
                Name = span.Name
            });
        }
    }

    /// <summary>Recomputes the occurrences of every policy (one transaction). Returns the number of stored occurrences.</summary>
    public static async Task<int> RefreshAllAsync(FleetoDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var policies = await db.Policies.IgnoreQueryFilters().AsNoTracking()
            .Select(p => new { p.Id, p.MaintenanceWindowsJson })
            .ToListAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var stored = 0;
        foreach (var policy in policies)
        {
            var windows = Parse(policy.MaintenanceWindowsJson);
            await ReplaceAsync(db, policy.Id, windows, now, cancellationToken);
            stored += db.ChangeTracker.Entries<MaintenanceWindowOccurrence>().Count(e => e.State == EntityState.Added && e.Entity.PolicyId == policy.Id);
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return stored;
    }

    /// <summary>
    /// The running occurrences per endpoint, with the policy name, for display. Each endpoint uses its effective policy
    /// (<see cref="EffectivePolicies"/>); the class of the occurrence is applied by <see cref="PeriodFor"/>.
    /// </summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<(MaintenanceWindowOccurrence Occurrence, string PolicyName)>>> RunningByEndpointAsync(
        FleetoDbContext db, IReadOnlyCollection<Guid> endpointIds, DateTime now, CancellationToken cancellationToken)
    {
        var result = new Dictionary<Guid, IReadOnlyList<(MaintenanceWindowOccurrence, string)>>();
        if (endpointIds.Count == 0)
        {
            return result;
        }

        var running = await db.MaintenanceWindowOccurrences.AsNoTracking()
            .Where(o => o.StartsAt <= now && o.EndsAt > now)
            .Join(db.Policies.IgnoreQueryFilters(), o => o.PolicyId, p => p.Id, (o, p) => new { Occurrence = o, p.Name, p.IsDefault })
            .ToListAsync(cancellationToken);
        if (running.Count == 0)
        {
            return result;
        }

        var ids = endpointIds.ToList();
        var policies = await EffectivePolicies.Query(db).IgnoreQueryFilters().Where(p => ids.Contains(p.EndpointId))
            .Select(p => new { p.EndpointId, p.PolicyId })
            .ToListAsync(cancellationToken);
        foreach (var endpoint in policies)
        {
            var forEndpoint = running.Where(r => r.Occurrence.PolicyId == endpoint.PolicyId).Select(r => (r.Occurrence, r.Name)).ToList();
            if (forEndpoint.Count > 0)
            {
                result[endpoint.EndpointId] = forEndpoint;
            }
        }

        return result;
    }

    /// <summary>The running window that applies to an endpoint of <paramref name="endpointClass"/>, as a period for the maintenance rule.</summary>
    public static MaintenancePeriod? PeriodFor(IReadOnlyDictionary<Guid, IReadOnlyList<(MaintenanceWindowOccurrence Occurrence, string PolicyName)>> running,
        Guid endpointId, EndpointClass endpointClass)
    {
        if (!running.TryGetValue(endpointId, out var occurrences))
        {
            return null;
        }

        var best = occurrences.Where(o => MaintenanceRules.WindowAppliesTo(o.Occurrence.AppliesTo, endpointClass))
            .OrderByDescending(o => o.Occurrence.EndsAt).FirstOrDefault();
        return best.Occurrence is null ? null : new MaintenancePeriod(best.Occurrence.StartsAt, best.Occurrence.EndsAt, best.PolicyName, best.Occurrence.Name);
    }
}
