using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Workers.Backups;

/// <summary>
/// The tables of the performance monitor (0.7.0) go into a backup with their structure only (PERFORMANCE.md §6): a restore does
/// not bring old measurements, and the nightly dump stays small. The rows of a hypertable live in its chunks in another schema,
/// so those are named one by one. The settings of the monitor are kept.
/// </summary>
public static class PerformanceBackup
{
    public static readonly IReadOnlyList<string> Tables =
    [
        "PerfMinutes", "PerfHours", "PerfRuntimeMinutes", "PerfHostMinutes", "PerfEvents", "PerfStatementSnapshots",
        "PerfDatabaseSnapshots", "PerfTableSnapshots", "PerfMarkers", "PerfFingerprints"
    ];

    private const string ChunksSql = """
        SELECT c.chunk_schema AS "Schema", c.chunk_name AS "Table"
        FROM timescaledb_information.chunks c
        WHERE c.hypertable_schema = 'public' AND c.hypertable_name = ANY({0})
        """;

    public static async Task<IReadOnlyList<(string Schema, string Table)>> TablesWithoutDataAsync(FleetoDbContext db, ILogger logger,
        CancellationToken cancellationToken)
    {
        var tables = Tables.Select(t => ("public", t)).ToList();
        try
        {
            var timescale = await db.Database
                .SqlQueryRaw<bool>("""SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') AS "Value" """)
                .SingleAsync(cancellationToken);
            if (timescale)
            {
                var chunks = await db.Database.SqlQueryRaw<ChunkRow>(ChunksSql, new object[] { Tables.ToArray() }).ToListAsync(cancellationToken);
                tables.AddRange(chunks.Select(c => (c.Schema, c.Table)));
            }
        }
        catch (PostgresException ex)
        {
            // The dump then holds the measurements too: larger, never wrong.
            logger.LogWarning(ex, "The chunks of the performance tables could not be listed; the backup includes their rows");
        }

        return tables;
    }

    private sealed class ChunkRow
    {
        public string Schema { get; set; } = string.Empty;
        public string Table { get; set; } = string.Empty;
    }
}
