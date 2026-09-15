using Fleeto.Gateway.Data;
using Fleeto.Gateway.Sessions;
using Fleeto.Gateway.Tls;
using Npgsql;

namespace Fleeto.Gateway.Diagnostics;

/// <summary>
/// <c>GET /health</c> on the plain HTTP health port: 200 when the database answers, a gateway certificate is loaded, the
/// allow list has been loaded and online state was reset; 503 otherwise, with the reason in plain language.
/// </summary>
public sealed class GatewayHealth
{
    private static readonly TimeSpan DatabaseTimeout = TimeSpan.FromSeconds(3);

    private readonly GatewayStore _store;
    private readonly GatewayCertificateProvider _certificates;
    private readonly CertificateAllowList _allowList;
    private readonly AgentSessionManager _sessions;

    public GatewayHealth(GatewayStore store, GatewayCertificateProvider certificates, CertificateAllowList allowList, AgentSessionManager sessions)
    {
        _store = store;
        _certificates = certificates;
        _allowList = allowList;
        _sessions = sessions;
    }

    public async Task HandleAsync(HttpContext context)
    {
        var problems = new List<string>();

        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
        {
            timeout.CancelAfter(DatabaseTimeout);
            try
            {
                await _store.PingAsync(timeout.Token);
            }
            catch (Exception ex) when (ex is NpgsqlException or TimeoutException or OperationCanceledException or InvalidOperationException)
            {
                problems.Add("The database is not reachable.");
            }
        }

        var certificate = _certificates.Current;
        if (certificate is null)
        {
            problems.Add(_certificates.Status);
        }

        if (!_allowList.IsLoaded)
        {
            problems.Add("The agent certificate allow list has not been loaded yet.");
        }

        if (!_sessions.IsReady)
        {
            problems.Add("The online state of endpoints has not been reset yet.");
        }

        var healthy = problems.Count == 0;
        context.Response.StatusCode = healthy ? StatusCodes.Status200OK : StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(new
        {
            status = healthy ? "healthy" : "unhealthy",
            reasons = problems,
            sessions = _sessions.Count,
            certificateValidUntil = certificate?.NotAfter,
            allowListLoadedAt = _allowList.LoadedAt,
            allowListCertificates = _allowList.Count
        }, context.RequestAborted);
    }
}
