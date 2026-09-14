using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Identity;
using Fleetify.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Services;

public sealed record UserListItem(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, bool TwoFactorEnabled, bool LockedOut,
    DateTime CreatedAt, DateTime? LastLoginAt);

/// <summary>
/// User administration (admins only). Every operation runs in its own service scope, so the Identity context is never
/// shared between concurrent events of a circuit. The last admin can never be deleted or demoted.
/// </summary>
public sealed class UserAdminService
{
    /// <summary>Serializes changes that could remove the last admin.</summary>
    internal const long AdminChangeLockKey = 0x466C742D41646D6E; // "Flt-Admn"

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly TimeProvider _time;

    public UserAdminService(IServiceScopeFactory scopeFactory, IFleetifyDbContextFactory dbFactory, TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _dbFactory = dbFactory;
        _time = time;
    }

    public async Task<IReadOnlyList<UserListItem>> ListAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow();
        var users = await db.Users.AsNoTracking().OrderBy(u => u.Email).ToListAsync(cancellationToken);
        var roles = await db.UserRoles.AsNoTracking()
            .Join(db.Roles, ur => ur.RoleId, r => r.Id, (ur, r) => new { ur.UserId, r.Name })
            .ToListAsync(cancellationToken);
        return users.Select(u => new UserListItem(u.Id, u.Email ?? string.Empty, u.DisplayName,
            roles.Where(r => r.UserId == u.Id).Select(r => r.Name!).OrderBy(r => r).ToList(),
            u.TwoFactorEnabled, u.LockoutEnd > now, u.CreatedAt, u.LastLoginAt)).ToList();
    }

    public async Task<ServiceResult<Guid>> CreateAsync(Caller caller, string? email, string? displayName, string? temporaryPassword,
        IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        var cleanEmail = ServiceSupport.Clean(email);
        if (!ServiceSupport.IsValidEmail(cleanEmail))
        {
            return ServiceResult<Guid>.Fail("Enter a valid email address.");
        }

        var cleanName = ServiceSupport.Clean(displayName);
        if (cleanName is null || cleanName.Length > 200)
        {
            return ServiceResult<Guid>.Fail("Enter a display name of at most 200 characters.");
        }

        if (RoleProblem(roles) is { } roleProblem)
        {
            return ServiceResult<Guid>.Fail(roleProblem);
        }

        if (AccountService.PasswordProblem(temporaryPassword) is { } passwordProblem)
        {
            return ServiceResult<Guid>.Fail(passwordProblem);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        if (await users.FindByEmailAsync(cleanEmail!) is not null)
        {
            return ServiceResult<Guid>.Fail("A user with this email address already exists.");
        }

        var db = scope.ServiceProvider.GetRequiredService<FleetifyDbContext>();
        var now = _time.GetUtcNow().UtcDateTime;
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = cleanEmail,
            Email = cleanEmail,
            EmailConfirmed = true,
            DisplayName = cleanName,
            CreatedAt = now
        };

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var created = await users.CreateAsync(user, temporaryPassword!);
        if (!created.Succeeded)
        {
            return ServiceResult<Guid>.Fail(Describe(created));
        }

        var added = await users.AddToRolesAsync(user, roles.Distinct());
        if (!added.Succeeded)
        {
            return ServiceResult<Guid>.Fail(Describe(added));
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.UserCreated, "User", user.Id.ToString(), null,
            new { user.Email, user.DisplayName, Roles = roles }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(user.Id);
    }

    public async Task<ServiceResult> SetRolesAsync(Caller caller, Guid userId, IReadOnlyCollection<string> roles, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        if (RoleProblem(roles) is { } roleProblem)
        {
            return ServiceResult.Fail(roleProblem);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<FleetifyDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdminChangeLockKey})", cancellationToken);

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("user");
        }

        var current = await users.GetRolesAsync(user);
        var wanted = roles.Distinct().ToList();
        if (current.Contains(FleetifyRoles.Admin) && !wanted.Contains(FleetifyRoles.Admin) && await CountAdminsAsync(db, cancellationToken) <= 1)
        {
            return ServiceResult.Fail("This is the last admin. Give another user the admin role first.");
        }

        var removed = await users.RemoveFromRolesAsync(user, current.Except(wanted));
        var added = removed.Succeeded ? await users.AddToRolesAsync(user, wanted.Except(current)) : removed;
        if (!added.Succeeded)
        {
            return ServiceResult.Fail(Describe(added));
        }

        // A new security stamp makes open sessions pick up the new roles at the next validation.
        await users.UpdateSecurityStampAsync(user);
        var now = _time.GetUtcNow().UtcDateTime;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.RolesChanged, "User", user.Id.ToString(), null,
            new { user.Email, From = current, To = wanted }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Turns two-factor authentication off and removes the key and recovery codes; the user sets it up again at the next sign-in.</summary>
    public async Task<ServiceResult> ResetTwoFactorAsync(Caller caller, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<FleetifyDbContext>();
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("user");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.RemoveAuthenticationTokenAsync(user, "[AspNetUserStore]", "AuthenticatorKey");
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        // Signs out every session of the user within the security stamp validation interval.
        await users.UpdateSecurityStampAsync(user);
        var now = _time.GetUtcNow().UtcDateTime;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TwoFactorReset, "User", user.Id.ToString(), null, new { user.Email }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    public async Task<ServiceResult> DeleteAsync(Caller caller, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        if (userId == caller.UserId)
        {
            return ServiceResult.Fail("You cannot delete your own account. Ask another admin to do it.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<FleetifyDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdminChangeLockKey})", cancellationToken);

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("user");
        }

        if (await users.IsInRoleAsync(user, FleetifyRoles.Admin) && await CountAdminsAsync(db, cancellationToken) <= 1)
        {
            return ServiceResult.Fail("This is the last admin and cannot be deleted. Give another user the admin role first.");
        }

        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return ServiceResult.Fail(Describe(result));
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.UserDeleted, "User", userId.ToString(), null, new { user.Email }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    private static Task<int> CountAdminsAsync(FleetifyDbContext db, CancellationToken cancellationToken)
    {
        var normalized = FleetifyRoles.Admin.ToUpperInvariant();
        return db.UserRoles.Join(db.Roles.Where(r => r.NormalizedName == normalized), ur => ur.RoleId, r => r.Id, (ur, _) => ur.UserId)
            .Distinct().CountAsync(cancellationToken);
    }

    private static string? RoleProblem(IReadOnlyCollection<string> roles)
    {
        if (roles.Count == 0)
        {
            return "Choose at least one role.";
        }

        return roles.All(FleetifyRoles.All.Contains) ? null : "Choose admin, technician or read-only.";
    }

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description)) is { Length: > 0 } text ? text : ServiceSupport.GenericProblem;
}
