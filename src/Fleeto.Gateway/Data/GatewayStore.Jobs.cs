using System.Security.Cryptography;
using Fleeto.Core.Entities;
using Fleeto.Protocol.Agent.V1;
using Npgsql;
using NpgsqlTypes;
using JobResult = Fleeto.Protocol.Agent.V1.JobResult;
using JobStream = Fleeto.Protocol.Agent.V1.JobStream;

namespace Fleeto.Gateway.Data;

/// <summary>A signed job that may be delivered to its agent now.</summary>
public sealed record DeliverableJob(Guid Id, Guid EndpointId, byte[] Payload, byte[] Signature, string KeyId);

/// <summary>What storing a job message changed, for notifications.</summary>
public enum JobUpdate
{
    /// <summary>Nothing changed (a resend, or a job that is not this endpoint's).</summary>
    None,
    Changed
}

/// <summary>
/// Job statements of the gateway (0.2.0, ARCHITECTURE.md §4, Job and Job output). Every agent message is matched on job id and
/// endpoint id, so an agent can only report on its own jobs. Writes are idempotent: a resent message changes nothing and is
/// acknowledged again.
/// </summary>
public sealed partial class GatewayStore
{
    /// <summary>Largest sequence per stream the gateway stores; together with the byte limit it bounds what one job can write.</summary>
    public const long MaxChunkSequence = 100_000;

    /// <summary>Queued, signed jobs that are still valid, oldest first.</summary>
    public async Task<List<DeliverableJob>> ReadDeliverableJobsAsync(Guid[] endpointIds, DateTime now, CancellationToken cancellationToken)
    {
        var jobs = new List<DeliverableJob>();
        if (endpointIds.Length == 0)
        {
            return jobs;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT "Id", "EndpointId", "Payload", "Signature", "SigningKeyId" FROM "Jobs"
            WHERE "EndpointId" = ANY($1) AND "State" = 'Queued' AND "ValidUntil" > $2 AND "Payload" IS NOT NULL AND "Signature" IS NOT NULL
            ORDER BY "CreatedAt"
            LIMIT 500
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid[]> { TypedValue = endpointIds });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            jobs.Add(new DeliverableJob(reader.GetGuid(0), reader.GetGuid(1), reader.GetFieldValue<byte[]>(2), reader.GetFieldValue<byte[]>(3),
                reader.IsDBNull(4) ? string.Empty : reader.GetString(4)));
        }

