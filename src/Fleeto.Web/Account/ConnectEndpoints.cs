using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Fleeto.Core.Domain;
using Fleeto.Web.Security;
using Fleeto.Web.Services;

namespace Fleeto.Web.Account;

/// <summary>
/// Fleeto Connect (0.8.0, PROXMOX-WEB.md): the claim it posts when a fleeto-connect: link starts it, and its downloads. The claim is anonymous
/// on purpose: the secret of the link is what authorises it, once and for 50 seconds, for the user who opened the web interface.
/// </summary>
public static class ConnectEndpoints
{
    public const string ClaimRateLimitPolicy = "connect-claim";

    /// <summary>The body Fleeto Connect posts.</summary>
    public sealed record ClaimRequest(string? Claim, string? PublicKey, string? Version, string? Platform);

    private static readonly ConcurrentDictionary<string, string> Hashes = new(StringComparer.Ordinal);

    public static IEndpointRouteBuilder MapConnectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/connect/v1/claim", async (ClaimRequest? request, HttpContext http, RemoteSessionService remote, CancellationToken cancellationToken) =>
        {
            byte[] key;
            try
            {
                key = Convert.FromBase64String(request?.PublicKey ?? string.Empty);
            }
            catch (FormatException)
            {
                key = [];
            }

            if (request?.Claim is not { Length: > 0 and < 200 } claim)
            {
                return Problem(RemoteSessionService.ClaimProblem);
            }

            var result = await remote.ClaimProxmoxAsync(claim, key, request.Version, request.Platform, http.RemoteIp(), cancellationToken);
            return result is { Success: true, Value: { } ticket }
                ? Results.Ok(new
                {
                    participantId = ticket.ParticipantId, sessionId = ticket.SessionId, token = ticket.Token, signature = ticket.Signature, keyId = ticket.KeyId,
                    fingerprints = ticket.Fingerprints, relayUrl = ticket.RelayUrl, endpoint = ticket.Endpoint, endpointId = ticket.EndpointId
                })
                : Problem(result.Problem ?? ServiceSupport.GenericProblem);
        }).AllowAnonymous().DisableAntiforgery().RequireRateLimiting(ClaimRateLimitPolicy);

        // The files of Fleeto Connect of this release, for users who may open the web interface.
        app.MapGet("/connect/download/{platform}", (string platform, IConfiguration configuration, IWebHostEnvironment environment) =>
        {
            var file = ConnectDownloads.Find(platform);
            var path = file is null ? null : FilePath(configuration, environment, file);
            if (file is null || path is null)
            {
                return Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not found",
                    detail: "Fleeto Connect is not available on this server for that operating system.");
            }

            return Results.File(path, file.ContentType, file.DownloadName.Replace("{version}", FleetoVersion.Current, StringComparison.Ordinal),
                enableRangeProcessing: true);
        }).RequireAuthorization(FleetoPolicies.PermissionPrefix + Permissions.RemoteProxmox).RequireRateLimiting(AccountEndpoints.DownloadRateLimitPolicy);

        return app;
    }

    /// <summary>The file of Fleeto Connect for a platform on this server, or null when the image carries none (a development build).</summary>
    public static string? FilePath(IConfiguration configuration, IWebHostEnvironment environment, ConnectDownloads.File file)
    {
        var configured = configuration["Connect:FilesDirectory"];
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var directory = Path.GetFullPath(Path.IsPathRooted(configured) ? configured : Path.Combine(environment.ContentRootPath, configured));
        var path = Path.GetFullPath(Path.Combine(directory, file.Path));
        return path.StartsWith(directory, StringComparison.Ordinal) && File.Exists(path) ? path : null;
    }

    /// <summary>The SHA-256 of a file of Fleeto Connect, for the download page; read once per file.</summary>
    public static string? Sha256(string? path)
    {
        if (path is null)
        {
            return null;
        }

        return Hashes.GetOrAdd(path, p =>
        {
            using var stream = File.OpenRead(p);
            return Convert.ToHexStringLower(SHA256.HashData(stream));
        });
    }

    public static RateLimitPartition<string> ClaimLimit(HttpContext context) => RateLimitPartition.GetFixedWindowLimiter(
        EndpointExtensions.ClientPartitionKey(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 20, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    private static IResult Problem(string detail) =>
        Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "Session not opened", detail: detail);
}
