using Fleetify.Core.Entities;
using Microsoft.AspNetCore.Authorization;

namespace Fleetify.Web.Security;

/// <summary>
/// Authorization policy names. Pages declare one of these; service methods check the same rules again on the
/// <see cref="Caller"/>, so a hidden button is never the only protection.
/// </summary>
public static class FleetifyPolicies
{
    /// <summary>Any signed-in user with a role: admin, technician or read-only.</summary>
    public const string Viewer = "fleetify.viewer";

    /// <summary>Admins and technicians: manage clients, sites, endpoints, templates, alerts and enrollment tokens.</summary>
    public const string Technician = "fleetify.technician";

    /// <summary>Admins only: everything under Settings.</summary>
    public const string Admin = "fleetify.admin";

    public static AuthorizationBuilder AddFleetifyPolicies(this AuthorizationBuilder builder) =>
        builder
            .AddPolicy(Viewer, policy => policy.RequireAuthenticatedUser()
                .RequireRole(FleetifyRoles.Admin, FleetifyRoles.Technician, FleetifyRoles.ReadOnly))
            .AddPolicy(Technician, policy => policy.RequireAuthenticatedUser()
                .RequireRole(FleetifyRoles.Admin, FleetifyRoles.Technician))
            .AddPolicy(Admin, policy => policy.RequireAuthenticatedUser().RequireRole(FleetifyRoles.Admin));
}

/// <summary>
/// Marks a page that a signed-in user may open before two-factor authentication is set up (the setup pages
/// themselves and the error pages). Every other page is refused until setup completes.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class AllowWithoutTwoFactorAttribute : Attribute;
