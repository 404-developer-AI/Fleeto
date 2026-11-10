using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Decides when an Action1 endpoint may follow a Fleeto endpoint into the organization of another client. The Action1 id
/// a Fleeto agent reports is only the word of that agent: a hostile or cloned agent can report the id of a machine of
/// another client. So the id alone never pulls an Action1 endpoint across clients. A move goes ahead only when the
/// newest reporter has the id to itself and the client that Action1 holds it under shows the record the machine left
/// behind: an endpoint of that client that reported the same id, is offline and was last seen before the new endpoint
/// enrolled (the machine was enrolled again under another client). Every other case waits for an admin, with the cause
/// in Settings, Integrations. Used by the patch sync when it queues a move and again by the follow pass before it asks
/// Action1, so both judge the same way.
/// </summary>
internal static class Action1Claims
{
    /// <summary>
    /// How long the record left behind may still have been seen after the new endpoint enrolled: the installer of the new
    /// enrollment stops the old agent around the same moment.
    /// </summary>
    public static readonly TimeSpan EnrollmentOverlap = TimeSpan.FromMinutes(15);

    /// <summary>One Fleeto endpoint whose agent reports an Action1 id.</summary>
    public sealed record Claimant(Guid EndpointId, Guid ClientId, string Hostname, EndpointTier Tier, DateTime ReceivedAt, bool IsOnline,
        DateTime? LastSeenAt, DateTime EnrolledAt);

    /// <summary>
    /// A wanted move of an Action1 endpoint to the organization of <see cref="Owner"/>'s client; <see cref="Held"/> holds the
    /// cause and next step when it must wait for an admin instead.
    /// </summary>
    public sealed record Move(Claimant Owner, string TargetTenant, string? Held);

    /// <summary>The endpoints that report each of the Action1 ids, by the id in lower case. One query, no calls to Action1.</summary>
    public static async Task<ILookup<string, Claimant>> ReadAsync(FleetoDbContext db, IReadOnlyCollection<string> keys,
        CancellationToken cancellationToken)
    {
        var rows = await (from i in db.InventorySnapshots
                          join e in db.Endpoints on i.EndpointId equals e.Id
                          where keys.Contains(i.Action1AgentId.ToLower())
                          select new
                          {
                              Key = i.Action1AgentId.ToLower(),
                              e.Id, e.ClientId, e.Hostname, e.Tier, i.ReceivedAt, e.IsOnline, e.LastSeenAt, e.EnrolledAt
                          })
            .ToListAsync(cancellationToken);
        return rows.ToLookup(r => r.Key,
            r => new Claimant(r.Id, r.ClientId, r.Hostname, r.Tier, r.ReceivedAt, r.IsOnline, r.LastSeenAt, r.EnrolledAt));
    }

    /// <summary>
    /// Whether the Action1 endpoint that Action1 holds in <paramref name="sourceTenant"/> should move, and whether it may
    /// do so by itself. Null when no move is wanted: the newest reporter is not managed, its client is not mapped, or it
    /// is already in the organization of that client.
    /// </summary>
    public static Move? Decide(IEnumerable<Claimant> claimants, string sourceTenant, IReadOnlyDictionary<Guid, string> tenantOfClient)
    {
        var all = claimants.ToList();
        if (all.Count == 0)
        {
            return null;
        }

        var owner = all.OrderByDescending(c => c.ReceivedAt).ThenByDescending(c => c.EnrolledAt).First();
        if (owner.Tier != EndpointTier.Managed || !tenantOfClient.TryGetValue(owner.ClientId, out var target) || target == sourceTenant)
        {
            return null;
        }

        var others = all.Where(c => c.EndpointId != owner.EndpointId).ToList();
        var live = others.FirstOrDefault(o => o.ClientId != owner.ClientId && !LeftBehind(o, owner));
        if (live is not null)
        {
            return new Move(owner, target,
                $"Endpoint {live.Hostname} of another client reports the same Action1 agent and is still in use, so Fleeto cannot tell " +
                $"which machine runs it and does not move it to the organization of {owner.Hostname}. Check which machine runs this " +
                "Action1 agent, delete the endpoint that does not, and dismiss this here; Fleeto checks again every 6 hours.");
        }

        var sourceClients = tenantOfClient.Where(t => t.Value == sourceTenant).Select(t => t.Key).ToHashSet();
        if (!others.Any(o => sourceClients.Contains(o.ClientId)))
        {
            return new Move(owner, target,
                "Action1 holds this endpoint in the organization of another client, and no endpoint of that client reported its " +
                $"Action1 agent before {owner.Hostname} did, so Fleeto does not move it on the word of one agent. Move it in the " +
                "Action1 console if it belongs to this client, and dismiss this here.");
        }

        return new Move(owner, target, null);
    }

    /// <summary>
    /// True when <paramref name="other"/> is the record a machine left behind when it was enrolled again as
    /// <paramref name="owner"/>: offline, and not seen since shortly after the new enrollment.
    /// </summary>
    private static bool LeftBehind(Claimant other, Claimant owner) =>
        !other.IsOnline && (other.LastSeenAt ?? DateTime.MinValue) <= owner.EnrolledAt + EnrollmentOverlap;
}
