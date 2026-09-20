using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>The endpoint a remote background window is for, with what stops a session from starting.</summary>
/// <param name="Problem">Why a session cannot start now, with the next step; null when it can.</param>
public sealed record RemoteTarget(Guid EndpointId, string Hostname, string? ClientCode, string OsPlatform, string OsName, bool WatchdogOnline,
    string WatchdogVersion, string? Problem);

/// <summary>The endpoint a remote control window is for (0.3.0 step 3), with the sessions it can show (Linux: the screen only, step 6).</summary>
/// <param name="Sessions">The console first, then the remote sessions of signed-in users as the agent last reported them.</param>
/// <param name="SessionsReportedAt">When the agent last reported the signed-in users; null when it never did.</param>
/// <param name="Running">Remote control sessions that run on the endpoint now (0.3.0 step 4); opening one on the same Windows session joins it.</param>
/// <param name="Problem">Why a session cannot start now, with the next step; null when it can.</param>
public sealed record RemoteControlTarget(Guid EndpointId, string Hostname, string? ClientCode, string OsPlatform, string OsName, EndpointClass Class,
    IReadOnlyList<RemoteWindowsSession> Sessions, DateTime? SessionsReportedAt, IReadOnlyList<RunningRemoteControl> Running, string? Problem);

/// <summary>A remote control session that runs now, with the technicians in it (0.3.0 step 4).</summary>
public sealed record RunningRemoteControl(Guid SessionId, int WindowsSessionId, IReadOnlyList<string> Technicians, DateTime? StartedAt);

/// <summary>What the browser needs to open the relay: the signed token and the keys the endpoint may sign its session key with.</summary>
/// <param name="CertificateFingerprints">
/// Lowercase hex SHA-256 of the public key of every valid certificate of the serving service. The endpoint sends its certificate key over
/// the relay; the browser accepts it only when it has one of these fingerprints, so the trust comes from the instance, not the relay.
/// </param>
public sealed record RemoteTicket(Guid SessionId, Guid ParticipantId, string Token, string Signature, string KeyId, IReadOnlyList<string> CertificateFingerprints);

