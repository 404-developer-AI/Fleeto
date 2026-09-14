using System.Buffers;
using System.Globalization;
using System.Threading.RateLimiting;
using Fleetify.Core.Entities;
using Fleetify.Gateway.Diagnostics;
using Fleetify.Gateway.Http;
using Fleetify.Gateway.Signing;
using Fleetify.Gateway.Tls;
using Fleetify.Infrastructure.Security;
using Fleetify.Protocol;
using Fleetify.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace Fleetify.Gateway.Enrollment;

/// <summary>
/// <c>POST /v1/enroll</c> (ARCHITECTURE.md §4, Enrollment): pre-checks the token, hands the request to fleetify-signer
/// and returns its EnrollResponse. The TLS connection is authenticated by the gateway certificate the agent validated
/// against the pinned CA fingerprint, so the response needs no signature of its own.
/// </summary>
public sealed class EnrollmentHandler
{
    public const string RateLimitPolicy = "enrollment";

    internal const string TokenRefusedDetail = "The enrollment token is not valid or has expired. Create a new token for the site.";

    private readonly EnrollmentTokenValidator _tokens;
    private readonly SigningRequestClient _signing;
    private readonly CertificateAllowList _allowList;
    private readonly GatewayMetrics _metrics;
    private readonly ILogger<EnrollmentHandler> _logger;

