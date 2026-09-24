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

    public static IApplicationBuilder UseFleetoSecurityHeaders(this IApplicationBuilder app)
    {
        var relaySource = RelaySource(app.ApplicationServices.GetRequiredService<IConfiguration>()["Remote:RelayUrl"]);
        return app.Use(async (context, next) =>
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
            headers["Content-Security-Policy"] = BuildContentSecurityPolicy(nonce, WebSocketSources(context) + relaySource,
                FormActionSources(context));
            headers["X-Content-Type-Options"] = "nosniff";
            headers["X-Frame-Options"] = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";

            await next();
        });
    }

    /// <summary>
    /// The remote session relay (0.3.0) is reached on the instance's own host under /relay/, which the host names already cover. Only a
    /// relay configured elsewhere (local development: the gateway's relay port) is added, by its origin.
    /// </summary>
    internal static string RelaySource(string? relayUrl)
    {
        if (string.IsNullOrWhiteSpace(relayUrl) || !Uri.TryCreate(relayUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("ws" or "wss") ||
            uri.Authority.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '-' or ':' or '[' or ']')))
        {
            return string.Empty;
        }

        return $" {uri.Scheme}://{uri.Authority}";
    }

    /// <summary>
    /// The sign-in page posts "Sign in with Microsoft" to this origin, which answers with a redirect to Microsoft. Browsers
    /// hold that redirect to form-action as well, so only the sign-in page names Microsoft's sign-in origin (0.5.0); every
    /// other page keeps its forms on this origin.
    /// </summary>
    internal static string FormActionSources(HttpContext context) =>
        context.Request.Path.Equals(LoginPath, StringComparison.OrdinalIgnoreCase) ? " " + MicrosoftSignInOrigin : string.Empty;

    internal const string LoginPath = "/account/login";
    internal const string MicrosoftSignInOrigin = "https://login.microsoftonline.com";

    internal static string BuildContentSecurityPolicy(string nonce, string webSocketSources, string formActionSources = "") =>
        "default-src 'self'; " +
        "base-uri 'self'; " +
        "object-src 'none'; " +
        "frame-ancestors 'none'; " +
        $"form-action 'self'{formActionSources}; " +
        "img-src 'self' data:; " +
        // The web app manifest and its icons come from the instance itself.
        "manifest-src 'self'; " +
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
