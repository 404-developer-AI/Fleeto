using System.Globalization;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Settings;
using Fleetify.Workers.Checks;
using Fleetify.Workers.Email;
using Fleetify.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleetify.Workers.Retention;

/// <summary>
/// Enforces retention every hour (CLAUDE.md, Performance and GDPR: the disk is finite and personal data is kept no longer
/// than needed). Every delete runs in batches of at most <see cref="BatchSize"/> rows, each its own statement, so no
/// giant transaction locks a table or bloats the WAL.
/// <list type="bullet">
/// <item>Check results older than the configured days, only when TimescaleDB is not installed (otherwise its retention
/// policy drops whole chunks).</item>
/// <item>Check results of deleted endpoints: found through their evaluation cursors (all ages, by endpoint index) and by a
/// scan of recent results (results that were never evaluated).</item>
/// <item>Ingest batches after 7 days, finished signing requests and check run requests after 7 days, processed endpoint events after 30 days,
/// sent emails after 30 days and failed ones after 90 days, agent certificates and enrollment tokens 30 days after they
/// expired or were used up, and the evaluation cursors of deleted endpoints.</item>
/// </list>
/// </summary>
public sealed class RetentionService : WorkerLoop
{
    public const int BatchSize = 5000;
    public const int DefaultCheckResultsDays = 30;

    /// <summary>Upper bound of batches per statement per run, so one run never monopolizes the database.</summary>
    private const int MaxBatchesPerStatement = 200;

    private const int DeletedEndpointsPerRun = 200;

    private const string TimescaleInstalledSql = """SELECT EXISTS (SELECT 1 FROM pg_extension WHERE extname = 'timescaledb') AS "Value" """;

    private const string DeleteOldCheckResultsSql = """
        DELETE FROM "CheckResults" WHERE ("Time", "Id") IN (
          SELECT "Time", "Id" FROM "CheckResults" WHERE "Time" < @cutoff LIMIT 5000)
        """;

    // No braces in raw SQL: EF Core treats them as format placeholders.
    private const string DeletedEndpointWatermarksSql = """
        SELECT w."Name" AS "Value"
        FROM "WorkerWatermarks" w
        WHERE w."Name" LIKE 'check-eval:%'
          AND NOT EXISTS (
            SELECT 1 FROM "Endpoints" e
            WHERE e."Id" = CASE WHEN length(w."Name") = 43 AND substr(w."Name", 12) ~ '^[0-9a-f]+$' THEN substr(w."Name", 12)::uuid END)
        LIMIT 200
        """;

    private const string DeleteEndpointCheckResultsSql = """
        DELETE FROM "CheckResults" WHERE ("Time", "Id") IN (
          SELECT "Time", "Id" FROM "CheckResults" WHERE "EndpointId" = @endpointId LIMIT 5000)
        """;

    private const string DeleteOrphanedRecentCheckResultsSql = """
        DELETE FROM "CheckResults" WHERE ("Time", "Id") IN (
          SELECT r."Time", r."Id" FROM "CheckResults" r
          WHERE r."Time" >= @since AND NOT EXISTS (SELECT 1 FROM "Endpoints" e WHERE e."Id" = r."EndpointId")
          LIMIT 5000)
        """;

    private const string DeleteIngestBatchesSql = """
        DELETE FROM "IngestBatches" WHERE ("EndpointId", "Sequence") IN (
          SELECT "EndpointId", "Sequence" FROM "IngestBatches" WHERE "ReceivedAt" < @cutoff LIMIT 5000)
        """;

    private const string DeleteSigningRequestsSql = """
        DELETE FROM "SigningRequests" WHERE "Id" IN (
          SELECT "Id" FROM "SigningRequests" WHERE "State" <> 'Pending' AND "CreatedAt" < @cutoff LIMIT 5000)
        """;