    public EnrollmentHandler(EnrollmentTokenValidator tokens, SigningRequestClient signing, CertificateAllowList allowList, GatewayMetrics metrics,
        ILogger<EnrollmentHandler> logger)
    {
        _tokens = tokens;
        _signing = signing;
        _allowList = allowList;
        _metrics = metrics;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var cancellationToken = context.RequestAborted;
        var remoteAddress = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";

        if (!IsProtobuf(context.Request.ContentType))
        {
            await Problems.WriteAsync(context, StatusCodes.Status415UnsupportedMediaType, "Unsupported content type",
                $"Send the enrollment request as {ProtocolLimits.ProtobufContentType}.");
            return;
        }

        var body = await ReadBodyAsync(context, cancellationToken);
        if (body is null)
        {
            await Problems.WriteAsync(context, StatusCodes.Status413PayloadTooLarge, "Request too large",
                $"An enrollment request may be at most {ProtocolLimits.MaxEnrollRequestBytes / 1024} KiB.");
            return;
        }

        EnrollRequest request;
        try
        {
            request = EnrollRequest.Parser.ParseFrom(body);
        }
        catch (InvalidProtocolBufferException)
        {
            await Problems.WriteAsync(context, StatusCodes.Status400BadRequest, "Invalid enrollment request",
                "The enrollment request could not be read. Use a Fleeto agent of the same version as the server.");
            return;
        }

        Guid? clientId;
        try
        {
            clientId = await _tokens.ValidateAsync(request.Token, cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _logger.LogWarning(ex, "Enrollment from {RemoteAddress}: database unavailable", remoteAddress);
            await ServiceUnavailableAsync(context, "The gateway cannot reach its database. Try again in a moment.");
            return;
        }

        if (clientId is null)
        {
            _metrics.EnrollmentRefused();
            _logger.LogInformation("Enrollment from {RemoteAddress} refused: token not usable", remoteAddress);
            await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "Enrollment refused", TokenRefusedDetail);
            return;
        }

        try
        {
            // Proof of possession and key type, before the signer spends work on it.
            InternalCertificateAuthority.CsrPublicKeyFingerprint(request.CsrDer.ToByteArray());
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
            outcome = await _signing.RequestAsync(SigningRequestKind.AgentEnrollment, clientId, null, body, $"gateway:{remoteAddress}",
                cancellationToken);
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException)
        {
            _metrics.EnrollmentFailed();
            _logger.LogWarning(ex, "Enrollment from {RemoteAddress}: could not write the signing request", remoteAddress);
            await ServiceUnavailableAsync(context, "The gateway cannot reach its database. Try again in a moment.");
            return;
        }

        switch (outcome.State)
        {
            case SigningOutcomeState.Completed when outcome.Result is not null:
                _metrics.EnrollmentCompleted();
                AllowNewCertificate(outcome.Result);
                _logger.LogInformation("Enrollment from {RemoteAddress} completed", remoteAddress);
                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = ProtocolLimits.ProtobufContentType;
                context.Response.ContentLength = outcome.Result.Length;
                await context.Response.Body.WriteAsync(outcome.Result, cancellationToken);
                return;
            case SigningOutcomeState.Refused:
                _metrics.EnrollmentRefused();
                _logger.LogInformation("Enrollment from {RemoteAddress} refused by fleetify-signer", remoteAddress);
                await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "Enrollment refused",
                    string.IsNullOrWhiteSpace(outcome.RefusalReason) ? TokenRefusedDetail : outcome.RefusalReason);
                return;
            case SigningOutcomeState.TimedOut:
                _metrics.EnrollmentFailed();
                _logger.LogWarning("Enrollment from {RemoteAddress}: fleetify-signer did not answer in time", remoteAddress);
                await ServiceUnavailableAsync(context, "Enrollment is taking longer than expected. Try again in a moment.");
                return;
            default:
                _metrics.EnrollmentFailed();
                _logger.LogWarning("Enrollment from {RemoteAddress}: fleetify-signer could not complete the request", remoteAddress);
                await ServiceUnavailableAsync(context, "Enrollment could not be completed. Try again in a moment.");
                return;
        }
    }

    /// <summary>Per remote address: at most N enrollment attempts per minute, answered with problem+json 429.</summary>
    public static void AddRateLimiting(IServiceCollection services, int permitsPerMinute)
    {
        services.AddRateLimiter(options =>
        {
            options.AddPolicy(RateLimitPolicy, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = permitsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
            options.OnRejected = async (rejected, _) =>
            {
                var retryAfter = rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var value) ? value : TimeSpan.FromMinutes(1);
                rejected.HttpContext.Response.Headers.RetryAfter =
                    ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                await Problems.WriteAsync(rejected.HttpContext, StatusCodes.Status429TooManyRequests, "Too many enrollment attempts",
                    "Too many enrollment attempts from this address. Wait a minute and try again.");
            };
        });
    }

    /// <summary>The agent connects right after enrolling; do not make it wait for the next allow list reload.</summary>
    private void AllowNewCertificate(byte[] enrollResponse)
    {
        try
        {
            var response = EnrollResponse.Parser.ParseFrom(enrollResponse);
            if (Guid.TryParse(response.EndpointId, out var endpointId))
            {
                _allowList.AddIssued(response.CertificateDer.ToByteArray(), endpointId);
            }
        }
        catch (Exception ex) when (ex is InvalidProtocolBufferException or System.Security.Cryptography.CryptographicException)
        {
            _logger.LogWarning(ex, "The enrollment response from fleetify-signer could not be read; the certificate is allowed after the next reload");
        }
    }

    private static Task ServiceUnavailableAsync(HttpContext context, string detail)
    {
        context.Response.Headers.RetryAfter = "10";
        return Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "Enrollment unavailable", detail);
    }

    private static bool IsProtobuf(string? contentType) =>
        contentType is not null &&
        contentType.Split(';', 2)[0].Trim().Equals(ProtocolLimits.ProtobufContentType, StringComparison.OrdinalIgnoreCase);

    /// <summary>Reads the body up to the limit. Returns null when it is larger.</summary>
    private static async Task<byte[]?> ReadBodyAsync(HttpContext context, CancellationToken cancellationToken)
    {
        var limit = ProtocolLimits.MaxEnrollRequestBytes;
        if (context.Request.ContentLength > limit)
        {
            return null;
        }

        var sizeFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (sizeFeature is { IsReadOnly: false })
        {
            sizeFeature.MaxRequestBodySize = limit;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(limit + 1);
        try
        {
            var total = 0;
            while (true)
            {
                int read;
                try
                {
                    read = await context.Request.Body.ReadAsync(buffer.AsMemory(total, limit + 1 - total), cancellationToken);
                }
                catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
                {
                    return null;
                }

                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total > limit)
                {
                    return null;
                }
            }

            return buffer.AsSpan(0, total).ToArray();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
