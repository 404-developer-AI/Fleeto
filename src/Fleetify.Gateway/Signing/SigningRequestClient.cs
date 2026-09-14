using System.Collections.Concurrent;
using System.Diagnostics;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Fleetify.Gateway.Signing;

public enum SigningOutcomeState
{
    Completed,
    Refused,
    Failed,
    /// <summary>fleetify-signer did not finish the request in time. The row stays Pending.</summary>
    TimedOut
}

/// <summary>The final state of a signing request as seen by the requester.</summary>
public sealed record SigningOutcome(SigningOutcomeState State, byte[]? Result, string? RefusalReason);

/// <summary>
/// Inserts a <see cref="SigningRequest"/> and waits for fleetify-signer to finish it (contract §4). The waiter is
/// registered before the insert and the row is re-read after every wake-up, so a notification that arrives early,
/// late or never only changes how fast the answer comes: a periodic re-read covers a lost notification.
/// </summary>
public sealed class SigningRequestClient : IDisposable
{
    /// <summary>Re-read interval when no notification arrives (lost notification, listener reconnecting).</summary>
    internal static TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(2);

    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly GatewayOptions _options;
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _waiters = new();
    private readonly IDisposable _subscription;

    public SigningRequestClient(NpgsqlDataSource dataSource, INotificationBus bus, TimeProvider time, IOptions<GatewayOptions> options)
    {
        _dataSource = dataSource;
        _time = time;
        _options = options.Value;
        _subscription = bus.Subscribe(NotificationChannels.SigningResults, OnResultAsync);
    }

    /// <summary>
    /// Inserts a request and waits up to <see cref="GatewayOptions.SigningTimeout"/> for the result. Database errors
    /// propagate to the caller, which answers "try again later".
    /// </summary>
    public async Task<SigningOutcome> RequestAsync(SigningRequestKind kind, Guid? clientId, Guid? subjectId, byte[] payload,
        string requestedBy, CancellationToken cancellationToken)
    {
        var id = Guid.CreateVersion7();
        using var signal = new SemaphoreSlim(0);
        _waiters[id] = signal;
        try
        {
            await InsertAsync(id, kind, clientId, subjectId, payload, requestedBy, cancellationToken);

            var timeout = _options.SigningTimeout;
            var stopwatch = Stopwatch.StartNew();
            while (true)
            {
                var outcome = await ReadAsync(id, cancellationToken);
                if (outcome is not null)
                {
                    return outcome;
                }

                var remaining = timeout - stopwatch.Elapsed;
                if (remaining <= TimeSpan.Zero)
                {
                    return new SigningOutcome(SigningOutcomeState.TimedOut, null, null);
                }

                await signal.WaitAsync(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
            }
        }
        finally
        {
            _waiters.TryRemove(id, out _);
        }
    }

    private async Task InsertAsync(Guid id, SigningRequestKind kind, Guid? clientId, Guid? subjectId, byte[] payload,
        string requestedBy, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            INSERT INTO "SigningRequests" ("Id", "ClientId", "Kind", "SubjectId", "Payload", "RequestedBy", "State", "CreatedAt")
            VALUES (@id, @clientId, @kind, @subjectId, @payload, @requestedBy, 'Pending', @createdAt)
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        command.Parameters.Add(new NpgsqlParameter("clientId", NpgsqlDbType.Uuid) { Value = (object?)clientId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<string>("kind", kind.ToString()));
        command.Parameters.Add(new NpgsqlParameter("subjectId", NpgsqlDbType.Uuid) { Value = (object?)subjectId ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter<byte[]>("payload", payload));
        command.Parameters.Add(new NpgsqlParameter<string>("requestedBy", requestedBy.Length > 200 ? requestedBy[..200] : requestedBy));
        command.Parameters.Add(new NpgsqlParameter<DateTime>("createdAt", _time.GetUtcNow().UtcDateTime));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SigningOutcome?> ReadAsync(Guid id, CancellationToken cancellationToken)
    {
        await using var command = _dataSource.CreateCommand("""
            SELECT "State", "Result", "RefusalReason" FROM "SigningRequests" WHERE "Id" = @id
            """);
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", id));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            // Deleted by retention while we waited: treat as a failure the caller can retry.
            return new SigningOutcome(SigningOutcomeState.Failed, null, null);
        }

        var state = reader.GetString(0);
        return state switch
        {
            nameof(SigningRequestState.Pending) => null,
            nameof(SigningRequestState.Completed) => new SigningOutcome(SigningOutcomeState.Completed,
                reader.IsDBNull(1) ? null : reader.GetFieldValue<byte[]>(1), null),
            nameof(SigningRequestState.Refused) => new SigningOutcome(SigningOutcomeState.Refused, null,
                reader.IsDBNull(2) ? null : reader.GetString(2)),
            _ => new SigningOutcome(SigningOutcomeState.Failed, null, reader.IsDBNull(2) ? null : reader.GetString(2))
        };
    }

    private Task OnResultAsync(string payload, CancellationToken cancellationToken)
    {
        if (payload == NotificationBusEvents.Resync)
        {
            foreach (var waiter in _waiters.Values)
            {
                Release(waiter);
            }
        }
        else if (Guid.TryParse(payload, out var id) && _waiters.TryGetValue(id, out var waiter))
        {
            Release(waiter);
        }

        return Task.CompletedTask;
    }

    private static void Release(SemaphoreSlim waiter)
    {
        try
        {
            waiter.Release();
        }
        catch (ObjectDisposedException)
        {
            // The requester finished between lookup and release.
        }
    }

    public void Dispose() => _subscription.Dispose();
}
