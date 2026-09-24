using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Security;

/// <summary>
/// Resolves the <see cref="Caller"/> for the current circuit or request. Roles, lockout and two-factor state are read
/// from the database (cached for a few seconds), so a demoted, locked or deleted user cannot keep acting through an
/// open circuit on the strength of an old cookie.
/// </summary>
public sealed class CurrentUser
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(5);

    private readonly AuthenticationStateProvider _authenticationStateProvider;
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly TimeProvider _time;
    private Caller? _cached;
    private DateTimeOffset _cachedAt;

    public CurrentUser(AuthenticationStateProvider authenticationStateProvider, IFleetoDbContextFactory dbFactory,
        IHttpContextAccessor httpContextAccessor, TimeProvider time)
    {
        _authenticationStateProvider = authenticationStateProvider;
        _dbFactory = dbFactory;
        _httpContextAccessor = httpContextAccessor;
        _time = time;
    }

    /// <summary>
    /// Remote IP address of the browser, captured when the circuit starts (the HTTP context is not available inside a
    /// circuit afterwards).
    /// </summary>
    public string? IpAddress { get; set; }

    /// <summary>The caller, or an <see cref="AccessDeniedException"/> when nobody usable is signed in.</summary>
    public async Task<Caller> GetAsync(CancellationToken cancellationToken = default)
    {
        var caller = await TryGetAsync(cancellationToken);
        return caller ?? throw new AccessDeniedException();
    }

    /// <summary>
    /// The caller, or null when not signed in, the account no longer exists, is locked out, has no role, or has not
    /// completed two-factor setup.
    /// </summary>
    public async Task<Caller?> TryGetAsync(CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        if (_cached is not null && now - _cachedAt < CacheLifetime)
        {
            return _cached;
        }

        var state = await _authenticationStateProvider.GetAuthenticationStateAsync();
        if (!TwoFactorGate.TryGetUserId(state.User, out var userId))
        {
            return null;
        }

        await using var db = _dbFactory.CreateSystem();
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.TwoFactorEnabled, u.EntraObjectId, u.LockoutEnd })
            .FirstOrDefaultAsync(cancellationToken);
        // Two factors by the same rule as TwoFactorGate: a user that signed in through Entra ID with a second factor from
        // Microsoft has no local authenticator, and must not be turned away by every page after the gate let it in.
        if (user is null || !TwoFactorGate.HasSecondFactor(user.TwoFactorEnabled, user.EntraObjectId, TwoFactorGate.HasMicrosoftSecondFactor(state.User)) ||
            user.LockoutEnd > now)
        {
            _cached = null;
            return null;
        }

        var roles = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => r.Name!)
            .ToListAsync(cancellationToken);
        if (!roles.Any(FleetoRoles.All.Contains))
        {
            _cached = null;
            return null;
        }

        IpAddress ??= _httpContextAccessor.HttpContext?.RemoteIp();

        // Users are not restricted to clients in 0.1.0; the scope type exists so API keys and tests can restrict.
        _cached = new Caller(user.Id, string.IsNullOrWhiteSpace(user.DisplayName) ? user.Email ?? string.Empty : user.DisplayName,
            user.Email ?? string.Empty, roles, SystemClientScope.Instance, IpAddress);
        _cachedAt = now;
        return _cached;
    }
}
