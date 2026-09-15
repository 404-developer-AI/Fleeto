using System.Buffers;
using Fleeto.Core.Entities;
using Fleeto.Gateway.Http;
using Fleeto.Gateway.Signing;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Npgsql;

namespace Fleeto.Gateway.Enrollment;

/// <summary>
/// <c>POST /v1/recover</c> (0.2.0, ARCHITECTURE.md §4, Certificate recovery): an agent whose certificate expired while it was
/// offline renews it with that certificate. Accepted only for a certificate on the allow list's expired set (expired within
/// <see cref="ProtocolLimits.RecoveryGrace"/>, never revoked); the CSR must carry the key the TLS handshake just proved. Never
/// opens a session. fleeto-signer checks everything again.
/// </summary>
public sealed class RecoveryHandler
{
    internal const string RefusedDetail =
        "This agent certificate cannot be recovered: it was revoked, replaced, or expired more than a year ago. Enroll the agent again from the endpoint menu.";

    private readonly CertificateAllowList _allowList;
    private readonly SigningRequestClient _signing;
    private readonly ILogger<RecoveryHandler> _logger;

    public RecoveryHandler(CertificateAllowList allowList, SigningRequestClient signing, ILogger<RecoveryHandler> logger)
    {
        _allowList = allowList;
        _signing = signing;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        var certificate = await context.Connection.GetClientCertificateAsync(cancellationToken);
        var decision = _allowList.AuthorizeRecovery(certificate, out var identity);
        if (decision == AllowListDecision.NotLoaded)
        {
            context.Response.Headers.RetryAfter = "30";
            await Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "Gateway starting",
                "The gateway is not ready yet. Try again in a moment.");
            return;
        }

        if (decision != AllowListDecision.Accepted || identity is null)
        {
            _logger.LogInformation("Refused a certificate recovery from {RemoteAddress}: {Reason}", remoteAddress,
                certificate is null ? "no client certificate" : "certificate not recoverable");
            await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "Recovery refused", RefusedDetail);
            return;
        }

        if (context.Request.ContentType?.Split(';')[0].Trim() != ProtocolLimits.ProtobufContentType)
        {
            await Problems.WriteAsync(context, StatusCodes.Status415UnsupportedMediaType, "Unsupported content type",
                $"Send the recovery request as {ProtocolLimits.ProtobufContentType}.");
            return;
        }

        var body = await ReadBodyAsync(context, cancellationToken);
        if (body is null)
        {
            await Problems.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "Request too large",
                $"A recovery request may be at most {ProtocolLimits.MaxEnrollRequestBytes / 1024} KiB.");
            return;
        }

        RecoverRequest request;
        try
        {
            request = RecoverRequest.Parser.ParseFrom(body);
        }
        catch (InvalidProtocolBufferException)
        {
            await Problems.WriteAsync(context, StatusCodes.Status400BadRequest, "Invalid recovery request",
                "The recovery request could not be read. Use a Fleeto agent of the same version as the server.");
            return;
        }

        var csrDer = request.CsrDer.ToByteArray();
        try
        {
            if (!SecureCompare.HexEquals(InternalCertificateAuthority.CsrPublicKeyFingerprint(csrDer), identity.PublicKeyFingerprint))
            {
                _logger.LogWarning("Endpoint {EndpointId}: refused a recovery whose key differs from the connection certificate", identity.EndpointId);
                await Problems.WriteAsync(context, StatusCodes.Status400BadRequest, "Invalid certificate signing request",
                    "The certificate signing request must use the key of the expired agent certificate.");
                return;
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            await Problems.WriteAsync(context, StatusCodes.Status400BadRequest, "Invalid certificate signing request",
                "The certificate signing request is invalid. It must be an ECDSA P-256 request signed with the agent key.");
            return;
        }

        SigningOutcome outcome;
        try
        {
            outcome = await _signing.RequestAsync(SigningRequestKind.AgentRecovery, identity.ClientId, identity.EndpointId, body,
                $"gateway:{remoteAddress}", cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Recovery for endpoint {EndpointId}: could not write the signing request", identity.EndpointId);
            await ServiceUnavailableAsync(context);
            return;
        }

        switch (outcome.State)
        {
            case SigningOutcomeState.Completed when outcome.Result is not null:
                // The recovered agent connects right away; it must not wait for the next allow list reload.
                _allowList.AddIssued(outcome.Result, identity.EndpointId);
                _logger.LogInformation("Endpoint {EndpointId}: expired certificate recovered", identity.EndpointId);
                var response = new RecoverResponse { CertificateDer = ByteString.CopyFrom(outcome.Result) }.ToByteArray();
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = ProtocolLimits.ProtobufContentType;
                context.Response.ContentLength = response.Length;
                await context.Response.Body.WriteAsync(response, cancellationToken);
                return;
            case SigningOutcomeState.Refused:
                _logger.LogWarning("Endpoint {EndpointId}: certificate recovery refused: {Reason}", identity.EndpointId, outcome.RefusalReason);
                await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "Recovery refused",
                    string.IsNullOrWhiteSpace(outcome.RefusalReason) ? RefusedDetail : outcome.RefusalReason);
                return;
            default:
                await ServiceUnavailableAsync(context);
                return;
        }
    }

    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var limit = ProtocolLimits.MaxEnrollRequestBytes;
        if (context.Request.ContentLength > limit)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>(4096);
        var chunk = new byte[4096];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.WrittenCount + read > limit)
            {
                return null;
            }

            buffer.Write(chunk.AsSpan(0, read));
        }

        return buffer.WrittenSpan.ToArray();
    }

    private static Task ServiceUnavailableAsync(HttpContext context)
    {
        context.Response.Headers.RetryAfter = "30";
        return Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "Recovery unavailable",
            "The certificate could not be renewed right now. The agent tries again later.");
    }
}
