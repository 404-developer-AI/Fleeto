using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <param name="Name">The stored name; a built-in role keeps its own (admin, technician, read-only).</param>
/// <param name="DisplayName">The name shown in the UI.</param>
public sealed record RoleListItem(Guid Id, string Name, string DisplayName, string? Description, bool IsBuiltIn, IReadOnlyList<string> Permissions,
    int UserCount);

public sealed record RoleInput(string? Name, string? Description, IReadOnlyCollection<string> Permissions);

/// <summary>
/// Roles and their permissions (0.6.0). Admin, technician and read-only are built in and cannot be changed or deleted; a custom
/// role holds exactly the permissions stored for it, so a permission a later release adds is off in it until somebody gives it.
/// A user that is not an admin can only create or change roles with permissions it holds itself. Every change is audited and
/// takes effect at once for the users of the role.
/// </summary>
public sealed class RoleService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<RoleService> _logger;

    public RoleService(IFleetoDbContextFactory dbFactory, INotificationBus bus, TimeProvider time, ILogger<RoleService> logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>Every role, built-in first, for Settings, Roles and for choosing the roles of a user.</summary>
    public async Task<IReadOnlyList<RoleListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAny(Permissions.RolesManage, Permissions.UsersManage);
        await using var db = _dbFactory.CreateSystem();
        var roles = await db.Roles.AsNoTracking().ToListAsync(cancellationToken);
        var stored = await RoleAuthority.PermissionsOfAsync(db, roles, cancellationToken);
        var counts = await db.UserRoles.AsNoTracking().GroupBy(ur => ur.RoleId).Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(g => g.Key, g => g.Count, cancellationToken);
        return roles
            .Select(r => new RoleListItem(r.Id, r.Name!, BuiltInRoles.DisplayName(r.Name!), r.IsBuiltIn ? BuiltInRoles.Description(r.Name!) : r.Description,
                r.IsBuiltIn, stored[r.Id], counts.GetValueOrDefault(r.Id)))
            .OrderBy(r => r.IsBuiltIn ? FleetoRoles.All.ToList().IndexOf(r.Name) : int.MaxValue)
            .ThenBy(r => r.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, RoleInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.RolesManage))
        {
            return ServiceResult<Guid>.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var (name, description, permissions, problem) = await ValidateAsync(db, caller, null, input, cancellationToken);
        if (problem is not null)
        {
            return ServiceResult<Guid>.Fail(problem);
        }

        var role = new ApplicationRole(name!)
        {
            Id = Guid.NewGuid(), NormalizedName = name!.ToUpperInvariant(), ConcurrencyStamp = Guid.NewGuid().ToString(), Description = description
        };
        db.Roles.Add(role);
        db.RolePermissions.AddRange(permissions.Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.RoleCreated, "Role", role.Id.ToString(), null,
            new { Name = name, Permissions = permissions }), _time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(role.Id);
    }

    public async Task<ServiceResult> UpdateAsync(Caller caller, Guid roleId, RoleInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.RolesManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return ServiceResult.NotFound("role");
        }

        if (role.IsBuiltIn)
        {
            return ServiceResult.Fail(BuiltInProblem);
        }

        var before = await db.RolePermissions.Where(p => p.RoleId == role.Id).ToListAsync(cancellationToken);
        if (RoleAuthority.MissingProblem(caller, before.Select(p => p.Permission)) is { } missing)
        {
            return ServiceResult.Fail(missing);
        }

        var (name, description, permissions, problem) = await ValidateAsync(db, caller, role.Id, input, cancellationToken);
        if (problem is not null)
        {
            return ServiceResult.Fail(problem);
        }

        var from = before.Select(p => p.Permission).Order(StringComparer.Ordinal).ToList();
        role.Name = name;
        role.NormalizedName = name!.ToUpperInvariant();
        role.Description = description;
        role.ConcurrencyStamp = Guid.NewGuid().ToString();
        db.RolePermissions.RemoveRange(before.Where(p => !permissions.Contains(p.Permission)));
        db.RolePermissions.AddRange(permissions.Except(from).Select(p => new RolePermission { RoleId = role.Id, Permission = p }));
        var now = _time.GetUtcNow().UtcDateTime;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.RoleUpdated, "Role", role.Id.ToString(), null,
            new { Name = name, Added = permissions.Except(from).ToList(), Removed = from.Except(permissions).ToList() }), now));
        await db.SaveChangesAsync(cancellationToken);

        var users = await db.UserRoles.AsNoTracking().Where(ur => ur.RoleId == role.Id).Select(ur => ur.UserId).ToListAsync(cancellationToken);
        await AccessChanges.ApplyAsync(_dbFactory, _bus, _logger, users, now, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Deletes a custom role that no user has any more.</summary>
    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid roleId, CancellationToken cancellationToken = default)
    {
        if (!caller.Has(Permissions.RolesManage))
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var role = await db.Roles.SingleOrDefaultAsync(r => r.Id == roleId, cancellationToken);
        if (role is null)
        {
            return ServiceResult.NotFound("role");
        }

        if (role.IsBuiltIn)
        {
            return ServiceResult.Fail(BuiltInProblem);
        }

        var permissions = await db.RolePermissions.Where(p => p.RoleId == role.Id).Select(p => p.Permission).ToListAsync(cancellationToken);
        if (RoleAuthority.MissingProblem(caller, permissions) is { } missing)
        {
            return ServiceResult.Fail(missing);
        }

        var users = await db.UserRoles.CountAsync(ur => ur.RoleId == role.Id, cancellationToken);
        if (users > 0)
        {
            return ServiceResult.Fail(users == 1
                ? "One user still has this role. Give that user another role first."
                : $"{users} users still have this role. Give them another role first.");
        }

        db.Roles.Remove(role);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.RoleDeleted, "Role", role.Id.ToString(), null,
            new { role.Name, Permissions = permissions }), _time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public const string BuiltInProblem = "A built-in role cannot be changed or deleted. Copy it into a new role and change that one.";

    private static async Task<(string? Name, string? Description, IReadOnlyList<string> Permissions, string? Problem)> ValidateAsync(
        FleetoDbContext db, Caller caller, Guid? roleId, RoleInput input, CancellationToken cancellationToken)
    {
        var name = ServiceSupport.Clean(input.Name);
        if (name is null || name.Length > RoleRules.MaxNameLength)
        {
            return Fail($"Enter a role name of at most {RoleRules.MaxNameLength} characters.");
        }

        var description = ServiceSupport.Clean(input.Description);
        if (description?.Length > RoleRules.MaxDescriptionLength)
        {
            return Fail($"The description can be at most {RoleRules.MaxDescriptionLength} characters.");
        }

        // A custom role named like a built-in one would be shown as two roles with one name.
        if (FleetoRoles.All.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(BuiltInRoles.DisplayName(r), name, StringComparison.OrdinalIgnoreCase)))
        {
            return Fail($"{name} is the name of a built-in role. Choose another name.");
        }

        var normalized = name.ToUpperInvariant();
        if (await db.Roles.AnyAsync(r => r.NormalizedName == normalized && r.Id != roleId, cancellationToken))
        {
            return Fail($"A role named {name} already exists. Choose another name.");
        }

        var permissions = input.Permissions.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (Permissions.Problem(permissions) is { } permissionProblem)
        {
            return Fail(permissionProblem);
        }

        if (RoleAuthority.MissingProblem(caller, permissions) is { } missing)
        {
            return Fail(missing);
        }

        return (name, description, permissions, null);

        static (string?, string?, IReadOnlyList<string>, string?) Fail(string problem) => (null, null, [], problem);
    }
}

