using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Services;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Keys;
using Fleeto.Signer.Processing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using RemoteSessionKind = Fleeto.Core.Entities.RemoteSessionKind;

namespace Fleeto.Signer.Handlers;

/// <summary>
/// Signs the single-use token of one remote session participant (0.3.0, ARCHITECTURE.md §4, Remote session and §5, Remote control).
/// Decided from the database, independently of web: the participant is still waiting for its token and was requested within the last
/// minute; its user exists, has two-factor authentication, is not locked out and is an admin or technician; the endpoint exists in the
/// participant's client and is managed (license included); the browser key is a usable X25519 key. The token carries the effective
/// policy's idle timeout and file size cap, for remote control the consent prompt, banner and clipboard as they apply to the endpoint's
/// class, and is valid for 60 seconds.
/// </summary>
public sealed class RemoteSessionTokenHandler : ISigningRequestHandler
{
    public const string MissingReason = "The remote session request no longer exists. Open the session again.";
    public const string TooOldReason = "The remote session request waited too long for the signer. Open the session again.";
    public const string UserReason =
        "The technician no longer exists, is locked out, has no two-factor authentication or has no admin or technician role. Ask an admin for access.";
    public const string NotManagedReason = "The endpoint is not managed. Switch it to managed before opening a remote session.";
    public const string BrowserKeyReason = "The browser sent an invalid session key. Close the window and open the session again.";
    public const string KindReason = "This kind of remote session is not available yet.";
    public const string PlatformReason = "Remote control runs on Windows and Linux endpoints only.";
    public const string AgentVersionReason =
        "The agent of this endpoint is too old for remote control. It needs Fleeto 0.3.0 or later (on Linux 0.3.0-alpha.16 or later); the agent updates with its update ring.";
    public const string LinuxSessionReason = "Remote control on Linux shows the screen of the endpoint only.";
    public const string BindingReason =
        "The remote session request was changed after it was made, so it was not signed. Open the session again, and tell an administrator if it happens again.";

    private readonly SignerKeyRing _keyRing;
    private readonly LicenseService _licenses;
    private readonly ILogger<RemoteSessionTokenHandler> _logger;

    public RemoteSessionTokenHandler(SignerKeyRing keyRing, LicenseService licenses, ILogger<RemoteSessionTokenHandler> logger)
    {
        _keyRing = keyRing;
        _licenses = licenses;
        _logger = logger;
    }

    public SigningRequestKind Kind => SigningRequestKind.RemoteSessionToken;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var now = context.Now;
        if (context.Request.SubjectId is not { } participantId)
        {
            return SigningOutcome.Refused(MissingReason);
        }

        var participants = await db.RemoteSessionParticipants
            .FromSql($"""SELECT * FROM "RemoteSessionParticipants" WHERE "Id" = {participantId} FOR UPDATE""")
            .IgnoreQueryFilters().ToListAsync(cancellationToken);
        if (participants.Count == 0 || participants[0].ClientId != context.Request.ClientId)
        {
            return SigningOutcome.Refused(MissingReason);
        }

        var participant = participants[0];
        if (participant.State != RemoteParticipantState.Requested)
        {
            // Given up by web (timeout) or handled already: nothing to sign and nothing wrong.
            return SigningOutcome.Completed(null);
        }

        if (now - participant.CreatedAt > RemoteSessionRules.MaxRequestAge || participant.CreatedAt > now + TimeSpan.FromMinutes(1))
        {
            return SigningOutcome.Refused(TooOldReason);
        }

        var session = await db.RemoteSessions.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(s => s.Id == participant.SessionId && s.ClientId == participant.ClientId, cancellationToken);
        if (session is null || session.EndpointId != participant.EndpointId || session.EndedAt is not null)
        {
            return SigningOutcome.Refused(MissingReason);
        }

