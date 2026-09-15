using System.Collections.Concurrent;
using Fleeto.Gateway.Http;
using Fleeto.Gateway.Tls;
using Microsoft.Extensions.Options;

namespace Fleeto.Gateway.Releases;

/// <summary>
/// <c>GET /v1/releases/{version}/{file}</c> (0.2.1): one agent binary of the current release for an agent or watchdog with a valid client
/// certificate. Only files named in the verified release manifest are served, at most <see cref="GatewayOptions.MaxConcurrentDownloads"/>
/// at a time and a few per endpoint per hour, so a release reaching thousands of endpoints at once cannot saturate the VPS link. The endpoint
/// verifies what it downloads against the signed manifest; this handler adds no trust.
/// </summary>
public sealed class ReleaseDownloadHandler
{
    public const int MaxDownloadsPerEndpointPerHour = 12;

    private readonly ReleaseCatalog _catalog;
    private readonly CertificateAllowList _allowList;
    private readonly TimeProvider _time;
    private readonly ILogger<ReleaseDownloadHandler> _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<Guid, Queue<DateTime>> _perEndpoint = new();

    public ReleaseDownloadHandler(ReleaseCatalog catalog, CertificateAllowList allowList, TimeProvider time, IOptions<GatewayOptions> options,
        ILogger<ReleaseDownloadHandler> logger)
    {
        _catalog = catalog;
        _allowList = allowList;
        _time = time;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, options.Value.MaxConcurrentDownloads));
    }

    public async Task HandleAsync(HttpContext context, string version, string file)
    {
        var certificate = await context.Connection.GetClientCertificateAsync(context.RequestAborted);
        if (_allowList.Authorize(certificate, out var identity) != AllowListDecision.Accepted || identity is null)
        {
            await Problems.WriteAsync(context, StatusCodes.Status401Unauthorized, "Agent certificate not accepted",
                "Agent binaries are only available to enrolled agents with a valid certificate.");
            return;
        }

        var release = _catalog.Current;
        if (release is null || !string.Equals(release.Version, version, StringComparison.Ordinal) ||
            !release.Binaries.TryGetValue(file, out var binary))
        {
            await Problems.WriteAsync(context, StatusCodes.Status404NotFound, "Not part of the current release",
                "This file is not in the release this instance offers. The agent waits for the next update offer.");
            return;
        }

        if (!TryCountDownload(identity.EndpointId))
        {
            context.Response.Headers.RetryAfter = "3600";
            await Problems.WriteAsync(context, StatusCodes.Status429TooManyRequests, "Too many downloads",
                "This endpoint downloaded agent binaries too often in the last hour. It tries again later.");
            return;
        }

        if (!await _slots.WaitAsync(0, context.RequestAborted))
        {
            context.Response.Headers.RetryAfter = "60";
            await Problems.WriteAsync(context, StatusCodes.Status503ServiceUnavailable, "Busy",
                "Many endpoints are downloading an update right now. The agent tries again in a minute.");
            return;
        }

        try
        {
            _logger.LogInformation("Endpoint {EndpointId} ({Role}) downloads {File} of release {Version}", identity.EndpointId, identity.Role, file, version);
            context.Response.ContentType = "application/octet-stream";
            context.Response.ContentLength = binary.Binary.Size;
            context.Response.Headers.CacheControl = "no-store";
            await context.Response.SendFileAsync(binary.Path, 0, binary.Binary.Size, context.RequestAborted);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException)
        {
            _logger.LogInformation("Endpoint {EndpointId}: download of {File} ended early", identity.EndpointId, file);
        }
        finally
        {
            _slots.Release();
        }
    }

    private bool TryCountDownload(Guid endpointId)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var queue = _perEndpoint.GetOrAdd(endpointId, _ => new Queue<DateTime>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() > TimeSpan.FromHours(1))
            {
                queue.Dequeue();
            }

            if (queue.Count >= MaxDownloadsPerEndpointPerHour)
            {
                return false;
            }

            queue.Enqueue(now);
        }

        if (_perEndpoint.Count > 50_000)
        {
            foreach (var (id, entries) in _perEndpoint)
            {
                lock (entries)
                {
                    if (entries.Count == 0 || now - entries.Peek() > TimeSpan.FromHours(1))
                    {
                        _perEndpoint.TryRemove(id, out _);
                    }
                }
            }
        }

        return true;
    }
}
