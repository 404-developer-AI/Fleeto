using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Services;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Keys;
using Fleeto.Signer.Processing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Signer.Handlers;

/// <summary>
/// Signs the uninstall command of a deleted endpoint (0.6.0, ARCHITECTURE.md §5). Decided from the database, independently of web:
/// the row still names the endpoint and the user web asked for (the request payload, which nobody but web writes); the endpoint no
/// longer exists, so the command only ever follows a deletion that is in the audit log; the user who deleted it is an admin or
/// technician; the row expires within the waiting period and lists the certificates to recognise. The command can do one thing on
/// the endpoint, remove Fleeto, and is valid until the row expires.
/// </summary>
public sealed class AgentUninstallHandler : ISigningRequestHandler
{
    public const string MissingReason = "The uninstall request no longer exists.";
    public const string BindingReason =
        "The uninstall request was changed after it was made, so it was not signed. Tell an administrator if it happens again.";
    public const string EndpointExistsReason = "The endpoint still exists. Fleeto only uninstalls the agent of an endpoint that was deleted.";
    public const string UserReason = "The user who deleted the endpoint no longer exists, is locked out or has no admin or technician role.";
    public const string ValidityReason = "The uninstall request has no valid waiting period or no certificates to recognise.";

    private readonly SignerKeyRing _keyRing;
    private readonly ILogger<AgentUninstallHandler> _logger;

    public AgentUninstallHandler(SignerKeyRing keyRing, ILogger<AgentUninstallHandler> logger)
    {
        _keyRing = keyRing;
        _logger = logger;
    }

    public SigningRequestKind Kind => SigningRequestKind.AgentUninstall;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var now = context.Now;
        if (context.Request.SubjectId is not { } uninstallId)
        {
            return SigningOutcome.Refused(MissingReason);
        }

        var rows = await db.AgentUninstalls.FromSql($"""SELECT * FROM "AgentUninstalls" WHERE "Id" = {uninstallId} FOR UPDATE""")
            .ToListAsync(cancellationToken);
        if (rows.Count == 0)
        {
            return SigningOutcome.Refused(MissingReason);
        }

        var uninstall = rows[0];
        if (uninstall.SignedAt is not null)
        {
            return SigningOutcome.Completed(null);
        }

        if (!AgentUninstallRules.Binding(uninstall.EndpointId, uninstall.RequestedByUserId).AsSpan().SequenceEqual(context.Request.Payload))
        {
            _logger.LogWarning("Uninstall {UninstallId} refused: the row differs from the request web made", uninstall.Id);
            return SigningOutcome.Refused(BindingReason);
        }

        if (await db.Endpoints.IgnoreQueryFilters().AnyAsync(e => e.Id == uninstall.EndpointId, cancellationToken))
        {
            return SigningOutcome.Refused(EndpointExistsReason);
        }

        if (!await ScriptCheckResolver.UserHasRoleAsync(db, uninstall.RequestedByUserId, now, cancellationToken, FleetoRoleNames.Admin,
                FleetoRoleNames.Technician))
        {
            return SigningOutcome.Refused(UserReason);
        }

        if (uninstall.ExpiresAt <= now || uninstall.ExpiresAt > now + AgentUninstallRules.Lifetime + TimeSpan.FromHours(1) ||
            uninstall.Fingerprints.Count == 0)
        {
            return SigningOutcome.Refused(ValidityReason);
        }

        var payload = new UninstallCommand
        {
            UninstallId = uninstall.Id.ToString("D"),
            InstanceId = _keyRing.InstanceId.ToString("D"),
            EndpointId = uninstall.EndpointId.ToString("D"),
            ValidUntil = Timestamp.FromDateTime(DateTime.SpecifyKind(uninstall.ExpiresAt, DateTimeKind.Utc)),
            RequestedBy = uninstall.RequestedByName
        }.ToByteArray();
        uninstall.Payload = payload;
        uninstall.Signature = _keyRing.Sign(SignatureContexts.Uninstall, payload);
        uninstall.SigningKeyId = _keyRing.SigningKeyId;
        uninstall.SignedAt = now;

        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.AgentUninstallSigned, "Endpoint", uninstall.EndpointId.ToString(),
            uninstall.ClientId, AuditActorType.System, "fleeto-signer", "fleeto-signer",
            new { uninstall.Hostname, uninstall.ExpiresAt, RequestedBy = uninstall.RequestedByName, Certificates = uninstall.Fingerprints.Count }),
            now, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Signed the uninstall command of deleted endpoint {EndpointId}", uninstall.EndpointId);
        // The gateway reloads its allow list on this channel, and with it the deleted endpoints it waits for.
        return SigningOutcome.Completed(null, new PendingNotification(NotificationChannels.Revocations, uninstall.EndpointId.ToString()));
    }

    public async Task<IReadOnlyList<PendingNotification>> OnRefusedAsync(SigningContext context, string reason, CancellationToken cancellationToken)
    {
        if (context.Request.SubjectId is { } uninstallId)
        {
            var text = reason.Length > 500 ? reason[..500] : reason;
            await context.Db.AgentUninstalls.Where(u => u.Id == uninstallId && u.SignedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.RefusalReason, text), cancellationToken);
        }

        return [];
    }
}