/// <summary>
/// Remote sessions in web (0.3.0, ARCHITECTURE.md §4 Remote session): opening a remote background session (served by the watchdog) or a
/// remote control session (served by the agent, Windows) writes the session, the technician's participant and a signing request, and
/// waits for fleeto-signer to sign the single-use token. Web checks what it can for
/// a clear answer; the signer, the gateway and the endpoint decide again.
/// </summary>
public sealed class RemoteSessionService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Session requests one technician may make a minute (security review of 0.3.0 step 7): every request writes rows and uses the signer's
    /// budget for the whole instance, so one technician (or a stolen browser session) cannot hold remote sessions up for everyone.
    /// </summary>
    public const int RequestsPerUserPerMinute = 20;

    private readonly System.Threading.RateLimiting.PartitionedRateLimiter<Guid> _requestLimiter =
        System.Threading.RateLimiting.PartitionedRateLimiter.Create<Guid, Guid>(user => System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            user, _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = RequestsPerUserPerMinute, Window = TimeSpan.FromMinutes(1), QueueLimit = 0
            }));

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<RemoteSessionService> _logger;

    public RemoteSessionService(IFleetoDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, TimeProvider time,
        ILogger<RemoteSessionService> logger)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>The endpoint for the remote background window; null when the caller may not open one or the endpoint is not visible.</summary>
    public async Task<RemoteTarget?> GetBackgroundTargetAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return null;
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new
            {
                e.Id, e.Hostname, e.Tier, e.Source, e.OsPlatform, e.OsName, e.WatchdogOnline, e.WatchdogVersion,
                ClientCode = db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Code).FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var problem = TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed
            ? "Remote background is only available on managed endpoints. Switch the endpoint to managed first."
            : endpoint.Source != EndpointSource.Agent || endpoint.OsPlatform is not ("windows" or "linux")
                ? "Remote background runs on Windows and Linux endpoints with a Fleeto agent."
            : !RemoteSessionRules.WatchdogSupportsRemoteBackground(endpoint.WatchdogVersion)
                ? $"The watchdog of this endpoint runs {(string.IsNullOrEmpty(endpoint.WatchdogVersion) ? "no known version" : "Fleeto " + endpoint.WatchdogVersion)}. Remote background needs Fleeto 0.3.0 or later; the agent updates it with its update ring."
            : !endpoint.WatchdogOnline
                ? "The watchdog of this endpoint is offline. Remote background starts when the endpoint and its watchdog are online."
            : null;
        return new RemoteTarget(endpoint.Id, endpoint.Hostname, endpoint.ClientCode, endpoint.OsPlatform, endpoint.OsName, endpoint.WatchdogOnline,
            endpoint.WatchdogVersion, problem);
    }

    /// <summary>The endpoint for the remote control window; null when the caller may not open one or the endpoint is not visible.</summary>
    public async Task<RemoteControlTarget?> GetControlTargetAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return null;
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new
            {
                e.Id, e.Hostname, e.Tier, e.Source, e.OsPlatform, e.OsName, e.IsOnline, e.AgentVersion, e.DetectedClass, e.ClassOverride,
                e.SignedInUsersJson, e.SignedInUsersAt,
                ClientCode = db.Clients.Where(c => c.Id == e.ClientId).Select(c => c.Code).FirstOrDefault()
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return null;
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var problem = TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed
            ? "Remote control is only available on managed endpoints. Switch the endpoint to managed first."
            : endpoint.Source != EndpointSource.Agent || !RemoteSessionRules.PlatformSupportsRemoteControl(endpoint.OsPlatform)
                ? "Remote control runs on Windows and Linux endpoints with a Fleeto agent."
            : !RemoteSessionRules.AgentSupportsRemoteControl(endpoint.AgentVersion, endpoint.OsPlatform)
                ? $"The agent of this endpoint runs {(string.IsNullOrEmpty(endpoint.AgentVersion) ? "no known version" : "Fleeto " + endpoint.AgentVersion)}. Remote control {(endpoint.OsPlatform == "linux" ? "on Linux needs Fleeto " + RemoteSessionRules.MinimumLinuxAgentVersion : "needs Fleeto 0.3.0")} or later; the agent updates with its update ring."
            : !endpoint.IsOnline
                ? "The agent of this endpoint is offline. Remote control starts when the endpoint is online."
            : null;
        var sessions = RemoteSessionRules.ControlSessions(endpoint.OsPlatform, SignedInUserRules.Parse(endpoint.SignedInUsersJson));
        var running = await RunningControlSessionsAsync(db, endpoint.Id, cancellationToken);
        return new RemoteControlTarget(endpoint.Id, endpoint.Hostname, endpoint.ClientCode, endpoint.OsPlatform, endpoint.OsName, endpoint.ClassOverride ?? endpoint.DetectedClass,
            sessions, endpoint.SignedInUsersAt, running, problem);
    }

    /// <summary>The remote control sessions of an endpoint that have not ended and still have a technician in them or on the way.</summary>
    private static async Task<IReadOnlyList<RunningRemoteControl>> RunningControlSessionsAsync(FleetoDbContext db, Guid endpointId,
        CancellationToken cancellationToken)
    {
        var active = db.RemoteSessionParticipants.Where(p =>
            p.State != RemoteParticipantState.Ended && p.State != RemoteParticipantState.Refused && p.State != RemoteParticipantState.Failed);
        var rows = await db.RemoteSessions.AsNoTracking()
            .Where(s => s.EndpointId == endpointId && s.Kind == RemoteSessionKind.RemoteControl && s.EndedAt == null && active.Any(p => p.SessionId == s.Id))
            .OrderBy(s => s.CreatedAt)
            .Select(s => new
            {
                s.Id, s.WindowsSessionId, s.StartedAt,
                Technicians = active.Where(p => p.SessionId == s.Id).OrderBy(p => p.CreatedAt).Select(p => p.UserName).ToList()
            })
            .ToListAsync(cancellationToken);
        return rows.Select(r => new RunningRemoteControl(r.Id, r.WindowsSessionId ?? RemoteSessionRules.ConsoleWindowsSession,
            r.Technicians.Distinct(StringComparer.Ordinal).ToList(), r.StartedAt)).ToList();
    }

    /// <summary>
    /// Opens a remote background session for the caller with the browser's ephemeral public key and waits up to 20 seconds for the signed
    /// token. The session and the request are audited whether or not the signer signs.
    /// </summary>
    public async Task<ServiceResult<RemoteTicket>> OpenBackgroundAsync(Caller caller, Guid endpointId, byte[] browserPublicKey, string? reason,
        CancellationToken cancellationToken = default)
    {
        var problem = CheckRequest(caller, browserPublicKey, ref reason);
        if (problem is not null)
        {
            return problem;
        }

        var target = await GetBackgroundTargetAsync(caller, endpointId, cancellationToken);
        if (target is null)
        {
            return ServiceResult<RemoteTicket>.NotFound("endpoint");
        }

        return target.Problem is not null
            ? ServiceResult<RemoteTicket>.Fail(target.Problem)
            : await OpenAsync(caller, endpointId, target.Hostname, RemoteSessionKind.RemoteBackground, AgentComponent.Watchdog, null, browserPublicKey,
                reason, cancellationToken);
    }

    /// <summary>
    /// Opens a remote control session (0.3.0 step 3) on the chosen Windows session: 0 for the console, otherwise a remote session the agent
    /// reported. When a remote control session already runs on that Windows session, the technician joins it with their own participant and
    /// token (0.3.0 step 4, decided 2026-09-17: at most one session per Windows session). Waits up to 20 seconds for the signed token, like
    /// <see cref="OpenBackgroundAsync"/>.
    /// </summary>
    public async Task<ServiceResult<RemoteTicket>> OpenControlAsync(Caller caller, Guid endpointId, byte[] browserPublicKey, string? reason,
        int windowsSessionId, CancellationToken cancellationToken = default)
    {
        var problem = CheckRequest(caller, browserPublicKey, ref reason);
        if (problem is not null)
        {
            return problem;
        }

        var target = await GetControlTargetAsync(caller, endpointId, cancellationToken);
        if (target is null)
        {
            return ServiceResult<RemoteTicket>.NotFound("endpoint");
        }

        if (target.Problem is not null)
        {
            return ServiceResult<RemoteTicket>.Fail(target.Problem);
        }

        if (target.Sessions.All(s => s.Id != windowsSessionId))
        {
            return ServiceResult<RemoteTicket>.Fail(target.OsPlatform == "linux"
                ? "Remote control on Linux shows the screen of the endpoint only."
                : "That Windows session is no longer signed in. Choose the console or another session.");
        }

        var running = target.Running.FirstOrDefault(r => r.WindowsSessionId == windowsSessionId);
        return await OpenAsync(caller, endpointId, target.Hostname, RemoteSessionKind.RemoteControl, AgentComponent.Agent, windowsSessionId,
            browserPublicKey, reason, cancellationToken, running?.SessionId);
    }

    private static ServiceResult<RemoteTicket>? CheckRequest(Caller caller, byte[] browserPublicKey, ref string? reason)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<RemoteTicket>.Forbidden();
        }

        if (!RemoteSessionRules.IsValidPublicKey(browserPublicKey))
        {
            return ServiceResult<RemoteTicket>.Fail("The browser did not create a valid session key. Close this window and open the session again.");
        }

        reason = ServiceSupport.Clean(reason);
        return reason is { Length: > RemoteSessionRules.MaxReasonLength }
            ? ServiceResult<RemoteTicket>.Fail($"The reason can be at most {RemoteSessionRules.MaxReasonLength} characters.")
            : null;
    }

    /// <summary>Takes one of the user's session requests of this minute; false when they used them all.</summary>
    internal bool TryTakeRequest(Guid userId)
    {
        using var lease = _requestLimiter.AttemptAcquire(userId);
        return lease.IsAcquired;
    }

    private async Task<ServiceResult<RemoteTicket>> OpenAsync(Caller caller, Guid endpointId, string hostname, RemoteSessionKind kind,
        AgentComponent component, int? windowsSessionId, byte[] browserPublicKey, string? reason, CancellationToken cancellationToken,
        Guid? joinSessionId = null)
    {
        if (!TryTakeRequest(caller.UserId))
        {
            return ServiceResult<RemoteTicket>.Fail("You opened many remote sessions in the last minute. Wait a minute and try again.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var clientId = await db.Endpoints.Where(e => e.Id == endpointId).Select(e => e.ClientId).SingleAsync(cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        var name = caller.Name.Length > 200 ? caller.Name[..200] : caller.Name;
        var joined = joinSessionId is { } joinId
            ? await db.RemoteSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == joinId && s.EndpointId == endpointId && s.EndedAt == null, cancellationToken)
            : null;
        var session = joined ?? new RemoteSession
        {
            Id = Guid.NewGuid(), ClientId = clientId, EndpointId = endpointId, Kind = kind, Component = component, WindowsSessionId = windowsSessionId,
            StartedByUserId = caller.UserId, StartedByName = name, Reason = reason, CreatedAt = now
        };
        var participant = new RemoteSessionParticipant
        {
            Id = Guid.NewGuid(), SessionId = session.Id, ClientId = clientId, EndpointId = endpointId, UserId = caller.UserId, UserName = name,
            Reason = reason, BrowserPublicKey = browserPublicKey, State = RemoteParticipantState.Requested, CreatedAt = now
        };
        var request = new SigningRequest
        {
            // What is to be signed, where no other container can change it (security review of 0.3.0 step 7).
            Id = Guid.NewGuid(), ClientId = clientId, Kind = SigningRequestKind.RemoteSessionToken, SubjectId = participant.Id,
            Payload = RemoteSessionBinding.Of(session, participant).ToPayload(),
            RequestedBy = "web:" + (caller.IpAddress ?? "unknown"), CreatedAt = now
        };
        if (joined is null)
        {
            db.RemoteSessions.Add(session);
        }

        db.RemoteSessionParticipants.Add(participant);
        db.SigningRequests.Add(request);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.RemoteSessionRequested, "RemoteSession", session.Id.ToString(), clientId, new
        {
            Hostname = hostname, EndpointId = endpointId, ParticipantId = participant.Id, Kind = session.Kind.ToString(), WindowsSessionId = windowsSessionId,
            Reason = reason, JoinsRunningSession = joined is not null
        }), now));
        await db.SaveChangesAsync(cancellationToken);

        var outcome = await WaitForTokenAsync(caller, participant.Id, request.Id, cancellationToken);
        if (outcome.Problem is not null)
        {
            return ServiceResult<RemoteTicket>.Fail(outcome.Problem);
        }

        var signed = outcome.Participant!;
        var keys = await CertificateKeysAsync(db, endpointId, component, cancellationToken);
        if (keys.Count == 0)
        {
            return ServiceResult<RemoteTicket>.Fail(component == AgentComponent.Watchdog
                ? "The watchdog of this endpoint has no valid certificate. The agent requests a new one; try again in a few minutes."
                : "The agent of this endpoint has no valid certificate. Check its certificate in the endpoint detail, then try again.");
        }

        return ServiceResult<RemoteTicket>.Ok(new RemoteTicket(session.Id, participant.Id, Convert.ToBase64String(signed.TokenPayload!),
            Convert.ToBase64String(signed.TokenSignature!), signed.SigningKeyId!, keys));
    }

    private sealed record TokenOutcome(RemoteSessionParticipant? Participant, string? Problem);

    private async Task<TokenOutcome> WaitForTokenAsync(Caller caller, Guid participantId, Guid requestId, CancellationToken cancellationToken)
    {
        using var signal = new SemaphoreSlim(0);
        using var subscription = _bus.Subscribe(NotificationChannels.SigningResults, (payload, _) =>
        {
            if (payload == requestId.ToString() || payload == NotificationBusEvents.Resync)
            {
                try
                {
                    signal.Release();
                }
                catch (ObjectDisposedException)
                {
                }
            }

            return Task.CompletedTask;
        });

        var deadline = _time.GetUtcNow() + RemoteSessionRules.SigningWait;
        while (true)
        {
            await using (var db = _dbFactory.Create(caller.Scope))
            {
                var participant = await db.RemoteSessionParticipants.AsNoTracking().SingleOrDefaultAsync(p => p.Id == participantId, cancellationToken);
                switch (participant?.State)
                {
                    case RemoteParticipantState.Signed when participant.TokenPayload is not null && participant.TokenSignature is not null:
                        return new TokenOutcome(participant, null);
                    case RemoteParticipantState.Refused:
                        return new TokenOutcome(null, participant.EndReason ?? "The signer refused the session.");
                    case null:
                        return new TokenOutcome(null, "The remote session no longer exists. Open it again.");
                }

                var request = await db.SigningRequests.AsNoTracking().Where(r => r.Id == requestId).Select(r => new { r.State, r.RefusalReason })
                    .SingleOrDefaultAsync(cancellationToken);
                if (request is { State: SigningRequestState.Refused or SigningRequestState.Failed })
                {
                    return new TokenOutcome(null, request.RefusalReason ?? "The signer could not sign the session. Try again.");
                }
            }

            var remaining = deadline - _time.GetUtcNow();
            if (remaining <= TimeSpan.Zero)
            {
                break;
            }

            await signal.WaitAsync(remaining < PollInterval ? remaining : PollInterval, cancellationToken);
        }

        // Give up, so a late signature is never used: the signer only signs a participant that is still requested.
        await using (var db = _dbFactory.Create(caller.Scope))
        {
            var given = await db.RemoteSessionParticipants.Where(p => p.Id == participantId && p.State == RemoteParticipantState.Requested)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.State, RemoteParticipantState.Failed)
                    .SetProperty(p => p.EndReason, "The signer did not answer in time.")
                    .SetProperty(p => p.EndedAt, _time.GetUtcNow().UtcDateTime), cancellationToken);
            if (given == 0)
            {
                // Signed in the last moment: use it after all.
                var participant = await db.RemoteSessionParticipants.AsNoTracking().SingleAsync(p => p.Id == participantId, cancellationToken);
                if (participant is { State: RemoteParticipantState.Signed, TokenPayload: not null, TokenSignature: not null })
                {
                    return new TokenOutcome(participant, null);
                }
            }
        }

        _logger.LogWarning("Remote session participant {ParticipantId}: fleeto-signer did not sign the token within {Wait}", participantId,
            RemoteSessionRules.SigningWait);
        return new TokenOutcome(null, "The signer did not answer in time. Check that fleeto-signer is running, then open the session again.");
    }

    /// <summary>The public key fingerprints of the valid certificates of a service; the endpoint signs its session key with one of those keys.</summary>
    private async Task<IReadOnlyList<string>> CertificateKeysAsync(FleetoDbContext db, Guid endpointId, AgentComponent component, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        return await db.AgentCertificates.AsNoTracking()
            .Where(c => c.EndpointId == endpointId && c.Role == component && c.RevokedAt == null && c.ExpiresAt > now)
            .OrderByDescending(c => c.IssuedAt)
            .Select(c => c.PublicKeyFingerprint)
            .Distinct()
            .Take(5)
            .ToListAsync(cancellationToken);
    }
}
