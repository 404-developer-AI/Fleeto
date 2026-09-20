using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Signer.Keys;
using Fleeto.Signer.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Signer.Handlers;

/// <summary>
/// The certificate of the watchdog of an endpoint (0.2.1). Requested by the gateway for a live agent session: the agent generated a
/// separate key for its watchdog and sends its CSR. The endpoint must still have a valid agent certificate, the watchdog key must differ
/// from every agent key of the endpoint, and at most <see cref="MaxPerDay"/> watchdog certificates are issued per endpoint per 24 hours.
/// A new watchdog certificate revokes the earlier ones, so one watchdog identity exists per endpoint.
/// </summary>
public sealed class WatchdogCertificateHandler : ISigningRequestHandler
{
    public const string RequesterName = "gateway";
    public const int MaxPerDay = 3;

    public const string WrongRequesterReason = "Only the gateway can request a watchdog certificate. Check the component that sent the request.";
    public const string MalformedReason = "The watchdog certificate request is malformed. Check that the gateway and signer run the same version.";
    public const string MissingEndpointReason = "The endpoint no longer exists. Enroll the agent again with a new enrollment token.";
    public const string InvalidCsrReason =
        "The certificate signing request is invalid: it must use an ECDSA P-256 key and be signed with that key. The agent will retry later.";
    public const string NoAgentCertificateReason =
        "The endpoint has no valid agent certificate, so it cannot get a watchdog certificate. Enroll the agent again with a new enrollment token.";
    public const string SameKeyReason = "The watchdog must have its own key; this key belongs to the agent. The agent will retry with a new key.";
    public const string TooManyReason = "Too many watchdog certificates were issued for this endpoint in the last 24 hours. The agent retries later.";
    public const string NotVouchedReason =
        "The agent of this endpoint did not sign this watchdog request with its certificate key. Update the agent to 0.3.0-alpha.17 or later; it requests the watchdog certificate again.";

    private readonly SignerKeyRing _keyRing;
    private readonly ILogger<WatchdogCertificateHandler> _logger;

    public WatchdogCertificateHandler(SignerKeyRing keyRing, ILogger<WatchdogCertificateHandler> logger)
    {
        _keyRing = keyRing;
        _logger = logger;
    }

    public SigningRequestKind Kind => SigningRequestKind.WatchdogCertificate;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var request = context.Request;
        var now = context.Now;

        if (!string.Equals(request.RequestedBy, RequesterName, StringComparison.Ordinal))
        {
            return SigningOutcome.Refused(WrongRequesterReason);
        }

        if (request.SubjectId is not { } endpointId || request.Payload.Length is 0 or > InputText.MaxCsrBytes + 2048)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        // The payload is the agent's request with the key of the agent certificate its connection used (0.3.0 step 7).
        Protocol.Agent.V1.WatchdogCertificateRequest vouched;
        try
        {
            vouched = Protocol.Agent.V1.WatchdogCertificateRequest.Parser.ParseFrom(request.Payload);
        }
        catch (Google.Protobuf.InvalidProtocolBufferException)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        var csr = vouched.CsrDer.ToByteArray();
        if (csr.Length is 0 or > InputText.MaxCsrBytes)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        // Locked: two requests for the same endpoint must not both pass the daily limit or both stay unrevoked.
        var endpoints = await db.Endpoints.FromSql($"""
            SELECT * FROM "Endpoints" WHERE "Id" = {endpointId} FOR NO KEY UPDATE
            """).IgnoreQueryFilters().AsNoTracking().ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            return SigningOutcome.Refused(MissingEndpointReason);
        }

        Endpoint endpoint = endpoints[0];
        if (request.ClientId != endpoint.ClientId || endpoint.Source != EndpointSource.Agent)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        string csrKeyFingerprint;
        try
        {
            csrKeyFingerprint = InternalCertificateAuthority.CsrPublicKeyFingerprint(csr);
        }
        catch (InvalidOperationException)
        {
            return SigningOutcome.Refused(InvalidCsrReason);
        }

        var certificates = await db.AgentCertificates.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EndpointId == endpointId)
            .ToListAsync(cancellationToken);
        if (!certificates.Any(c => c.Role == AgentComponent.Agent && c.RevokedAt is null && c.ExpiresAt > now))
        {
            return SigningOutcome.Refused(NoAgentCertificateReason);
        }

        // The agent vouches for its watchdog with the key of a current agent certificate of this endpoint: a request the agent did not sign
        // (a compromised gateway asking for a certificate for its own key) is never issued (security review of 0.3.0 step 7).
        var vouchedBy = InternalCertificateAuthority.VerifyWatchdogVouch(vouched.AgentPublicKey.ToByteArray(), csr, vouched.AgentSignature.ToByteArray());
        if (vouchedBy is null || !certificates.Any(c =>
                c.Role == AgentComponent.Agent && c.RevokedAt is null && c.ExpiresAt > now && SecureCompare.HexEquals(c.PublicKeyFingerprint, vouchedBy)))
        {
            _logger.LogWarning("Endpoint {EndpointId}: watchdog certificate refused, the request is not signed by a current agent certificate key", endpointId);
            return SigningOutcome.Refused(NotVouchedReason);
        }

        if (certificates.Any(c => c.Role == AgentComponent.Agent && SecureCompare.HexEquals(c.PublicKeyFingerprint, csrKeyFingerprint)))
        {
            return SigningOutcome.Refused(SameKeyReason);
        }

        if (certificates.Count(c => c.Role == AgentComponent.Watchdog && c.IssuedAt > now - TimeSpan.FromHours(24)) >= MaxPerDay)
        {
            return SigningOutcome.Refused(TooManyReason);
        }

        var replaced = await db.AgentCertificates.IgnoreQueryFilters()
            .Where(c => c.EndpointId == endpointId && c.Role == AgentComponent.Watchdog && c.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.RevokedAt, now)
                .SetProperty(c => c.RevokedReason, "Replaced by a new watchdog certificate."), cancellationToken);

        var issued = _keyRing.IssueAgentCertificate(csr, endpointId, now);
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpointId,
            Fingerprint = issued.Fingerprint,
            PublicKeyFingerprint = issued.PublicKeyFingerprint,
            SerialNumber = issued.SerialNumber,
            Role = AgentComponent.Watchdog,
            IssuedAt = now,
            ExpiresAt = issued.NotAfter
        });

        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.CertificateIssued, "Endpoint", endpointId.ToString(), endpoint.ClientId,
            AuditActorType.Agent, endpointId.ToString(), endpoint.Hostname,
            new
            {
                endpoint.Hostname,
                Role = nameof(AgentComponent.Watchdog),
                CertificateFingerprint = issued.Fingerprint,
                issued.SerialNumber,
                ExpiresAt = issued.NotAfter,
                ReplacedCertificates = replaced
            }), now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Issued a watchdog certificate for endpoint {EndpointId} until {ExpiresAt:yyyy-MM-dd}; {Replaced} earlier one(s) revoked",
            endpointId, issued.NotAfter, replaced);
        return replaced > 0
            ? SigningOutcome.Completed(issued.CertificateDer, new PendingNotification(NotificationChannels.Revocations, endpointId.ToString()))
            : SigningOutcome.Completed(issued.CertificateDer);
    }
}
