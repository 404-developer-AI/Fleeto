using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;

namespace Fleeto.Web.Security;

public static class EndpointExtensions
{
    /// <summary>
    /// Verifies the antiforgery token on an endpoint that reads the form itself. <c>UseAntiforgery()</c> only validates
    /// endpoints whose parameters bind form data, so handlers that call <c>ReadFormAsync</c> would otherwise go unchecked.
    /// </summary>
    /// <param name="failureRedirect">Where to send a browser whose token is missing or stale (usually a page left open too long).</param>
    public static RouteHandlerBuilder ValidateAntiforgery(this RouteHandlerBuilder builder, string failureRedirect) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var antiforgery = httpContext.RequestServices.GetRequiredService<IAntiforgery>();
            try
            {
                await antiforgery.ValidateRequestAsync(httpContext);
            }
            catch (AntiforgeryValidationException)
            {
                httpContext.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("Fleeto.Web.Security.Antiforgery")
                    .LogWarning("Refused a form post without a valid antiforgery token to {Path}", LogText.Clean(httpContext.Request.Path.Value));
                return Results.Redirect(failureRedirect);
            }

            return await next(context);
        });

    /// <summary>
    /// Runs the endpoint with an anonymous principal. Sign-in endpoints identify their subject by credentials or a token,
    /// never by a session cookie that may belong to another account signed in in the same browser.
    /// </summary>
    public static RouteHandlerBuilder WithAnonymousPrincipal(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
            return await next(context);
        });

    /// <summary>
    /// Refuses an authenticated endpoint while the signed-in user has not completed two-factor setup. The same rule is
    /// enforced for pages and the Blazor circuit by <see cref="TwoFactorEnforcementMiddleware"/>.
    /// </summary>
    public static RouteHandlerBuilder RequireCompletedTwoFactor(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter(async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var gate = httpContext.RequestServices.GetRequiredService<TwoFactorGate>();
            if (httpContext.User.Identity?.IsAuthenticated != true || await gate.RequiresSetupAsync(httpContext.User, httpContext.RequestAborted))
            {
                return Results.Redirect("/account/setup-2fa");
            }

            return await next(context);
        });

    /// <summary>
    /// Rate-limit partition for an anonymous caller. IPv6 is limited per /64, because a home connection normally holds a
    /// whole /64 and limiting per address would be trivial to walk around.
    /// </summary>
    public static string ClientPartitionKey(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return "unknown";
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address.ToString();
        }

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    /// <summary>Remote IP address for audit entries (already resolved from forwarded headers when behind the proxy).</summary>
    public static string? RemoteIp(this HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        return (address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address).ToString();
    }

    /// <summary>A local path that is safe to redirect to after sign-in: starts with one slash, no scheme, no backslash.</summary>
    public static string SafeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrEmpty(returnUrl) || returnUrl.Length > 500 || returnUrl[0] != '/' ||
            returnUrl.StartsWith("//", StringComparison.Ordinal) || returnUrl.Contains('\\') ||
            returnUrl.Any(char.IsControl) || returnUrl.StartsWith("/account/", StringComparison.OrdinalIgnoreCase) ||
            returnUrl.StartsWith("/api/", StringComparison.OrdinalIgnoreCase))
        {
            return "/";
        }

        return returnUrl;
    }
}
