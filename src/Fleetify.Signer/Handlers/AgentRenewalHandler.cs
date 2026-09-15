using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Security;
using Fleetify.Signer.Keys;
using Fleetify.Signer.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Endpoint = Fleetify.Core.Entities.Endpoint;

namespace Fleetify.Signer.Handlers;

/// <summary>
/// Certificate renewal of an agent or a watchdog (0.2.1). The new certificate keeps the key and the role of a current (not revoked, not
/// expired) certificate of the same endpoint, so a revoked or stolen-then-revoked identity can never renew; at most one certificate per
/// endpoint and role per 24 hours.
/// </summary>
public sealed class AgentRenewalHandler : ISigningRequestHandler
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromHours(24);

    public const string MissingEndpointReason =
        "The endpoint no longer exists. Enroll the agent again with a new enrollment token.";
    public const string MalformedReason = "The renewal request is malformed. Check that the gateway and signer run the same version.";
    public const string InvalidCsrReason =
        "The certificate signing request is invalid: it must use an ECDSA P-256 key and be signed with that key. The agent will retry later.";
    public const string NoCurrentCertificateReason =
        "The endpoint has no valid certificate with this key; it may have been revoked or expired. Enroll the agent again with a new enrollment token.";
    public const string TooSoonReason =
        "A certificate was issued for this endpoint less than 24 hours ago. The agent keeps its current certificate and will retry later.";

    private readonly SignerKeyRing _keyRing;
    private readonly ILogger<AgentRenewalHandler> _logger;

    public AgentRenewalHandler(SignerKeyRing keyRing, ILogger<AgentRenewalHandler> logger)
    {
        _keyRing = keyRing;
        _logger = logger;
    }

    public SigningRequestKind Kind => SigningRequestKind.AgentRenewal;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var request = context.Request;
        var now = context.Now;

        if (request.SubjectId is not { } endpointId || request.Payload.Length is 0 or > InputText.MaxCsrBytes)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        // Locked: two renewals for the same endpoint must not both pass the 24-hour check.
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
            csrKeyFingerprint = InternalCertificateAuthority.CsrPublicKeyFingerprint(request.Payload);
        }
        catch (InvalidOperationException)
        {
            return SigningOutcome.Refused(InvalidCsrReason);
        }

        var certificates = await db.AgentCertificates.IgnoreQueryFilters().AsNoTracking()
            .Where(c => c.EndpointId == endpointId)
            .ToListAsync(cancellationToken);

        var current = certificates.FirstOrDefault(c =>
            c.RevokedAt is null && c.ExpiresAt > now && SecureCompare.HexEquals(c.PublicKeyFingerprint, csrKeyFingerprint));
        if (current is null)
        {
            return SigningOutcome.Refused(NoCurrentCertificateReason);
        }

        if (certificates.Any(c => c.Role == current.Role && c.IssuedAt > now - MinimumInterval))
        {
            return SigningOutcome.Refused(TooSoonReason);
        }

        var issued = _keyRing.IssueAgentCertificate(request.Payload, endpointId, now);
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpointId,
            Fingerprint = issued.Fingerprint,
            PublicKeyFingerprint = issued.PublicKeyFingerprint,
            SerialNumber = issued.SerialNumber,
            Role = current.Role,
            IssuedAt = now,
            ExpiresAt = issued.NotAfter
        });

        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.CertificateRenewed, "Endpoint", endpointId.ToString(),
            endpoint.ClientId, AuditActorType.Agent, endpointId.ToString(), endpoint.Hostname,
            new
            {
                endpoint.Hostname,
                Role = current.Role.ToString(),
                PreviousCertificateFingerprint = current.Fingerprint,
                CertificateFingerprint = issued.Fingerprint,
                issued.SerialNumber,
                ExpiresAt = issued.NotAfter
            }), now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Renewed the {Role} certificate of endpoint {EndpointId} until {ExpiresAt:yyyy-MM-dd}", current.Role, endpointId, issued.NotAfter);
        return SigningOutcome.Completed(issued.CertificateDer);
    }
}
