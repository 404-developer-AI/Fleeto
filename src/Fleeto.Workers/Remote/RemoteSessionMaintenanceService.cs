using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Workers.Remote;

/// <summary>
/// Ends remote session rows nobody will finish (0.3.0), every minute: a participant that was requested or signed but never connected
/// within 5 minutes (the browser was closed, the signer was down, the token expired), and a session without an active participant. Claims of
/// Fleeto Connect (0.8.0) are removed a day after they were made: what they led to is in the session and the audit log. A
/// connected participant is ended only by the gateway, which resets them all when it starts. Every statement is conditional on the state it
/// changes, so it never overwrites what the gateway stored meanwhile.
/// </summary>
public sealed class RemoteSessionMaintenanceService : WorkerLoop
{
    public const string NotConnectedReason = "The session was not connected in time.";
    public const string NoParticipantReason = "Nobody is connected to the session any more.";

    /// <summary>How long a claim of Fleeto Connect is kept.</summary>
    public static readonly TimeSpan ClaimRetention = TimeSpan.FromDays(1);

    private const string EndIdleSessionsSql = """
        UPDATE "RemoteSessions" r SET "EndedAt" = @now, "EndReason" = @reason
        WHERE r."EndedAt" IS NULL AND r."CreatedAt" < @cutoff
          AND NOT EXISTS (SELECT 1 FROM "RemoteSessionParticipants" p
                          WHERE p."SessionId" = r."Id" AND p."State" IN ('Requested', 'Signed', 'Connecting', 'Connected'))
        RETURNING r."Id", r."ClientId", r."EndpointId", r."Kind", r."StartedAt"
        """;

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

        // A session that had started and ends here is audited as ended (0.8.0), like the gateway does when the last technician leaves.
        // One statement ends the sessions and returns them, so a session the gateway ended meanwhile is neither ended nor audited twice.
        var ended = await db.Database.SqlQueryRaw<EndedSession>(EndIdleSessionsSql,
                new NpgsqlParameter<DateTime>("now", now), new NpgsqlParameter<DateTime>("cutoff", cutoff), new NpgsqlParameter<string>("reason", NoParticipantReason))
            .ToListAsync(cancellationToken);
        changed += ended.Count;
        foreach (var session in ended.Where(r => r.StartedAt is not null))
        {
            db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.RemoteSessionEnded, "RemoteSession", session.Id.ToString(),
                session.ClientId, AuditActorType.System, "fleeto-workers", "fleeto-workers", new
                {
                    session.EndpointId, session.Kind, Reason = NoParticipantReason,
                    DurationSeconds = (long)Math.Max(0, (now - session.StartedAt!.Value).TotalSeconds)
                }), now));
        }

        await db.SaveChangesAsync(cancellationToken);

        // A claim is used within 50 seconds or never; after a day it holds nothing anybody reads.
        changed += await db.RemoteSessionClaims.Where(c => c.CreatedAt < now - ClaimRetention).ExecuteDeleteAsync(cancellationToken);

        if (changed > 0)
        {
            Logger.LogInformation("Remote session maintenance ended {Count} row(s)", changed);
        }

        return changed;
    }

    private sealed class EndedSession
    {
        public Guid Id { get; set; }
        public Guid ClientId { get; set; }
        public Guid EndpointId { get; set; }
        public string Kind { get; set; } = string.Empty;
        public DateTime? StartedAt { get; set; }
    }
}
