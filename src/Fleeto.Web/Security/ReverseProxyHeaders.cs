using Microsoft.AspNetCore.HttpOverrides;

namespace Fleeto.Web.Security;

/// <summary>
/// What web takes from the host proxy (Caddy) in front of it: the client address (X-Forwarded-For) and the scheme
/// (X-Forwarded-Proto), only from the networks in <c>ReverseProxy:KnownNetworks</c>. In the Compose stack that is exactly the
/// gateway address of the instance's edge network, the address Caddy arrives from through the loopback port. X-Forwarded-Host is
/// never taken: Caddy passes the Host header on unchanged, and every absolute URL comes from the configured FQDN.
/// </summary>
public static class ReverseProxyHeaders
{
    public static ForwardedHeadersOptions Options(IConfiguration configuration)
    {
        var forwarded = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
            // Exactly one proxy (Caddy) sits in front of the web container.
            ForwardLimit = 1
        };
        forwarded.KnownIPNetworks.Clear();
        forwarded.KnownProxies.Clear();
        foreach (var network in configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [])
        {
            forwarded.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
        }

        return forwarded;
    }
}
