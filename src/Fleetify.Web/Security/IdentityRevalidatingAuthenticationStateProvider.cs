using System.Security.Claims;
using Fleetify.Infrastructure.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Fleetify.Web.Security;

/// <summary>
/// Re-checks the security stamp of the user behind an open circuit every few minutes, so a deleted user, a reset of
/// two-factor authentication or a role change also ends an interactive session, not only new requests.
/// </summary>
public sealed class IdentityRevalidatingAuthenticationStateProvider : RevalidatingServerAuthenticationStateProvider
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IdentityOptions _options;

    public IdentityRevalidatingAuthenticationStateProvider(ILoggerFactory loggerFactory, IServiceScopeFactory scopeFactory,
        IOptions<IdentityOptions> options)
        : base(loggerFactory)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
    }

    protected override TimeSpan RevalidationInterval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> ValidateAuthenticationStateAsync(AuthenticationState authenticationState, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.GetUserAsync(authenticationState.User);
        if (user is null)
        {
            return false;
        }

        if (!users.SupportsUserSecurityStamp)
        {
            return true;
        }

        var principalStamp = authenticationState.User.FindFirstValue(_options.ClaimsIdentity.SecurityStampClaimType);
        var userStamp = await users.GetSecurityStampAsync(user);
        return principalStamp == userStamp;
    }
}
