using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Security;
using Fleetify.Signer.Keys;
using Fleetify.Signer.Processing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleetify.Signer.Handlers;

/// <summary>
/// The gateway's 24-hour server certificate for the agent host name. The host name comes from the instance
/// settings, never from the CSR. Renewed every 12 hours, so it is logged but audited at most once a day.
/// </summary>
public sealed class GatewayCertificateHandler : ISigningRequestHandler
{
    public const string RequesterName = "gateway";

    public const string WrongRequesterReason = "Only the gateway can request a gateway certificate. Check the component that sent the request.";
    public const string MalformedReason = "The gateway certificate request is malformed. Check that the gateway and signer run the same version.";
    public const string InvalidCsrReason =
        "The certificate signing request is invalid: it must use an ECDSA P-256 key and be signed with that key. The gateway will retry.";

    private readonly SignerKeyRing _keyRing;
    private readonly ILogger<GatewayCertificateHandler> _logger;
    private DateOnly? _lastAuditedDay;

    public GatewayCertificateHandler(SignerKeyRing keyRing, ILogger<GatewayCertificateHandler> logger)
    {
        _keyRing = keyRing;
        _logger = logger;
    }

    public SigningRequestKind Kind => SigningRequestKind.GatewayCertificate;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        if (!string.Equals(request.RequestedBy, RequesterName, StringComparison.Ordinal))
        {
            return SigningOutcome.Refused(WrongRequesterReason);
        }

        if (request.ClientId is not null || request.SubjectId is not null || request.Payload.Length is 0 or > InputText.MaxCsrBytes)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        try
        {
            _ = InternalCertificateAuthority.CsrPublicKeyFingerprint(request.Payload);
        }
        catch (InvalidOperationException)
        {
            return SigningOutcome.Refused(InvalidCsrReason);
        }

        var hostName = await context.Db.InstanceSettings.AsNoTracking().Select(s => s.AgentHostName).SingleAsync(cancellationToken);
        var issued = _keyRing.IssueGatewayCertificate(request.Payload, [hostName], context.Now);

        var today = DateOnly.FromDateTime(context.Now);
        if (_lastAuditedDay != today)
        {
            await SignerAudit.WriteAsync(context.Db, new AuditRecord(AuditActions.CertificateIssued, "GatewayCertificate", hostName,
                null, AuditActorType.System, "fleetify-signer", "fleetify-signer",
                new { HostName = hostName, CertificateFingerprint = issued.Fingerprint, ExpiresAt = issued.NotAfter }), context.Now, cancellationToken);
            // Set only after the write: if the transaction rolls back, the next issue audits again, which is harmless.
            _lastAuditedDay = today;
        }

        _logger.LogInformation("Issued a gateway certificate for {HostName} until {ExpiresAt:O} ({Fingerprint})",
            hostName, issued.NotAfter, issued.Fingerprint);
        return SigningOutcome.Completed(issued.CertificateDer);
    }
}
