using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Remote;

/// <summary>
/// Ends remote session rows nobody will finish (0.3.0), every minute: a participant that was requested or signed but never connected
/// within 5 minutes (the browser was closed, the signer was down, the token expired), and a session without an active participant. A
/// connected participant is ended only by the gateway, which resets them all when it starts. Every statement is conditional on the state it
/// changes, so it never overwrites what the gateway stored meanwhile.
/// </summary>
public sealed class RemoteSessionMaintenanceService : WorkerLoop
{
    public const string NotConnectedReason = "The session was not connected in time.";
    public const string NoParticipantReason = "Nobody is connected to the session any more.";

    private readonly IFleetoDbContextFactory _dbFactory;

    public RemoteSessionMaintenanceService(IFleetoDbContextFactory dbFactory, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<RemoteSessionMaintenanceService> logger)
        : base("remote-session-maintenance", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await RunAsync(cancellationToken);
        return false;
    }

    /// <summary>Applies both transitions once. Returns the number of rows changed.</summary>
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var cutoff = now - RemoteSessionRules.StaleAfter;
        await using var db = _dbFactory.CreateSystem();

        // Requested or signed but never connected, and claimed by the gateway but never paired (a gateway that lost its database or stopped
        // in between): both end, so no session shows technicians that are not there (security review of 0.3.0 step 7).
        var changed = await db.RemoteSessionParticipants
            .Where(p => ((p.State == RemoteParticipantState.Requested || p.State == RemoteParticipantState.Signed) && p.CreatedAt < cutoff) ||
                        (p.State == RemoteParticipantState.Connecting && p.ConnectingAt < cutoff))
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.State, RemoteParticipantState.Ended)
                .SetProperty(p => p.EndedAt, now)
                .SetProperty(p => p.EndReason, NotConnectedReason), cancellationToken);

        changed += await db.RemoteSessions
            .Where(r => r.EndedAt == null && r.CreatedAt < cutoff && !db.RemoteSessionParticipants.Any(p => p.SessionId == r.Id &&
                (p.State == RemoteParticipantState.Requested || p.State == RemoteParticipantState.Signed ||
                 p.State == RemoteParticipantState.Connecting || p.State == RemoteParticipantState.Connected)))
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.EndedAt, now)
                .SetProperty(r => r.EndReason, NoParticipantReason), cancellationToken);

        if (changed > 0)
        {
            Logger.LogInformation("Remote session maintenance ended {Count} row(s)", changed);
        }

        return changed;
    }
}
