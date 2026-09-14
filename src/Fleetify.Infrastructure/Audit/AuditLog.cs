using System.Text.Json;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;

namespace Fleetify.Infrastructure.Audit;

/// <summary>Writes append-only audit entries in their own context, so an audit write never rides on a caller's unit of work.</summary>
public sealed class AuditLog : IAuditLog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public AuditLog(IFleetifyDbContextFactory dbFactory, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task WriteAsync(AuditRecord record, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        db.AuditEntries.Add(ToEntry(record, _time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>Builds an entry for callers that must write the audit row in their own transaction.</summary>
    public static AuditEntry ToEntry(AuditRecord record, DateTime now) => new()
    {
        Time = now,
        ClientId = record.ClientId,
        ActorType = record.ActorType,
        ActorId = Truncate(record.ActorId, 100),
        ActorName = Truncate(record.ActorName, 200),
        Action = record.Action,
        TargetType = record.TargetType,
        TargetId = Truncate(record.TargetId, 100),
        DetailsJson = record.Details is null ? "{}" : JsonSerializer.Serialize(record.Details, JsonOptions),
        IpAddress = record.IpAddress is null ? null : Truncate(record.IpAddress, 64)
    };

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
