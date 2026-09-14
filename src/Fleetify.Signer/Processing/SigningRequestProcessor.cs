using System.Net;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Signer.Keys;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace Fleetify.Signer.Processing;

/// <summary>What happened to one claimed request.</summary>
public enum ProcessStatus
{
    /// <summary>Nothing to claim: no pending request, or another signer holds it.</summary>
    NothingClaimed,
    Completed,
    Refused,
    Failed,
    /// <summary>Over the rate limit; the request stays Pending for a later attempt.</summary>
    RateLimited
}

public sealed record ProcessResult(ProcessStatus Status, Guid? RequestId = null, SigningRequestKind? Kind = null);

public sealed record BatchResult(int Handled, int RateLimited);

/// <summary>
/// Claims pending signing requests and runs them through their handler, one transaction per request.
/// <para>
/// Claiming uses <c>FOR UPDATE SKIP LOCKED</c>, so several signer processes can run side by side and a request is
/// signed once. The row lock is held until the state change commits; the handler's writes and the state change
/// commit together, so a crash in between leaves the request Pending and nothing half-done.
/// </para>
/// </summary>
public sealed class SigningRequestProcessor
{
    public const int BatchSize = 20;

    /// <summary>Requesters give up after 30 seconds; anything this old has no one waiting for it.</summary>
    public static readonly TimeSpan MaxRequestAge = TimeSpan.FromMinutes(5);

    /// <summary>Rate-limited enrollments and renewals older than this are refused; the agent retries on its own.</summary>
    public static readonly TimeSpan RateLimitedRefusalAge = TimeSpan.FromSeconds(60);

    public const string ExpiredReason = "The request expired before it could be signed. Try again.";
    public const string FailedReason = "The signer could not process the request. See the signer log.";
    public const string TooManyRequestsReason = "Too many requests; the agent will retry.";
    public const string UnknownKindReason =
        "The signer does not handle this kind of request. Check that the gateway, workers and signer run the same version.";

    private const string HandlerSavepoint = "fleetify_signer_handler";
    private const string SignerActor = "fleetify-signer";

    private static readonly string[] KnownKinds = Enum.GetNames<SigningRequestKind>();
    private static readonly IReadOnlySet<SigningRequestKind> NoKinds = new HashSet<SigningRequestKind>();

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly SignerKeyRing _keyRing;
    private readonly IReadOnlyDictionary<SigningRequestKind, ISigningRequestHandler> _handlers;
    private readonly SigningRateLimiter _rateLimiter;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<SigningRequestProcessor> _logger;

