using Fleeto.Core.Domain;
using Fleeto.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Security;

/// <summary>
/// The roles of a user and the permissions they give together (0.6.0), read from the database. A built-in role counts with the
/// permissions of this build (<see cref="BuiltInRoles"/>), a custom role with the permissions stored for it.
/// </summary>
public static class UserPermissions
{
    public static async Task<(IReadOnlyList<string> Roles, IReadOnlySet<string> Granted)> LoadAsync(FleetoDbContext db, Guid userId,
        CancellationToken cancellationToken)
    {
        var roles = await db.UserRoles.AsNoTracking()
            .Where(ur => ur.UserId == userId)
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (_, r) => new { r.Id, Name = r.Name! })
            .ToListAsync(cancellationToken);
        var granted = roles.SelectMany(r => BuiltInRoles.PermissionsOf(r.Name) ?? []).ToHashSet(StringComparer.Ordinal);
        var custom = roles.Where(r => !BuiltInRoles.IsBuiltIn(r.Name)).Select(r => r.Id).ToList();
        if (custom.Count > 0)
        {
            granted.UnionWith(await db.RolePermissions.AsNoTracking()
                .Where(p => custom.Contains(p.RoleId))
                .Select(p => p.Permission)
                .ToListAsync(cancellationToken));
        }

        return (roles.Select(r => r.Name).ToList(), granted);
    }
}
