using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Services;

namespace Fleetify.Web.Security;

/// <summary>
/// The signed-in user performing an operation, resolved from the database (not only from the cookie), so a
/// deleted, locked or demoted user loses access in an open circuit as well.
/// </summary>
public sealed record Caller(Guid UserId, string Name, string Email, IReadOnlyCollection<string> Roles, IClientScope Scope,
    string? IpAddress = null)
{
    public bool IsAdmin => Roles.Contains(FleetifyRoles.Admin);

    /// <summary>Admins and technicians may change clients, sites, endpoints, templates, alerts and tokens.</summary>
    public bool CanManage => IsAdmin || Roles.Contains(FleetifyRoles.Technician);

    public bool CanView => CanManage || Roles.Contains(FleetifyRoles.ReadOnly);

    public Actor ToActor() => new(UserId, Name, Scope, IpAddress);

    public AuditRecord Audit(string action, string targetType, string targetId, Guid? clientId, object? details = null) =>
        new(action, targetType, targetId, clientId, AuditActorType.User, UserId.ToString(), Name, details, IpAddress);
}

/// <summary>Thrown when a caller without the required role reaches a service method.</summary>
public sealed class AccessDeniedException : UnauthorizedAccessException
{
    public AccessDeniedException()
        : base(ServiceResult.ForbiddenProblem)
    {
    }
}

/// <summary>Outcome of a mutating service call. <see cref="Problem"/> is shown to the user verbatim.</summary>
public record ServiceResult(bool Success, string? Problem)
{
    public const string ForbiddenProblem = "Your role does not allow this action. Ask an admin if you need access.";

    public static ServiceResult Ok() => new(true, null);
    public static ServiceResult Fail(string problem) => new(false, problem);
    public static ServiceResult Forbidden() => new(false, ForbiddenProblem);
    public static ServiceResult NotFound(string what) => new(false, $"The {what} no longer exists. Refresh the page and try again.");
}

/// <summary>Outcome of a mutating service call that produces a value.</summary>
public sealed record ServiceResult<T>(bool Success, string? Problem, T? Value) : ServiceResult(Success, Problem)
{
    public static ServiceResult<T> Ok(T value) => new(true, null, value);
    public static new ServiceResult<T> Fail(string problem) => new(false, problem, default);
    public static new ServiceResult<T> Forbidden() => new(false, ForbiddenProblem, default);
    public static new ServiceResult<T> NotFound(string what) => new(false, $"The {what} no longer exists. Refresh the page and try again.", default);
}

internal static class CallerGuards
{
    public static void EnsureView(this Caller caller)
    {
        if (!caller.CanView)
        {
            throw new AccessDeniedException();
        }
    }

    public static void EnsureAdmin(this Caller caller)
    {
        if (!caller.IsAdmin)
        {
            throw new AccessDeniedException();
        }
    }
}
