using Fleeto.Core.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Fleeto.Web.Security;

/// <summary>
/// Authorization policy names. Pages declare one of these; service methods check the same rules again on the
/// <see cref="Caller"/>, so a hidden button is never the only protection.
/// </summary>
public static class FleetoPolicies
{
    /// <summary>Any signed-in user with a role: admin, technician or read-only.</summary>
    public const string Viewer = "fleeto.viewer";

    /// <summary>Admins and technicians: manage clients, sites, endpoints, templates, alerts and enrollment tokens.</summary>
    public const string Technician = "fleeto.technician";

    /// <summary>Admins only: everything under Settings.</summary>
    public const string Admin = "fleeto.admin";

    public static AuthorizationBuilder AddFleetoPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddPolicy(Viewer, policy => policy.RequireAuthenticatedUser()
                .RequireRole(FleetoRoles.Admin, FleetoRoles.Technician, FleetoRoles.ReadOnly))
            .AddPolicy(Technician, policy => policy.RequireAuthenticatedUser()
                .RequireRole(FleetoRoles.Admin, FleetoRoles.Technician))
            .AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().RequireRole(FleetoRoles.Admin));
}

/// <summary>
/// Marks a page that a signed-in user may open before two-factor authentication is set up (the setup pages
/// themselves and the error pages). Every other page is refused until setup completes.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AllowWithoutTwoFactorAttribute : Attribute;
