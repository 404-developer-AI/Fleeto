using Fleeto.Core.Domain;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace Fleeto.Web.Security;

/// <summary>
/// Authorization policies of pages. A page declares <see cref="Viewer"/> (any signed-in user with a role) or the permission it
/// needs with <see cref="RequirePermissionAttribute"/> (0.6.0); service methods check the same permission again on the
/// <see cref="Caller"/>, so a hidden button is never the only protection. Both read the roles and permissions from the
/// database, never from the cookie, so a change to a role counts at once.
/// </summary>
public static class FleetoPolicies
{
    /// <summary>Any signed-in user with a role.</summary>
    public const string Viewer = "fleeto.viewer";

    /// <summary>Prefix of the policy that requires one permission: the key of the permission follows it.</summary>
    public const string PermissionPrefix = "fleeto.permission:";

    public static AuthorizationBuilder AddFleetoPolicies(this AuthorizationBuilder builder)
    {
        builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        builder.Services.AddScoped<IAuthorizationHandler, PermissionHandler>();
        return builder.AddPolicy(Viewer, policy => policy.RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(null)));
    }
}

/// <summary>A page that needs one permission (0.6.0).</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class RequirePermissionAttribute : AuthorizeAttribute
{
    public RequirePermissionAttribute(string permission)
    {
        Permission = permission;
        Policy = FleetoPolicies.PermissionPrefix + permission;
    }

    public string Permission { get; }
}

/// <summary>The user holds <see cref="Permission"/>, or any role when it is null.</summary>
public sealed record PermissionRequirement(string? Permission) : IAuthorizationRequirement;

/// <summary>Builds the policy of a <see cref="RequirePermissionAttribute"/> from its name.</summary>
public sealed class PermissionPolicyProvider : DefaultAuthorizationPolicyProvider
{
    public PermissionPolicyProvider(IOptions<AuthorizationOptions> options)
        : base(options)
    {
    }

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        if (policyName.StartsWith(FleetoPolicies.PermissionPrefix, StringComparison.Ordinal))
        {
            var permission = policyName[FleetoPolicies.PermissionPrefix.Length..];
            if (!Permissions.IsKnown(permission))
            {
                throw new InvalidOperationException($"Unknown permission {permission}.");
            }

            return new AuthorizationPolicyBuilder().RequireAuthenticatedUser().AddRequirements(new PermissionRequirement(permission)).Build();
        }

        return await base.GetPolicyAsync(policyName);
    }
}

/// <summary>Checks a <see cref="PermissionRequirement"/> against the roles and permissions in the database.</summary>
public sealed class PermissionHandler : AuthorizationHandler<PermissionRequirement>
{
    private readonly IFleetoDbContextFactory _dbFactory;

    public PermissionHandler(IFleetoDbContextFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        if (!TwoFactorGate.TryGetUserId(context.User, out var userId))
        {
            return;
        }

        await using var db = _dbFactory.CreateSystem();
        var (roles, granted) = await UserPermissions.LoadAsync(db, userId, CancellationToken.None);
        if (roles.Count == 0)
        {
            return;
        }

        if (requirement.Permission is { } permission)
        {
            var scope = granted.Contains(permission) && Permissions.Find(permission) is { InstanceWide: true }
                ? await UserAccessResolver.ScopeOfAsync(db, userId, CancellationToken.None)
                : Infrastructure.Data.SystemClientScope.Instance;
            var caller = new Caller(userId, string.Empty, string.Empty, roles, scope, Granted: granted);
            if (!caller.Has(permission))
            {
                return;
            }
        }

        context.Succeed(requirement);
    }
}

/// <summary>
/// Marks a page that a signed-in user may open before two-factor authentication is set up (the setup pages
/// themselves and the error pages). Every other page is refused until setup completes.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AllowWithoutTwoFactorAttribute : Attribute;