    private const string DeleteCheckRunRequestsSql = """
        DELETE FROM "CheckRunRequests" WHERE "Id" IN (
          SELECT "Id" FROM "CheckRunRequests" WHERE "RequestedAt" < @cutoff LIMIT 5000)
        """;

    private const string DeleteEndpointEventsSql = """
        DELETE FROM "EndpointEvents" WHERE "Id" IN (
          SELECT "Id" FROM "EndpointEvents" WHERE "ProcessedAt" IS NOT NULL AND "Time" < @cutoff LIMIT 5000)
        """;

    private const string DeleteSentEmailsSql = """
        DELETE FROM "OutboxEmails" WHERE "Id" IN (
          SELECT "Id" FROM "OutboxEmails" WHERE "SentAt" IS NOT NULL AND "SentAt" < @cutoff LIMIT 5000)
        """;

    private const string DeleteFailedEmailsSql = """
        DELETE FROM "OutboxEmails" WHERE "Id" IN (
          SELECT "Id" FROM "OutboxEmails" WHERE "SentAt" IS NULL AND "Attempts" >= @maxAttempts AND "CreatedAt" < @cutoff LIMIT 5000)
        """;

    private const string DeleteAgentCertificatesSql = """
        DELETE FROM "AgentCertificates" WHERE "Id" IN (
          SELECT "Id" FROM "AgentCertificates" WHERE "ExpiresAt" < @cutoff LIMIT 5000)
        """;

    // EnrollmentTokens has no "used at" column: a used-up token counts from its creation, which is never later than its last use.
    private const string DeleteEnrollmentTokensSql = """
        DELETE FROM "EnrollmentTokens" WHERE "Id" IN (
          SELECT "Id" FROM "EnrollmentTokens"
          WHERE "ExpiresAt" < @cutoff
             OR ("RevokedAt" IS NOT NULL AND "RevokedAt" < @cutoff)
             OR ("MaxUses" IS NOT NULL AND "UseCount" >= "MaxUses" AND "CreatedAt" < @cutoff)
          LIMIT 5000)
        """;

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;

    public RetentionService(IFleetifyDbContextFactory dbFactory, SettingsStore settings, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<RetentionService> logger)
        : base("retention", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override TimeSpan MaxRunDuration => TimeSpan.FromHours(1);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await RunAsync(cancellationToken);
        return false;
    }

    /// <summary>One retention run. Returns the number of deleted rows per data type.</summary>
    public async Task<IReadOnlyDictionary<string, int>> RunAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var deleted = new Dictionary<string, int>();

        await using (var db = _dbFactory.CreateSystem())
        {
            var timescale = await db.Database.SqlQueryRaw<bool>(TimescaleInstalledSql).SingleAsync(cancellationToken);
            if (!timescale)
            {
                var days = await CheckResultsDaysAsync(cancellationToken);
                deleted["CheckResults (age)"] = await DeleteInBatchesAsync(DeleteOldCheckResultsSql, () => [new NpgsqlParameter("cutoff", now.AddDays(-days))], cancellationToken);
            }
        }

        deleted["CheckResults (deleted endpoints)"] = await PurgeDeletedEndpointResultsAsync(now, cancellationToken);
        deleted["IngestBatches"] = await DeleteInBatchesAsync(DeleteIngestBatchesSql, () => [new NpgsqlParameter("cutoff", now.AddDays(-7))], cancellationToken);
        deleted["SigningRequests"] = await DeleteInBatchesAsync(DeleteSigningRequestsSql, () => [new NpgsqlParameter("cutoff", now.AddDays(-7))], cancellationToken);
        deleted["CheckRunRequests"] = await DeleteInBatchesAsync(DeleteCheckRunRequestsSql, () => [new NpgsqlParameter("cutoff", now.AddDays(-7))], cancellationToken);
        deleted["EndpointEvents"] = await DeleteInBatchesAsync(DeleteEndpointEventsSql, () => [new NpgsqlParameter("cutoff", now.AddDays(-30))], cancellationToken);
        deleted["OutboxEmails (sent)"] = await DeleteInBatchesAsync(DeleteSentEmailsSql, () => [new NpgsqlParameter("cutoff", now.AddDays(-30))], cancellationToken);
        deleted["OutboxEmails (failed)"] = await DeleteInBatchesAsync(DeleteFailedEmailsSql,
            () => [new NpgsqlParameter("cutoff", now.AddDays(-90)), new NpgsqlParameter("maxAttempts", OutboxEmailService.MaxAttempts)], cancellationToken);
        deleted["AgentCertificates"] = await DeleteInBatchesAsync(DeleteAgentCertificatesSql, () => [new NpgsqlParameter("cutoff", now.AddDays(-30))], cancellationToken);
        deleted["EnrollmentTokens"] = await DeleteInBatchesAsync(DeleteEnrollmentTokensSql, () => [new NpgsqlParameter("cutoff", now.AddDays(-30))], cancellationToken);

