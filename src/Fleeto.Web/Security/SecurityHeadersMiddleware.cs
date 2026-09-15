using System.Security.Cryptography;

namespace Fleeto.Web.Security;

/// <summary>
/// Security headers for every response, set in the application so they apply behind any proxy and in development.
/// The Content Security Policy allows scripts only from this origin or with the per-response nonce.
/// </summary>
public static class SecurityHeadersMiddleware
{
    /// <summary>Key under which the per-response nonce is published to the renderer (App.razor).</summary>
    public const string NonceItemKey = "fleeto-csp-nonce";

    public static IApplicationBuilder UseFleetoSecurityHeaders(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            // A fresh nonce per response; a reused nonce would be forgeable.
            var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            context.Items[NonceItemKey] = nonce;

            context.Response.OnStarting(static state =>
            {
                var ctx = (HttpContext)state;
                // Signed-in pages must not stay in a shared computer's disk cache, and pages reference fingerprinted
                // assets that change with every release.
                if (ctx.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
                {
                    ctx.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    ctx.Response.Headers.Pragma = "no-cache";
                }

                return Task.CompletedTask;
            }, context);

            var headers = context.Response.Headers;
            headers["Content-Security-Policy"] = BuildContentSecurityPolicy(nonce, WebSocketSources(context));
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            await next();
        });

    internal static string BuildContentSecurityPolicy(string nonce, string webSocketSources) =>
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        "form-action 'self'; " +
        "img-src 'self' data:; " +
        // Fonts are self-hosted (no Google Fonts: GDPR).
        "font-src 'self'; " +
        // 'unsafe-inline' for styles is required: MudBlazor renders inline style attributes, which cannot carry a nonce.
        // Scripts never get 'unsafe-inline'.
        "style-src 'self' 'unsafe-inline'; " +
        $"script-src 'self' 'nonce-{nonce}'; " +
        $"connect-src 'self'{webSocketSources}";

    /// <summary>
    /// The Blazor circuit runs over a WebSocket to this same origin. Naming the host keeps connect-src closed; a blanket
    /// <c>wss:</c> would let injected script stream data anywhere.
    /// </summary>
    private static string WebSocketSources(HttpContext context)
    {
        var host = context.Request.Host.Value;

        // A header value must never carry a space or semicolon from the request.
        if (string.IsNullOrEmpty(host) || host.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or ':' or '[' or ']')))
        {
            return string.Empty;
        }

        return $" wss://{host} ws://{host}";
    }
}