    public SigningRequestProcessor(IFleetifyDbContextFactory dbFactory, SignerKeyRing keyRing, IEnumerable<ISigningRequestHandler> handlers,
        SigningRateLimiter rateLimiter, INotificationBus bus, TimeProvider time, ILogger<SigningRequestProcessor> logger)
    {
        _dbFactory = dbFactory;
        _keyRing = keyRing;
        _handlers = handlers.ToDictionary(h => h.Kind);
        _rateLimiter = rateLimiter;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Handles up to <see cref="BatchSize"/> pending requests, oldest first. A kind that hits its rate limit is left
    /// out for the rest of the batch (enrollments and renewals older than a minute stay eligible, to be refused).
    /// </summary>
    public async Task<BatchResult> ProcessBatchAsync(CancellationToken cancellationToken = default)
    {
        var waitingKinds = new HashSet<SigningRequestKind>();
        var youngOnlyKinds = new HashSet<SigningRequestKind>();
        var handled = 0;
        var rateLimited = 0;

        // Bounded: every rate-limited result removes (part of) a kind from the query, so the extra attempts are few.
        for (var attempt = 0; handled < BatchSize && attempt < BatchSize * 2; attempt++)
        {
            var result = await ProcessNextAsync(null, waitingKinds, youngOnlyKinds, cancellationToken);
            if (result.Status == ProcessStatus.NothingClaimed)
            {
                break;
            }

            if (result.Status == ProcessStatus.RateLimited)
            {
                rateLimited++;
                if (result.Kind is SigningRequestKind.AgentEnrollment or SigningRequestKind.AgentRenewal)
                {
                    youngOnlyKinds.Add(result.Kind.Value);
                }
                else if (result.Kind is { } kind)
                {
                    waitingKinds.Add(kind);
                }

                continue;
            }

            handled++;
        }

        return new BatchResult(handled, rateLimited);
    }

    /// <summary>Claims and handles one specific request, if it is still pending and not held by another signer.</summary>
    public Task<ProcessResult> ProcessRequestAsync(Guid requestId, CancellationToken cancellationToken = default) =>
        ProcessNextAsync(requestId, NoKinds, NoKinds, cancellationToken);

    private async Task<ProcessResult> ProcessNextAsync(Guid? requestId, IReadOnlySet<SigningRequestKind> waitingKinds,
        IReadOnlySet<SigningRequestKind> youngOnlyKinds, CancellationToken cancellationToken)
    {
        if (!_keyRing.IsLoaded)
        {
            throw new InvalidOperationException("The signer keys are not loaded; requests cannot be processed.");
        }

        await using var db = _dbFactory.CreateSystem();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var claimTime = _time.GetUtcNow().UtcDateTime;
        var request = await ClaimAsync(db, requestId, waitingKinds, youngOnlyKinds, claimTime - RateLimitedRefusalAge, cancellationToken);
        if (request is null)
        {
            return new ProcessResult(ProcessStatus.NothingClaimed);
        }

        var id = request.Id;
        var kind = request.Kind;
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var age = now - request.CreatedAt;
            SigningOutcome outcome;

            if (!_handlers.TryGetValue(kind, out var handler))
            {
                outcome = SigningOutcome.Refused(UnknownKindReason);
            }
            // Configs are computed from the database, not from the request, so an old config request is still correct
            // to serve; refusing it after an outage would leave endpoints on a stale configuration.
            else if (kind != SigningRequestKind.AgentConfig && age > MaxRequestAge)
            {
                outcome = SigningOutcome.Refused(ExpiredReason);
            }
            else if (!_rateLimiter.TryAcquire(kind, now))
            {
                if (kind is SigningRequestKind.AgentEnrollment or SigningRequestKind.AgentRenewal && age > RateLimitedRefusalAge)
                {
                    outcome = SigningOutcome.Refused(TooManyRequestsReason);
                }
                else
                {
                    await transaction.RollbackAsync(cancellationToken);
                    _logger.LogDebug("Rate limit reached for {Kind}; request {RequestId} stays pending", kind, id);
                    return new ProcessResult(ProcessStatus.RateLimited, id, kind);
                }
            }
            else
            {
                (request, outcome) = await RunHandlerAsync(db, transaction, handler, request, now, cancellationToken);
            }

            request.State = outcome.State;
            request.Result = outcome.State == SigningRequestState.Completed ? outcome.Result : null;
            request.RefusalReason = outcome.RefusalReason is null ? null : Truncate(outcome.RefusalReason, 1000);
            request.CompletedAt = now;

            if (outcome.State == SigningRequestState.Refused)
            {
                await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.SigningRefused, "SigningRequest", id.ToString(),
                    request.ClientId, AuditActorType.System, SignerActor, SignerActor,
                    new
                    {
                        Kind = kind.ToString(),
                        Reason = outcome.RefusalReason,
                        request.RequestedBy,
                        request.SubjectId
                    },
                    IpAddressFrom(request.RequestedBy)), now, cancellationToken);
                _logger.LogWarning("Refused {Kind} request {RequestId} from {RequestedBy}: {Reason}", kind, id, request.RequestedBy,
                    outcome.RefusalReason);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await PublishAsync(outcome.Notifications, cancellationToken);
            return new ProcessResult(outcome.State switch
            {
                SigningRequestState.Completed => ProcessStatus.Completed,
                SigningRequestState.Refused => ProcessStatus.Refused,
                _ => ProcessStatus.Failed
            }, id, kind);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // The handler outcome could not be stored (e.g. a constraint violation at commit). Record the failure in a
            // fresh transaction so the requester gets an answer instead of a timeout.
            _logger.LogError(ex, "Could not complete {Kind} request {RequestId}", kind, id);
            await transaction.DisposeAsync();
            await MarkFailedAsync(id, cancellationToken);
            return new ProcessResult(ProcessStatus.Failed, id, kind);
        }
    }

    private async Task<(SigningRequest Request, SigningOutcome Outcome)> RunHandlerAsync(FleetifyDbContext db, IDbContextTransaction transaction,
        ISigningRequestHandler handler, SigningRequest request, DateTime now, CancellationToken cancellationToken)
    {
        var id = request.Id;
        await transaction.CreateSavepointAsync(HandlerSavepoint, cancellationToken);

        SigningOutcome outcome;
        try
        {
            outcome = await handler.HandleAsync(new SigningContext(db, request, now), cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            _logger.LogError(ex, "The {Kind} handler failed for request {RequestId}", request.Kind, id);
            outcome = new SigningOutcome(SigningRequestState.Failed, null, FailedReason, []);
        }

        if (outcome.State == SigningRequestState.Completed)
        {
            return (request, outcome);
        }

        // A refusal or failure must leave no trace of whatever the handler wrote before it decided.
        await transaction.RollbackToSavepointAsync(HandlerSavepoint, cancellationToken);
        db.ChangeTracker.Clear();
        var reloaded = await db.SigningRequests.IgnoreQueryFilters().SingleAsync(r => r.Id == id, cancellationToken);
        return (reloaded, outcome with { Result = null, Notifications = [] });
    }

    private static async Task<SigningRequest?> ClaimAsync(FleetifyDbContext db, Guid? requestId, IReadOnlySet<SigningRequestKind> waitingKinds,
        IReadOnlySet<SigningRequestKind> youngOnlyKinds, DateTime youngCutoff, CancellationToken cancellationToken)
    {
        // Not composed further: EF Core sends this SQL as written, so the locking clause stays on the outer query.
        List<SigningRequest> rows;
        if (requestId is { } id)
        {
            rows = await db.SigningRequests.FromSql($"""
                SELECT * FROM "SigningRequests"
                WHERE "Id" = {id} AND "State" = 'Pending' AND "Kind" = ANY({KnownKinds})
                FOR UPDATE SKIP LOCKED
                """).IgnoreQueryFilters().ToListAsync(cancellationToken);
        }
        else
        {
            var waiting = waitingKinds.Select(k => k.ToString()).ToArray();
            var youngOnly = youngOnlyKinds.Select(k => k.ToString()).ToArray();
            // Rows of a rate-limited enrollment or renewal kind stay eligible only when old enough to be refused.
            rows = await db.SigningRequests.FromSql($"""
                SELECT * FROM "SigningRequests"
                WHERE "State" = 'Pending'
                  AND "Kind" = ANY({KnownKinds})
                  AND NOT ("Kind" = ANY({waiting}))
                  AND NOT ("Kind" = ANY({youngOnly}) AND "CreatedAt" > {youngCutoff})
                ORDER BY "CreatedAt"
                LIMIT 1
                FOR UPDATE SKIP LOCKED
                """).IgnoreQueryFilters().ToListAsync(cancellationToken);
        }

        return rows.Count == 0 ? null : rows[0];
    }

    private async Task MarkFailedAsync(Guid requestId, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = _dbFactory.CreateSystem();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var rows = await db.SigningRequests.FromSql($"""
                SELECT * FROM "SigningRequests" WHERE "Id" = {requestId} AND "State" = 'Pending' FOR UPDATE SKIP LOCKED
                """).IgnoreQueryFilters().ToListAsync(cancellationToken);
            if (rows.Count == 0)
            {
                return;
            }

            rows[0].State = SigningRequestState.Failed;
            rows[0].Result = null;
            rows[0].RefusalReason = FailedReason;
            rows[0].CompletedAt = _time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Stays Pending; the next poll tries again.
            _logger.LogError(ex, "Could not mark request {RequestId} as failed", requestId);
        }
    }

    private async Task PublishAsync(IReadOnlyList<PendingNotification> notifications, CancellationToken cancellationToken)
    {
        foreach (var notification in notifications)
        {
            try
            {
                await _bus.PublishAsync(notification.Channel, notification.Payload, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A lost notification only delays work: subscribers also catch up from the database.
                _logger.LogWarning(ex, "Could not publish {Channel} for {Payload}", notification.Channel, notification.Payload);
            }
        }
    }

    /// <summary>The gateway records enrollments as <c>gateway:&lt;remote ip&gt;</c>; the address goes into the audit entry.</summary>
    internal static string? IpAddressFrom(string requestedBy)
    {
        const string prefix = "gateway:";
        if (!requestedBy.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        return IPAddress.TryParse(requestedBy[prefix.Length..], out var address) ? address.ToString() : null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
