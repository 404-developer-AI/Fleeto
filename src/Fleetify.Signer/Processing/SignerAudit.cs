using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace Fleetify.Signer.Processing;

/// <summary>
/// Writes audit entries inside the caller's transaction with a plain INSERT.
/// <para>
/// Why not <c>db.AuditEntries.Add</c>: EF Core inserts identity rows with <c>RETURNING "Id"</c>, and PostgreSQL
/// requires SELECT privilege for RETURNING. The signer role may insert audit entries but not read them
/// (DatabaseGrants), so the tracked insert fails with "permission denied".
/// </para>
/// </summary>
internal static class SignerAudit
{
    public const string ActorName = "fleetify-signer";

    public static async Task WriteAsync(FleetifyDbContext db, AuditRecord record, DateTime now, CancellationToken cancellationToken)
    {
        var entry = AuditLog.ToEntry(record, now);
        var clientId = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Uuid, Value = (object?)entry.ClientId ?? DBNull.Value };
        var details = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Jsonb, Value = entry.DetailsJson };
        var ipAddress = new NpgsqlParameter { NpgsqlDbType = NpgsqlDbType.Varchar, Value = (object?)entry.IpAddress ?? DBNull.Value };

        await db.Database.ExecuteSqlAsync($"""
            INSERT INTO "AuditEntries" ("Time", "ClientId", "ActorType", "ActorId", "ActorName", "Action", "TargetType", "TargetId", "DetailsJson", "IpAddress")
            VALUES ({entry.Time}, {clientId}, {entry.ActorType.ToString()}, {entry.ActorId}, {entry.ActorName}, {entry.Action},
                    {entry.TargetType}, {entry.TargetId}, {details}, {ipAddress})
            """, cancellationToken);
    }
}
