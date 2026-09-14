using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Security;
using Fleetify.Protocol;
using Fleetify.Protocol.Agent.V1;
using Fleetify.Signer.Keys;
using Fleetify.Signer.Processing;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Endpoint = Fleetify.Core.Entities.Endpoint;

namespace Fleetify.Signer.Handlers;

/// <summary>
/// Enrollment: checks the enrollment token and the CSR again (the gateway's pre-check is not trusted), creates the
/// endpoint as agent-only, issues its certificate and signs its first configuration, all in one transaction.
/// </summary>
public sealed class AgentEnrollmentHandler : ISigningRequestHandler
{
    public const string MalformedReason = "The enrollment request is malformed. Run the install command from the site page again.";
    public const string InvalidTokenReason =
        "The enrollment token is not valid. Copy the install command again from the site page, or create a new enrollment token.";
    public const string ExpiredTokenReason = "The enrollment token has expired. Create a new enrollment token for the site and run the new install command.";
    public const string RevokedTokenReason = "The enrollment token was revoked. Create a new enrollment token for the site and run the new install command.";
    public const string UsedUpTokenReason =
        "The enrollment token has already been used the maximum number of times. Create a new enrollment token for the site.";
    public const string InvalidCsrReason =
        "The certificate signing request is invalid: it must use an ECDSA P-256 key and be signed with that key. Reinstall the agent to generate a new key.";
    public const string MissingHostnameReason = "The enrollment request has no host name. Check the host name of the endpoint and run the install command again.";

    private readonly SignerKeyRing _keyRing;
    private readonly AgentConfigSigner _configSigner;
    private readonly ILogger<AgentEnrollmentHandler> _logger;

    public AgentEnrollmentHandler(SignerKeyRing keyRing, AgentConfigSigner configSigner, ILogger<AgentEnrollmentHandler> logger)
    {
        _keyRing = keyRing;
        _configSigner = configSigner;
        _logger = logger;
    }

    public SigningRequestKind Kind => SigningRequestKind.AgentEnrollment;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var request = context.Request;
        var now = context.Now;

        if (request.Payload.Length is 0 or > ProtocolLimits.MaxEnrollRequestBytes)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        EnrollRequest enroll;
        try
        {
            enroll = EnrollRequest.Parser.ParseFrom(request.Payload);
        }
        catch (InvalidProtocolBufferException)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        if (enroll.CsrDer.Length is 0 or > InputText.MaxCsrBytes)
        {
            return SigningOutcome.Refused(MalformedReason);
        }

        // Token: shape, row (locked, so concurrent enrollments count uses correctly), secret, client, usability.
        if (!OpaqueTokens.TryParse(enroll.Token, OpaqueTokens.EnrollmentPrefix, out var tokenId, out var secretHash))
        {
            return SigningOutcome.Refused(InvalidTokenReason);
        }

        var tokens = await db.EnrollmentTokens.FromSql($"""
            SELECT * FROM "EnrollmentTokens" WHERE "Id" = {tokenId} FOR UPDATE
            """).IgnoreQueryFilters().ToListAsync(cancellationToken);
        var token = tokens.Count == 1 ? tokens[0] : null;

        // Compare even when the row is missing, so the response time does not depend on which check failed.
        var storedHash = token?.TokenHash ?? new string('0', secretHash.Length);
        var secretMatches = SecureCompare.HexEquals(storedHash, secretHash);
        if (token is null || !secretMatches || request.ClientId != token.ClientId)
        {
            return SigningOutcome.Refused(InvalidTokenReason);
        }

        if (!token.IsUsable(now))
        {
            return SigningOutcome.Refused(token.RevokedAt is not null ? RevokedTokenReason
                : token.ExpiresAt <= now ? ExpiredTokenReason
                : UsedUpTokenReason);
        }

        // CSR: proof of possession and P-256, checked before anything is written.
        var csrDer = enroll.CsrDer.ToByteArray();
        try
        {
            _ = InternalCertificateAuthority.CsrPublicKeyFingerprint(csrDer);
        }
        catch (InvalidOperationException)
        {
            return SigningOutcome.Refused(InvalidCsrReason);
        }