        var total = deleted.Values.Sum();
        if (total > 0)
        {
            Logger.LogInformation("Retention removed {Total} row(s): {Details}", total,
                string.Join(", ", deleted.Where(d => d.Value > 0).Select(d => $"{d.Key} {d.Value}")));
        }

        return deleted;
    }

    private async Task<int> CheckResultsDaysAsync(CancellationToken cancellationToken)
    {
        var text = await _settings.GetStringAsync(SettingKeys.RetentionCheckResultsDays, cancellationToken);
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var days) ? Math.Clamp(days, 1, 3650) : DefaultCheckResultsDays;
    }

    private async Task<int> PurgeDeletedEndpointResultsAsync(DateTime now, CancellationToken cancellationToken)
    {
        List<string> watermarks;
        await using (var db = _dbFactory.CreateSystem())
        {
            watermarks = await db.Database.SqlQueryRaw<string>(DeletedEndpointWatermarksSql).ToListAsync(cancellationToken);
        }

        var total = 0;
        foreach (var name in watermarks.Take(DeletedEndpointsPerRun))
        {
            if (!Guid.TryParseExact(name[CheckEvaluationService.WatermarkPrefix.Length..], "N", out var endpointId))
            {
                continue;
            }

            var (count, complete) = await DeleteInBatchesWithStatusAsync(DeleteEndpointCheckResultsSql,
                () => [new NpgsqlParameter("endpointId", endpointId)], cancellationToken);
            total += count;
            if (complete)
            {
                await using var db = _dbFactory.CreateSystem();
                await db.WorkerWatermarks.Where(w => w.Name == name).ExecuteDeleteAsync(cancellationToken);
            }
        }

        total += await DeleteInBatchesAsync(DeleteOrphanedRecentCheckResultsSql, () => [new NpgsqlParameter("since", now.AddDays(-2))], cancellationToken);
        return total;
    }

    private async Task<int> DeleteInBatchesAsync(string sql, Func<object[]> parameters, CancellationToken cancellationToken) =>
        (await DeleteInBatchesWithStatusAsync(sql, parameters, cancellationToken)).Deleted;

    /// <summary>Repeats a LIMIT 5000 delete until it deletes fewer rows. Parameters are created per statement (Npgsql parameters cannot be shared).</summary>
    private async Task<(int Deleted, bool Complete)> DeleteInBatchesWithStatusAsync(string sql, Func<object[]> parameters,
        CancellationToken cancellationToken)
    {
        var total = 0;
        for (var batch = 0; batch < MaxBatchesPerStatement; batch++)
        {
            int count;
            await using (var db = _dbFactory.CreateSystem())
            {
                db.Database.SetCommandTimeout(TimeSpan.FromMinutes(5));
                count = await db.Database.ExecuteSqlRawAsync(sql, parameters(), cancellationToken);
            }

            total += count;
            if (count < BatchSize)
            {
                return (total, true);
            }

            // Give concurrent writers room between batches.
            await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
        }

        return (total, false);
    }
}
