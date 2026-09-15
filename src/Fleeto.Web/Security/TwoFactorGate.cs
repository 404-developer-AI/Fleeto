using System.Security.Claims;
using Fleeto.Infrastructure.Data;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Security;

/// <summary>
/// Two-factor authentication is mandatory for every user. A user who signed in with a password but has not set up
/// TOTP yet gets a restricted session: only the setup pages work until setup completes. The database is the source of
/// truth, so completing setup takes effect on the next request without a new sign-in.
/// </summary>
public sealed class TwoFactorGate
{
    private readonly IFleetoDbContextFactory _dbFactory;

    public TwoFactorGate(IFleetoDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task<bool> RequiresSetupAsync(ClaimsPrincipal user, CancellationToken cancellationToken = default)
    {
        if (!TryGetUserId(user, out var userId))
        {
            return true;
        }

        return await RequiresSetupAsync(userId, cancellationToken);
    }

    /// <summary>True when the user has not enabled two-factor authentication, or no longer exists. Fails closed.</summary>
    public async Task<bool> RequiresSetupAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var enabled = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => (bool?)u.TwoFactorEnabled)
            .FirstOrDefaultAsync(cancellationToken);
        return enabled != true;
    }

    public static bool TryGetUserId(ClaimsPrincipal user, out Guid userId)
    {
        userId = Guid.Empty;
        return user.Identity?.IsAuthenticated == true &&
               Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out userId);
    }
}

/// <summary>
/// Server-side enforcement of <see cref="TwoFactorGate"/> for pages and the Blazor circuit. Pages marked
/// <see cref="AllowWithoutTwoFactorAttribute"/> stay reachable; everything else redirects to the setup page, and the
/// circuit endpoint refuses outright, so a restricted session cannot open an interactive page at all.
/// </summary>
public sealed class TwoFactorEnforcementMiddleware
{
    private readonly RequestDelegate _next;

    public TwoFactorEnforcementMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public async Task InvokeAsync(HttpContext context, TwoFactorGate gate)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await _next(context);
            return;
        }

        var isCircuit = context.Request.Path.StartsWithSegments("/_blazor", StringComparison.OrdinalIgnoreCase);
        var page = context.GetEndpoint()?.Metadata.GetMetadata<ComponentTypeMetadata>();
        var mustCheck = isCircuit ||
                        (page is not null && !page.Type.IsDefined(typeof(AllowWithoutTwoFactorAttribute), inherit: false));

        if (mustCheck && await gate.RequiresSetupAsync(context.User, context.RequestAborted))
        {
            if (isCircuit)
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("Set up two-factor authentication before using Fleeto.", context.RequestAborted);
                return;
            }

            context.Response.Redirect("/account/setup-2fa");
            return;
        }

        await _next(context);
    }
}
