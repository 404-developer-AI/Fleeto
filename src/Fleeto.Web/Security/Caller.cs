using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Services;

namespace Fleeto.Web.Security;

/// <summary>
/// The signed-in user performing an operation, resolved from the database (not only from the cookie), so a
/// deleted, locked or demoted user loses access in an open circuit as well. A public API call runs as a caller too: the API key
/// id as <see cref="UserId"/>, the read-only role and the key's client scope, with <see cref="ActorType"/> ApiKey.
/// <para>
/// What the caller may do are its permissions (0.6.0): the union of those of its roles. <paramref name="Granted"/> is read from
/// the database; without it the permissions of the built-in roles among <paramref name="Roles"/> count.
/// </para>
/// </summary>
public sealed record Caller(Guid UserId, string Name, string Email, IReadOnlyCollection<string> Roles, IClientScope Scope,
    string? IpAddress = null, AuditActorType ActorType = AuditActorType.User, IReadOnlySet<string>? Granted = null)
{
    private readonly IReadOnlySet<string> _granted = Granted ?? BuiltInPermissions(Roles);

    /// <summary>The built-in admin role: every permission, and outside the groups, so it sees every client.</summary>
    public bool IsAdmin => Roles.Contains(FleetoRoles.Admin);

    /// <summary>
    /// The caller holds the permission; an admin holds every one. A permission of the administration of the instance counts only for
    /// a caller that sees every client without exception, so a caller limited to clients cannot use it to reach further.
    /// </summary>
    public bool Has(string permission) =>
        (IsAdmin || _granted.Contains(permission)) && (Core.Domain.Permissions.Find(permission) is not { InstanceWide: true } || Scope.AllClients);

    /// <summary>Every permission the caller can use, for the role editor and the list of users.</summary>
    public IReadOnlyCollection<string> Effective =>
        Core.Domain.Permissions.All.Where(p => Has(p.Key)).Select(p => p.Key).ToList();

    /// <summary>The roles as shown in the UI.</summary>
    public string RoleText => string.Join(", ", Roles.Select(Core.Domain.BuiltInRoles.DisplayName).Order(StringComparer.OrdinalIgnoreCase));

    private static IReadOnlySet<string> BuiltInPermissions(IEnumerable<string> roles) =>
        roles.SelectMany(r => Core.Domain.BuiltInRoles.PermissionsOf(r) ?? []).ToHashSet(StringComparer.Ordinal);

    /// <summary>The caller sees all of the client, so it may change what belongs to the client as a whole (0.6.0).</summary>
    public bool HasWholeClient(Guid clientId) => Scope.HasWholeClient(clientId);

    /// <summary>The caller sees all of the site, so it may change what belongs to the site as a whole (0.6.0).</summary>
    public bool HasWholeSite(Guid clientId, Guid siteId) => Scope.HasWholeSite(clientId, siteId);

    public Actor ToActor() => new(UserId, Name, Scope, IpAddress);

    public AuditRecord Audit(string action, string targetType, string targetId, Guid? clientId, object? details = null) =>
        new(action, targetType, targetId, clientId, ActorType, UserId.ToString(), Name, details, IpAddress);
}

/// <summary>Thrown when a caller without the required permission reaches a service method.</summary>
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
    public const string ForbiddenProblem = "Your roles do not allow this action. Ask an admin if you need access.";

    /// <summary>A change to a client as a whole by a user whose groups give part of it (0.6.0).</summary>
    public const string WholeClientProblem =
        "This changes the whole client, and your groups give you part of it only. Ask an admin or somebody with access to the whole client.";

    /// <summary>A change to a site as a whole by a user whose groups give part of it (0.6.0).</summary>
    public const string WholeSiteProblem =
        "This changes the whole site, and your groups give you part of it only. Ask an admin or somebody with access to the whole site.";

    /// <summary>A new client by a user whose groups do not grow with new clients (0.6.0).</summary>
    public const string NewClientProblem =
        "Your groups do not include clients created later, so you would not see this client. Ask an admin to create it.";

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
    /// <summary>Throws an <see cref="AccessDeniedException"/> unless the caller holds the permission (0.6.0).</summary>
    public static void Ensure(this Caller caller, string permission)
    {
        if (!caller.Has(permission))
        {
            throw new AccessDeniedException();
        }
    }

    /// <summary>Throws unless the caller holds at least one of the permissions.</summary>
    public static void EnsureAny(this Caller caller, params string[] permissions)
    {
        if (!permissions.Any(caller.Has))
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