        // Remote background is served by the watchdog, remote control (Windows, 0.3.0 step 3) by the agent on a chosen Windows session.
        var servedBy = session.Kind switch
        {
            RemoteSessionKind.RemoteBackground when session.Component == AgentComponent.Watchdog && session.WindowsSessionId is null => Component.Watchdog,
            RemoteSessionKind.RemoteControl when session.Component == AgentComponent.Agent && session.WindowsSessionId >= 0 => Component.Agent,
            _ => Component.Unspecified
        };
        if (servedBy == Component.Unspecified)
        {
            return SigningOutcome.Refused(KindReason);
        }

        // The rows must still say what web asked for (security review of 0.3.0 step 7): another container that can update them must never
        // get a token for its own key, another endpoint or another kind of session.
        if (RemoteSessionBinding.FromPayload(context.Request.Payload) is not { } binding || binding != RemoteSessionBinding.Of(session, participant))
        {
            _logger.LogWarning("Remote session token for participant {ParticipantId} refused: the session rows differ from the request web made",
                participant.Id);
            return SigningOutcome.Refused(BindingReason);
        }

        if (!RemoteSessionRules.IsValidPublicKey(participant.BrowserPublicKey))
        {
            return SigningOutcome.Refused(BrowserKeyReason);
        }

        if (!await ScriptCheckResolver.UserHasRoleAsync(db, participant.UserId, now, cancellationToken, FleetoRoleNames.Admin,
                FleetoRoleNames.Technician))
        {
            return SigningOutcome.Refused(UserReason);
        }

