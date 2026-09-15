using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Keys;
using Fleeto.Signer.Processing;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Signer.Handlers;

/// <summary>
/// Recovery of an expired certificate (0.2.0): an agent that was offline past its certificate's end date renews it with that
/// certificate. The gateway accepted the expired certificate for this request only; the signer does not trust that and checks
/// on its own: the key of the request belongs to the endpoint's most recently issued certificate, that certificate was never
/// revoked, it has expired, and no longer than <see cref="ProtocolLimits.RecoveryGrace"/> ago. Revoking an endpoint therefore
/// still stops a lost or stolen device, and an older copy of the identity can never come back after a newer certificate.
/// </summary>
public sealed class AgentRecoveryHandler : ISigningRequestHandler
{
    public const string MissingEndpointReason = AgentRenewalHandler.MissingEndpointReason;
    public const string MalformedReason = AgentRenewalHandler.MalformedReason;
    public const string InvalidCsrReason = AgentRenewalHandler.InvalidCsrReason;
    public const string NotRecoverableReason =
        "This agent certificate cannot be recovered: it was revoked, replaced by a newer certificate, or expired more than a year ago. Enroll the agent again from the endpoint menu.";
    public const string NotExpiredReason =
        "The agent certificate has not expired. The agent renews it over its normal connection.";

    private readonly SignerKeyRing _keyRing;
    private readonly ILogger<AgentRecoveryHandler> _logger;

    public AgentRecoveryHandler(SignerKeyRing keyRing, ILogger<AgentRecoveryHandler> logger)
    {
        _keyRing = keyRing;
        _logger = logger;
    }

    public SigningRequestKind Kind => SigningRequestKind.AgentRecovery;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var request = context.Request;
        var now = context.Now;

        if (request.SubjectId is not { } endpointId || request.Payload.Length is 0 or > ProtocolLimits.MaxEnrollRequestBytes)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        RecoverRequest recover;
        try
        {
            recover = RecoverRequest.Parser.ParseFrom(request.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        var csrDer = recover.CsrDer.ToByteArray();
        if (csrDer.Length is 0 or > InputText.MaxCsrBytes)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        // Locked: a recovery and a revocation or second recovery for the same endpoint are serialized.
        var endpoints = await db.Endpoints.FromSql($"""
            SELECT * FROM "Endpoints" WHERE "Id" = {endpointId} FOR NO KEY UPDATE
            """).IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            return SigningOutcome.Refused(MissingEndpointReason);
        }

        Endpoint endpoint = endpoints[0];
        if (request.ClientId != endpoint.ClientId)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        string csrKeyFingerprint;
        try
        {
            csrKeyFingerprint = InternalCertificateAuthority.CsrPublicKeyFingerprint(csrDer);
        }
        catch (InvalidOperationException)
        {
            return SigningOutcome.Refused(InvalidCsrReason);
        }

        var latest = await db.AgentCertificates.IgnoreQueryFilters().AsNoTracking()
            // Agent certificates only: a watchdog never recovers, the agent gives it a new certificate (0.2.1).
            .Where(c => c.EndpointId == endpointId && c.Role == AgentComponent.Agent)
            .OrderByDescending(c => c.IssuedAt).ThenByDescending(c => c.ExpiresAt)
            .FirstOrDefaultAsync(cancellationToken);
        if (latest is null || latest.RevokedAt is not null || !SecureCompare.HexEquals(latest.PublicKeyFingerprint, csrKeyFingerprint) ||
            now - latest.ExpiresAt > ProtocolLimits.RecoveryGrace)
        {
            return SigningOutcome.Refused(NotRecoverableReason);
        }

        if (latest.ExpiresAt > now)
        {
            return SigningOutcome.Refused(NotExpiredReason);
        }

        var issued = _keyRing.IssueAgentCertificate(csrDer, endpointId, now);
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpointId,
            Fingerprint = issued.Fingerprint,
            PublicKeyFingerprint = issued.PublicKeyFingerprint,
            SerialNumber = issued.SerialNumber,
            IssuedAt = now,
            ExpiresAt = issued.NotAfter
        });

        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.CertificateRecovered, "Endpoint", endpointId.ToString(),
            endpoint.ClientId, AuditActorType.Agent, endpointId.ToString(), endpoint.Hostname,
            new
            {
                endpoint.Hostname,
                PreviousCertificateFingerprint = latest.Fingerprint,
                ExpiredAt = latest.ExpiresAt,
                CertificateFingerprint = issued.Fingerprint,
                issued.SerialNumber,
                ExpiresAt = issued.NotAfter
            }, SigningRequestProcessor.IpAddressFrom(request.RequestedBy)), now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Recovered the certificate of endpoint {EndpointId}, expired since {ExpiredAt:yyyy-MM-dd}, until {ExpiresAt:yyyy-MM-dd}",
            endpointId, latest.ExpiresAt, issued.NotAfter);
        return SigningOutcome.Completed(issued.CertificateDer);
    }
}