        return jobs;
    }

    /// <summary>
    /// Records the first delivery and returns true while the job is still queued and valid. Checked right before sending, so a job
    /// cancelled a moment earlier is never sent; once delivered it can no longer be cancelled.
    /// </summary>
    public async Task<bool> MarkJobDeliveredAsync(Guid jobId, DateTime now, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "Jobs" SET "DeliveredAt" = COALESCE("DeliveredAt", $2)
            WHERE "Id" = $1 AND "State" = 'Queued' AND "ValidUntil" > $2
            RETURNING 1
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    /// <summary>
    /// The agent started the job. Server time is recorded; a job the workers marked expired meanwhile runs after all. The account the agent
    /// reports is stored only for a job that runs as the signed-in user.
    /// </summary>
    public async Task<JobUpdate> JobStartedAsync(Guid jobId, Guid endpointId, DateTime now, string runAsAccount, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            UPDATE "Jobs" SET "State" = 'Running', "StartedAt" = $3,
                "RunAsAccount" = CASE WHEN "RunAs" = 'LoggedOnUser' THEN NULLIF($4, '') END
            WHERE "Id" = $1 AND "EndpointId" = $2 AND "DeliveredAt" IS NOT NULL AND "State" IN ('Queued', 'Expired') AND "CompletedAt" IS NULL
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = DbText.Clean(runAsAccount, 256) });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0 ? JobUpdate.Changed : JobUpdate.None;
    }

    /// <summary>
    /// Stores one output chunk once. A chunk beyond the job's byte limit or sequence limit is not stored (and still acknowledged,
    /// so the agent stops sending it). When the completion is already known, completeness is checked.
    /// </summary>
    public async Task<JobUpdate> StoreJobOutputAsync(Guid jobId, Guid endpointId, JobStream stream, ulong sequence, byte[] data, DateTime now,
        CancellationToken cancellationToken)
    {
        if (data.Length is 0 or > ScriptRules.MaxChunkBytes || sequence >= MaxChunkSequence || stream is not (JobStream.Stdout or JobStream.Stderr))
        {
            return JobUpdate.None;
        }

        var streamName = stream == JobStream.Stdout ? nameof(Core.Entities.JobStream.Stdout) : nameof(Core.Entities.JobStream.Stderr);
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        bool completed;
        await using (var counter = new NpgsqlCommand("""
            UPDATE "Jobs" SET "ReceivedOutputBytes" = "ReceivedOutputBytes" + $3,
                              "OutputState" = CASE WHEN "OutputState" = 'None' THEN 'Receiving' ELSE "OutputState" END
            WHERE "Id" = $1 AND "EndpointId" = $2 AND "DeliveredAt" IS NOT NULL AND "ReceivedOutputBytes" + $3 <= "MaxOutputBytes" + 65536
            RETURNING "ClientId", "CompletedAt" IS NOT NULL
            """, connection, transaction))
        {
            counter.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
            counter.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
            counter.Parameters.Add(new NpgsqlParameter<long> { TypedValue = data.Length });
            await using var reader = await counter.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return JobUpdate.None;
            }

            var clientId = reader.GetGuid(0);
            completed = reader.GetBoolean(1);
            await reader.DisposeAsync();

            await using var insert = new NpgsqlCommand("""
                INSERT INTO "JobOutputChunks" ("JobId", "Stream", "Sequence", "ClientId", "Data", "ReceivedAt") VALUES ($1, $2, $3, $4, $5, $6)
                ON CONFLICT DO NOTHING
                """, connection, transaction);
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
            insert.Parameters.Add(new NpgsqlParameter<string> { TypedValue = streamName });
            insert.Parameters.Add(new NpgsqlParameter<long> { TypedValue = (long)sequence });
            insert.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = clientId });
            insert.Parameters.Add(new NpgsqlParameter { Value = data, NpgsqlDbType = NpgsqlDbType.Bytea });
            insert.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
            if (await insert.ExecuteNonQueryAsync(cancellationToken) == 0)
            {
                // A resend: roll back the counter.
                await transaction.RollbackAsync(cancellationToken);
                return JobUpdate.None;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return completed && await TryCompleteOutputAsync(jobId, cancellationToken) ? JobUpdate.Changed : JobUpdate.None;
    }

    /// <summary>Records the job's result once. A result for a job marked lost meanwhile is the real outcome and replaces it.</summary>
    public async Task<JobUpdate> CompleteJobAsync(Guid jobId, Guid endpointId, JobCompletion completion, DateTime now, CancellationToken cancellationToken)
    {
        var (state, result) = completion.Result switch
        {
            JobResult.Exited => (completion.ExitCode == 0 ? JobState.Succeeded : JobState.Failed, Core.Entities.JobResult.Exited),
            JobResult.TimedOut => (JobState.Failed, Core.Entities.JobResult.TimedOut),
            JobResult.Refused => (JobState.Refused, Core.Entities.JobResult.Refused),
            JobResult.Interrupted => (JobState.Lost, Core.Entities.JobResult.Interrupted),
            _ => (JobState.Failed, Core.Entities.JobResult.FailedToStart)
        };

        await using var command = _dataSource.CreateCommand("""
            UPDATE "Jobs" SET "State" = $3, "Result" = $4, "ExitCode" = $5, "Error" = $6, "CompletedAt" = $7, "StartedAt" = COALESCE("StartedAt", $7),
                "OutputTruncated" = $8,
                "StdoutChunks" = $9, "StdoutBytes" = $10, "StdoutSha256" = $11, "StderrChunks" = $12, "StderrBytes" = $13, "StderrSha256" = $14,
                "RefusalReason" = CASE WHEN $3 = 'Refused' THEN $6 ELSE "RefusalReason" END,
                "OutputState" = CASE WHEN $9 = 0 AND $12 = 0 THEN 'Complete' WHEN "OutputState" = 'None' THEN 'Receiving' ELSE "OutputState" END
            WHERE "Id" = $1 AND "EndpointId" = $2 AND "DeliveredAt" IS NOT NULL AND "CompletedAt" IS NULL
              AND "State" IN ('Queued', 'Running', 'Expired', 'Lost')
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = endpointId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = state.ToString() });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = result.ToString() });
        command.Parameters.Add(new NpgsqlParameter { Value = completion.Result == JobResult.Exited ? completion.ExitCode : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });
        var error = DbText.Clean(completion.Error, 1000);
        command.Parameters.Add(new NpgsqlParameter { Value = error.Length == 0 ? DBNull.Value : error, NpgsqlDbType = NpgsqlDbType.Varchar });
        command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
        command.Parameters.Add(new NpgsqlParameter<bool> { TypedValue = completion.OutputTruncated });
        AddSummary(command, completion.Stdout);
        AddSummary(command, completion.Stderr);
        if (await command.ExecuteNonQueryAsync(cancellationToken) == 0)
        {
            return JobUpdate.None;
        }

        await TryCompleteOutputAsync(jobId, cancellationToken);
        return JobUpdate.Changed;
    }

    private static void AddSummary(NpgsqlCommand command, JobStreamSummary? summary)
    {
        var chunks = (long)Math.Min(summary?.Chunks ?? 0, (ulong)MaxChunkSequence);
        var bytes = (long)Math.Min(summary?.Bytes ?? 0, (ulong)(ScriptRules.MaxOutputBytes + ScriptRules.MaxChunkBytes));
        var sha = summary?.Sha256 is { Length: 64 } hex && hex.All(Uri.IsHexDigit) ? hex.ToLowerInvariant() : null;
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = chunks });
        command.Parameters.Add(new NpgsqlParameter<long> { TypedValue = bytes });
        command.Parameters.Add(new NpgsqlParameter { Value = (object?)sha ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Varchar });
    }

    /// <summary>
    /// When every announced chunk is stored, hashes the output per stream and marks it complete, or incomplete when a hash differs.
    /// Returns true when the output state changed.
    /// </summary>
    public async Task<bool> TryCompleteOutputAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        long?[] expectedChunks = new long?[2];
        long?[] expectedBytes = new long?[2];
        string?[] expectedSha = new string?[2];
        await using (var command = new NpgsqlCommand("""
            SELECT "StdoutChunks", "StdoutBytes", "StdoutSha256", "StderrChunks", "StderrBytes", "StderrSha256" FROM "Jobs"
            WHERE "Id" = $1 AND "CompletedAt" IS NOT NULL AND "OutputState" = 'Receiving'
            """, connection))
        {
            command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return false;
            }

            for (var i = 0; i < 2; i++)
            {
                expectedChunks[i] = reader.IsDBNull(i * 3) ? 0 : reader.GetInt64(i * 3);
                expectedBytes[i] = reader.IsDBNull(i * 3 + 1) ? 0 : reader.GetInt64(i * 3 + 1);
                expectedSha[i] = reader.IsDBNull(i * 3 + 2) ? null : reader.GetString(i * 3 + 2);
            }
        }

        var matches = true;
        string[] streams = [nameof(Core.Entities.JobStream.Stdout), nameof(Core.Entities.JobStream.Stderr)];
        for (var i = 0; i < 2; i++)
        {
            await using (var count = new NpgsqlCommand("""
                SELECT count(*), COALESCE(sum(octet_length("Data")), 0), COALESCE(max("Sequence"), -1) FROM "JobOutputChunks" WHERE "JobId" = $1 AND "Stream" = $2
                """, connection))
            {
                count.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
                count.Parameters.Add(new NpgsqlParameter<string> { TypedValue = streams[i] });
                await using var reader = await count.ExecuteReaderAsync(cancellationToken);
                await reader.ReadAsync(cancellationToken);
                var chunks = reader.GetInt64(0);
                var bytes = reader.GetInt64(1);
                var maxSequence = reader.GetInt64(2);
                if (chunks != expectedChunks[i] || bytes != expectedBytes[i] || maxSequence != chunks - 1)
                {
                    // Not everything has arrived yet.
                    return false;
                }
            }

            if (expectedChunks[i] == 0)
            {
                continue;
            }

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var read = new NpgsqlCommand("""
                SELECT "Data" FROM "JobOutputChunks" WHERE "JobId" = $1 AND "Stream" = $2 ORDER BY "Sequence"
                """, connection))
            {
                read.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
                read.Parameters.Add(new NpgsqlParameter<string> { TypedValue = streams[i] });
                await using var reader = await read.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess, cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    hash.AppendData(await reader.GetFieldValueAsync<byte[]>(0, cancellationToken));
                }
            }

            matches &= expectedSha[i] is not null && Convert.ToHexStringLower(hash.GetHashAndReset()) == expectedSha[i];
        }

        await using var update = new NpgsqlCommand("""
            UPDATE "Jobs" SET "OutputState" = $2 WHERE "Id" = $1 AND "OutputState" = 'Receiving'
            """, connection);
        update.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = jobId });
        update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = matches ? nameof(JobOutputState.Complete) : nameof(JobOutputState.Incomplete) });
        return await update.ExecuteNonQueryAsync(cancellationToken) > 0;
    }
}
