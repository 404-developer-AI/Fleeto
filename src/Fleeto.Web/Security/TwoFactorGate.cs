using System.Security.Claims;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Endpoints;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Security;

/// <summary>
/// Two-factor authentication is mandatory for every user. A user who signed in with a password but has not set up
/// TOTP yet gets a restricted session: only the setup pages work until setup completes. The database is the source of
/// truth, so completing setup takes effect on the next request without a new sign-in.
/// <para>
/// From 0.5.0 a second factor can also come from Microsoft Entra ID: when the id_token of the sign-in proved
/// multi-factor authentication, or the customer leaves the second factor of linked users to its tenant, the session carries
/// <see cref="SecondFactorClaim"/> and the user needs no local authenticator. The link in the database is checked again on
/// every request, so unlinking a user ends that exemption.
/// </para>
/// </summary>
public sealed class TwoFactorGate
{
    /// <summary>Claim on a session whose second factor came from somewhere else than the local authenticator (0.5.0).</summary>
    public const string SecondFactorClaim = "fleeto:second-factor";

    /// <summary>Value of <see cref="SecondFactorClaim"/> when Microsoft Entra ID proved the second factor.</summary>
    public const string EntraSecondFactor = EntraSignIn.ProvenSecondFactor;

    /// <summary>Value of <see cref="SecondFactorClaim"/> when the customer leaves the second factor to its tenant.</summary>
    public const string DelegatedSecondFactor = EntraSignIn.DelegatedSecondFactor;

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

        return await RequiresSetupAsync(userId,
            user.HasClaim(SecondFactorClaim, EntraSecondFactor) || user.HasClaim(SecondFactorClaim, DelegatedSecondFactor), cancellationToken);
    }

    /// <summary>True when the user has not enabled two-factor authentication, or no longer exists. Fails closed.</summary>
    public Task<bool> RequiresSetupAsync(Guid userId, CancellationToken cancellationToken = default) =>
        RequiresSetupAsync(userId, entraSecondFactor: false, cancellationToken);

    /// <param name="entraSecondFactor">
    /// True when this session signed in through Entra ID with a token that proved multi-factor authentication, or while the
    /// customer left the second factor to its tenant (switching that off ends these sessions). It counts
    /// only while the user is still linked, so the exemption cannot outlive the link.
    /// </param>
    public async Task<bool> RequiresSetupAsync(Guid userId, bool entraSecondFactor, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var found = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.TwoFactorEnabled, u.EntraObjectId })
            .FirstOrDefaultAsync(cancellationToken);
        if (found is null)
        {
            return true;
        }

        return !found.TwoFactorEnabled && !(entraSecondFactor && found.EntraObjectId is not null);
    }

    /// <summary>
    /// Keeps <see cref="SecondFactorClaim"/> on a session whose principal Identity rebuilds when it validates the security
    /// stamp (every five minutes). The rebuilt principal comes from the user store, which does not know how this session
    /// signed in, so without this the session would lose its second factor halfway and be sent to the setup page.
    /// </summary>
    public static void CarryOverSecondFactor(ClaimsPrincipal? current, ClaimsPrincipal? refreshed)
    {
        if (current?.FindFirst(SecondFactorClaim) is not { } claim || refreshed?.Identities.FirstOrDefault() is not { } identity)
        {
            return;
        }

        if (!refreshed.HasClaim(claim.Type, claim.Value))
        {
            identity.AddClaim(new Claim(claim.Type, claim.Value));
        }
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