        var hostname = InputText.Clean(enroll.Hostname, 255);
        if (hostname.Length == 0)
        {
            return SigningOutcome.Refused(MissingHostnameReason);
        }

        var os = enroll.Os ?? new OsInfo();
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            ClientId = token.ClientId,
            SiteId = token.SiteId,
            Hostname = hostname,
            DetectedClass = os.IsServer ? EndpointClass.Server : EndpointClass.Workstation,
            Tier = EndpointTier.AgentOnly,
            Source = EndpointSource.Agent,
            OsPlatform = InputText.Clean(os.Platform, 20).ToLowerInvariant(),
            OsName = InputText.Clean(os.Name, 200),
            OsVersion = InputText.Clean(os.Version, 100),
            Architecture = InputText.Clean(os.Architecture, 20).ToLowerInvariant(),
            AgentVersion = InputText.Clean(enroll.AgentVersion, 50),
            EnrolledAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Endpoints.Add(endpoint);

        var certificate = _keyRing.IssueAgentCertificate(csrDer, endpoint.Id, now);
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            Fingerprint = certificate.Fingerprint,
            PublicKeyFingerprint = certificate.PublicKeyFingerprint,
            SerialNumber = certificate.SerialNumber,
            IssuedAt = now,
            ExpiresAt = certificate.NotAfter
        });

        token.UseCount++;

        var ipAddress = SigningRequestProcessor.IpAddressFrom(request.RequestedBy);
        // Never the token: only its id, which is not a credential.
        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.EndpointEnrolled, "Endpoint", endpoint.Id.ToString(),
            endpoint.ClientId, AuditActorType.Agent, endpoint.Id.ToString(), hostname,
            new
            {
                Hostname = hostname,
                SiteId = endpoint.SiteId,
                TokenId = token.Id,
                CertificateFingerprint = certificate.Fingerprint
            }, ipAddress), now, cancellationToken);
        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.CertificateIssued, "Endpoint", endpoint.Id.ToString(),
            endpoint.ClientId, AuditActorType.Agent, endpoint.Id.ToString(), hostname,
            new
            {
                Hostname = hostname,
                SiteId = endpoint.SiteId,
                TokenId = token.Id,
                CertificateFingerprint = certificate.Fingerprint,
                certificate.SerialNumber,
                ExpiresAt = certificate.NotAfter
            }, ipAddress), now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);

        var config = await _configSigner.SignAsync(context, endpoint.Id, cancellationToken);
        if (config.Status != ConfigSignStatus.Issued)
        {
            // A brand-new agent-only endpoint always gets version 1; anything else means something is badly wrong,
            // and the whole enrollment is rolled back rather than leaving an endpoint without a signed configuration.
            throw new InvalidOperationException($"The initial configuration for a new endpoint was not issued ({config.Status}).");
        }

        var response = new EnrollResponse
        {
            EndpointId = endpoint.Id.ToString("D"),
            InstanceId = _keyRing.InstanceId.ToString("D"),
            CertificateDer = ByteString.CopyFrom(certificate.CertificateDer),
            CaCertificateDer = ByteString.CopyFrom(_keyRing.CaCertificateDer),
            InstanceSigningPublicKey = ByteString.CopyFrom(_keyRing.SigningPublicKey),
            InstanceSigningKeyId = _keyRing.SigningKeyId
        };

        _logger.LogInformation("Enrolled endpoint {EndpointId} ({Hostname}) in site {SiteId} with token {TokenId}",
            endpoint.Id, hostname, endpoint.SiteId, token.Id);

        return SigningOutcome.Completed(response.ToByteArray(),
            new PendingNotification(NotificationChannels.EndpointStatus, endpoint.Id.ToString()),
            new PendingNotification(NotificationChannels.EndpointConfig, endpoint.Id.ToString()));
    }
}