        var endpoint = await db.Endpoints.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == participant.EndpointId && e.ClientId == participant.ClientId)
            .Select(e => new { e.Id, e.SiteId, e.Tier, e.Hostname, e.OsPlatform, e.AgentVersion, Class = e.ClassOverride ?? e.DetectedClass })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return SigningOutcome.Refused(MissingReason);
        }

        if (session.Kind == RemoteSessionKind.RemoteControl && !RemoteSessionRules.PlatformSupportsRemoteControl(endpoint.OsPlatform))
        {
            return SigningOutcome.Refused(PlatformReason);
        }

        // Linux shows the console only (0.3.0 step 6, decided 2026-09-19).
        if (session.Kind == RemoteSessionKind.RemoteControl && endpoint.OsPlatform == "linux" &&
            session.WindowsSessionId != RemoteSessionRules.ConsoleWindowsSession)
        {
            return SigningOutcome.Refused(LinuxSessionReason);
        }

        // An older agent would ignore the consent prompt and banner of the policy (0.3.0 step 4), or not serve Linux at all (step 6).
        if (session.Kind == RemoteSessionKind.RemoteControl && !RemoteSessionRules.AgentSupportsRemoteControl(endpoint.AgentVersion, endpoint.OsPlatform))
        {
            return SigningOutcome.Refused(AgentVersionReason);
        }

        // Tier enforcement, layer 2 (the signer): the stored tier and the license must both allow managed behaviour.
        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        if (TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed)
        {
            return SigningOutcome.Refused(NotManagedReason);
        }

        var policy = await db.SitePolicies.IgnoreQueryFilters().AsNoTracking()
                         .Where(l => l.SiteId == endpoint.SiteId)
                         .Select(l => l.Policy)
                         .FirstOrDefaultAsync(cancellationToken)
                     ?? await db.Policies.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, cancellationToken);
        var idleMinutes = RemoteSessionRules.IdleTimeoutMinutes(policy?.RemoteIdleTimeoutMinutes ?? RemoteSessionRules.DefaultIdleTimeoutMinutes);
        var maxFileBytes = RemoteSessionRules.MaxFileBytes(policy?.RemoteMaxFileBytes ?? RemoteSessionRules.DefaultMaxFileBytes);
        // Consent and banner follow the policy on workstations only; a server never asks and shows no banner (0.3.0 step 4).
        var control = session.Kind == RemoteSessionKind.RemoteControl
            ? RemoteSessionRules.EffectiveControlRules(endpoint.Class, policy?.RemoteConsentRequired ?? false,
                policy?.RemoteConsentTimeoutSeconds ?? RemoteSessionRules.DefaultConsentTimeoutSeconds, policy?.RemoteBannerVisible ?? true,
                policy?.RemoteClipboardEnabled ?? true)
            : null;

        var validUntil = now + RemoteSessionRules.TokenValidity;
        var payload = new RemoteSessionToken
        {
            ParticipantId = participant.Id.ToString("D"),
            SessionId = session.Id.ToString("D"),
            InstanceId = _keyRing.InstanceId.ToString("D"),
            EndpointId = endpoint.Id.ToString("D"),
            Kind = session.Kind == RemoteSessionKind.RemoteControl
                ? Protocol.Agent.V1.RemoteSessionKind.RemoteControl
                : Protocol.Agent.V1.RemoteSessionKind.RemoteBackground,
            Component = servedBy,
            WindowsSessionId = (uint)(session.WindowsSessionId ?? 0),
            TechnicianId = participant.UserId.ToString("D"),
            TechnicianName = participant.UserName,
            BrowserPublicKey = ByteString.CopyFrom(participant.BrowserPublicKey),
            IssuedAt = Timestamp.FromDateTime(DateTime.SpecifyKind(now, DateTimeKind.Utc)),
            ValidUntil = Timestamp.FromDateTime(DateTime.SpecifyKind(validUntil, DateTimeKind.Utc)),
            IdleTimeoutSeconds = (uint)(idleMinutes * 60),
            MaxFileBytes = (ulong)maxFileBytes,
            ConsentRequired = control?.ConsentRequired ?? false,
            ConsentTimeoutSeconds = (uint)(control?.ConsentTimeoutSeconds ?? 0),
            BannerVisible = control?.BannerVisible ?? false,
            ClipboardEnabled = control?.ClipboardEnabled ?? false
        }.ToByteArray();

        participant.TokenPayload = payload;
        participant.TokenSignature = _keyRing.Sign(SignatureContexts.RemoteSession, payload);
        participant.SigningKeyId = _keyRing.SigningKeyId;
        participant.SignedAt = now;
        participant.ValidUntil = validUntil;
        participant.State = RemoteParticipantState.Signed;

        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.RemoteSessionSigned, "RemoteSession", session.Id.ToString(), session.ClientId,
            AuditActorType.System, SignerAudit.ActorName, SignerAudit.ActorName,
            new
            {
                endpoint.Hostname,
                EndpointId = endpoint.Id,
                ParticipantId = participant.Id,
                Kind = session.Kind.ToString(),
                Technician = participant.UserName,
                session.WindowsSessionId,
                validUntil,
                IdleTimeoutMinutes = idleMinutes,
                ConsentRequired = control?.ConsentRequired,
                BannerVisible = control?.BannerVisible,
                ClipboardEnabled = control?.ClipboardEnabled
            }), now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Signed remote session token {ParticipantId} ({Kind}) for endpoint {EndpointId}", participant.Id, session.Kind, endpoint.Id);
        return SigningOutcome.Completed(null);
    }

    /// <summary>Marks the participant refused with the signer's reason, after the handler's own writes were rolled back.</summary>
    public async Task<IReadOnlyList<PendingNotification>> OnRefusedAsync(SigningContext context, string reason, CancellationToken cancellationToken)
    {
        if (context.Request.SubjectId is not { } participantId)
        {
            return [];
        }

        var reasonText = reason.Length > 500 ? reason[..500] : reason;
        await context.Db.RemoteSessionParticipants.IgnoreQueryFilters()
            .Where(p => p.Id == participantId && p.ClientId == context.Request.ClientId && p.State == RemoteParticipantState.Requested)
            .ExecuteUpdateAsync(s => s
                .SetProperty(p => p.State, RemoteParticipantState.Refused)
                .SetProperty(p => p.EndReason, reasonText)
                .SetProperty(p => p.EndedAt, context.Now), cancellationToken);
        return [];
    }
}
