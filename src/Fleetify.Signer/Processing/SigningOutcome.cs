using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;

namespace Fleetify.Signer.Processing;

/// <summary>
/// Everything a handler gets: the request row (locked by the processor for the duration of the transaction) and a
/// context inside that transaction. Handlers read and write through <see cref="Db"/> only and never commit.
/// </summary>
public sealed record SigningContext(FleetifyDbContext Db, SigningRequest Request, DateTime Now);

/// <summary>A notification to publish after the transaction commits.</summary>
public sealed record PendingNotification(string Channel, string Payload);

/// <summary>What a handler decided. Refusals carry a reason that states the cause and the next step.</summary>
public sealed record SigningOutcome(SigningRequestState State, byte[]? Result, string? RefusalReason, IReadOnlyList<PendingNotification> Notifications)
{
    public static SigningOutcome Completed(byte[]? result, params PendingNotification[] notifications) =>
        new(SigningRequestState.Completed, result, null, notifications);

    public static SigningOutcome Refused(string reason) => new(SigningRequestState.Refused, null, reason, []);
}

/// <summary>Processes one kind of signing request.</summary>
public interface ISigningRequestHandler
{
    SigningRequestKind Kind { get; }

    /// <summary>
    /// Re-checks every rule against the database and either returns a refusal without writing anything, or makes
    /// its writes through the context and returns a completed outcome. Exceptions mark the request Failed.
    /// </summary>
    Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken);
}
