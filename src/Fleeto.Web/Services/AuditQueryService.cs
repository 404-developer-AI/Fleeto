using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record AuditQuery(string? Action, string? Actor, string? TargetType, string? TargetId, Guid? ClientId, DateTime? FromUtc, DateTime? ToUtc,
    long? BeforeId = null, int Limit = 50);

public sealed record AuditPage(IReadOnlyList<AuditEntryView> Items, long? NextBeforeId);

/// <summary>The audit log for admins, with filters and keyset pagination on the append-only id.</summary>
public sealed class AuditQueryService
{
    private readonly IFleetoDbContextFactory _dbFactory;

    public AuditQueryService(IFleetoDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<AuditPage> QueryAsync(Caller caller, AuditQuery query, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        var limit = Math.Clamp(query.Limit, 1, 200);
        await using var db = _dbFactory.Create(caller.Scope);
        var entries = db.AuditEntries.AsNoTracking();

        if (ServiceSupport.Clean(query.Action) is { } action)
        {
            entries = entries.Where(a => EF.Functions.ILike(a.Action, ServiceSupport.LikePattern(action)));
        }

        if (ServiceSupport.Clean(query.Actor) is { } actor)
        {
            var pattern = ServiceSupport.LikePattern(actor);
            entries = entries.Where(a => EF.Functions.ILike(a.ActorName, pattern) || a.ActorId == actor);
        }

        if (ServiceSupport.Clean(query.TargetType) is { } targetType)
        {
            entries = entries.Where(a => a.TargetType == targetType);
        }

        if (ServiceSupport.Clean(query.TargetId) is { } targetId)
        {
            entries = entries.Where(a => a.TargetId == targetId);
        }

        if (query.ClientId is { } clientId)
        {
            entries = entries.Where(a => a.ClientId == clientId);
        }

        if (query.FromUtc is { } from)
        {
            entries = entries.Where(a => a.Time >= from);
        }

        if (query.ToUtc is { } to)
        {
            entries = entries.Where(a => a.Time < to);
        }

        if (query.BeforeId is { } beforeId)
        {
            entries = entries.Where(a => a.Id < beforeId);
        }

        var items = await entries.OrderByDescending(a => a.Id).Take(limit + 1)
            .Select(a => new AuditEntryView(a.Id, a.Time, a.ClientId, a.ActorType, a.ActorId, a.ActorName, a.Action, a.TargetType, a.TargetId,
                a.DetailsJson, a.IpAddress))
            .ToListAsync(cancellationToken);
        long? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(limit);
            next = items[^1].Id;
        }

        return new AuditPage(items, next);
    }

    /// <summary>Known action names for the filter, from the constants rather than a scan of the (large) audit table.</summary>
    public static IReadOnlyList<string> KnownActions { get; } = typeof(Fleeto.Core.Interfaces.AuditActions)
        .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
        .Where(f => f.IsLiteral && f.FieldType == typeof(string))
        .Select(f => (string)f.GetRawConstantValue()!)
        .OrderBy(a => a, StringComparer.Ordinal)
        .ToList();
}