/// <summary>
/// Keeps roles from becoming a way up (0.6.0): only an admin gives the admin role or changes an admin, and anybody else gives,
/// creates or changes only roles and users whose permissions it holds itself.
/// </summary>
internal static class RoleAuthority
{
    /// <summary>The permissions of each role: of this build for a built-in role, as stored for a custom one.</summary>
    public static async Task<IReadOnlyDictionary<Guid, IReadOnlyList<string>>> PermissionsOfAsync(FleetoDbContext db, IReadOnlyCollection<ApplicationRole> roles,
        CancellationToken cancellationToken)
    {
        var ids = roles.Select(r => r.Id).ToList();
        var stored = await db.RolePermissions.AsNoTracking().Where(p => ids.Contains(p.RoleId)).ToListAsync(cancellationToken);
        return roles.ToDictionary(r => r.Id, r => BuiltInRoles.PermissionsOf(r.Name!) ??
            (IReadOnlyList<string>)stored.Where(p => p.RoleId == r.Id).Select(p => p.Permission).Where(Permissions.IsKnown).Order(StringComparer.Ordinal).ToList());
    }

    /// <summary>Why the caller may not hand out these permissions: it does not hold one of them itself.</summary>
    public static string? MissingProblem(Caller caller, IEnumerable<string> permissions)
    {
        if (caller.IsAdmin)
        {
            return null;
        }

        var missing = permissions.Where(Permissions.IsKnown).FirstOrDefault(p => !caller.Has(p));
        return missing is null
            ? null
            : $"You cannot give the permission \"{Permissions.Find(missing)!.Label}\", because you do not have it yourself. Ask an admin.";
    }

    /// <summary>Why the caller may not give a user these roles: an unknown role, the admin role, or a permission it lacks itself.</summary>
    public static async Task<string?> GrantProblemAsync(FleetoDbContext db, Caller caller, IReadOnlyCollection<string> roleNames,
        CancellationToken cancellationToken)
    {
        if (roleNames.Count == 0)
        {
            return "Choose at least one role.";
        }

        var names = roleNames.Distinct().ToList();
        var roles = await db.Roles.AsNoTracking().Where(r => names.Contains(r.Name!)).ToListAsync(cancellationToken);
        if (roles.Count != names.Count)
        {
            return "A role you chose no longer exists. Refresh the page and try again.";
        }

        if (!caller.IsAdmin && names.Contains(FleetoRoles.Admin))
        {
            return "Only an admin can give the admin role.";
        }

        var permissions = await PermissionsOfAsync(db, roles, cancellationToken);
        return MissingProblem(caller, permissions.Values.SelectMany(p => p));
    }

    /// <summary>
    /// Why the caller may not change this user (its roles, groups, password, two-factor authentication or link, or delete it): it is
    /// an admin, or holds a permission the caller does not, and the caller is not an admin.
    /// </summary>
    public static async Task<string?> ManageProblemAsync(FleetoDbContext db, Caller caller, Guid userId, CancellationToken cancellationToken)
    {
        if (caller.IsAdmin)
        {
            return null;
        }

        var (roles, granted) = await UserPermissions.LoadAsync(db, userId, cancellationToken);
        if (roles.Contains(FleetoRoles.Admin))
        {
            return "Only an admin can change an admin.";
        }

        return granted.Where(Permissions.IsKnown).Any(p => !caller.Has(p))
            ? "This user has permissions you do not have, so only an admin can change it."
            : null;
    }
}
