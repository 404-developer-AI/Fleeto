using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <param name="EntraObjectId">The Entra ID account this user signs in with (0.5.0), or null for a local account.</param>
/// <param name="EntraAccount">The account name of the link, for display.</param>
public sealed record UserListItem(Guid Id, string Email, string DisplayName, IReadOnlyList<string> Roles, bool TwoFactorEnabled, bool LockedOut,
    DateTime CreatedAt, DateTime? LastLoginAt, Guid? EntraObjectId = null, string? EntraAccount = null);

/// <summary>
/// User administration (admins only). Every operation runs in its own service scope, so the Identity context is never
/// shared between concurrent events of a circuit. The last admin can never be deleted or demoted.
/// </summary>
public sealed class UserAdminService
{
    /// <summary>Serializes changes that could remove the last admin.</summary>
    internal const long AdminChangeLockKey = 0x466C742D41646D6E; // "Flt-Admn"

    /// <summary>Why the last admin that signs in with a password cannot be linked, demoted or deleted (0.5.0).</summary>
    internal const string LastLocalAdminProblem =
        "This is the last admin that signs in with a password of this instance. Keep one, so a problem at Microsoft cannot " +
        "lock everybody out; give another admin a password first.";

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;

    public UserAdminService(IServiceScopeFactory scopeFactory, IFleetoDbContextFactory dbFactory, SettingsStore settings, TimeProvider time)
    {
        _scopeFactory = scopeFactory;
        _dbFactory = dbFactory;
        _settings = settings;
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
            u.TwoFactorEnabled, u.LockoutEnd > now, u.CreatedAt, u.LastLoginAt, u.EntraObjectId, u.EntraAccount)).ToList();
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

        var db = scope.ServiceProvider.GetRequiredService<FleetoDbContext>();
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
        var db = scope.ServiceProvider.GetRequiredService<FleetoDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdminChangeLockKey})", cancellationToken);

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("user");
        }

        var current = await users.GetRolesAsync(user);
        var wanted = roles.Distinct().ToList();
        if (current.Contains(FleetoRoles.Admin) && !wanted.Contains(FleetoRoles.Admin))
        {
            if (await CountAdminsAsync(db, cancellationToken) <= 1)
            {
                return ServiceResult.Fail("This is the last admin. Give another user the admin role first.");
            }

            if (!user.IsLinkedToEntra && await CountLocalAdminsAsync(db, cancellationToken) <= 1)
            {
                return ServiceResult.Fail(LastLocalAdminProblem);
            }
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
        await AddApprovedScriptChangesAsync(db, user.Id, now, cancellationToken);
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
        var db = scope.ServiceProvider.GetRequiredService<FleetoDbContext>();
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
        await AddApprovedScriptChangesAsync(db, user.Id, now, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.TwoFactorReset, "User", user.Id.ToString(), null, new { user.Email }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Links a user to an Entra ID account (0.5.0), so it signs in with Microsoft instead of with a password. The object id
    /// comes from the user in the Entra ID portal: an admin enters it, because Fleeto reads nothing from the directory and
    /// never matches on an email address, which changes and can be given to somebody else.
    /// </summary>
    public async Task<ServiceResult> LinkEntraAsync(Caller caller, Guid userId, string? objectId, string? account,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        if (!Guid.TryParse(ServiceSupport.Clean(objectId), out var entraObjectId) || entraObjectId == Guid.Empty)
        {
            return ServiceResult.Fail("Enter the object ID of the user in the Entra ID portal. It looks like 00000000-0000-0000-0000-000000000000.");
        }

        var cleanAccount = ServiceSupport.Clean(account);
        if (cleanAccount is { Length: > 320 })
        {
            return ServiceResult.Fail("The account name is at most 320 characters.");
        }

        var settings = await _settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, cancellationToken);
        if (settings is null || !settings.IsComplete)
        {
            return ServiceResult.Fail("Sign-in with Microsoft Entra ID is not configured yet. Configure it in Settings, Sign-in first.");
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<FleetoDbContext>();
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("user");
        }

        var takenBy = await db.Users.AsNoTracking()
            .Where(u => u.EntraObjectId == entraObjectId && u.Id != userId)
            .Select(u => u.Email)
            .FirstOrDefaultAsync(cancellationToken);
        if (takenBy is not null)
        {
            return ServiceResult.Fail($"That Entra ID account is already linked to {takenBy}. Remove that link first.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdminChangeLockKey})", cancellationToken);

        // Break-glass: one admin keeps a password and an authenticator, so an outage at Microsoft cannot lock the customer
        // out of its own instance.
        if (!user.IsLinkedToEntra && await users.IsInRoleAsync(user, FleetoRoles.Admin) &&
            await CountLocalAdminsAsync(db, cancellationToken) <= 1)
        {
            return ServiceResult.Fail(LastLocalAdminProblem);
        }

        var now = _time.GetUtcNow().UtcDateTime;
        user.EntraObjectId = entraObjectId;
        // The tenant of the instance, when it is configured as a directory id; the sign-in records the tenant it proved.
        user.EntraTenantId = Guid.TryParse(settings.TenantId, out var tenant) ? tenant.ToString("D") : null;
        user.EntraAccount = cleanAccount;
        user.EntraLinkedAt = now;
        var update = await users.UpdateAsync(user);
        if (!update.Succeeded)
        {
            return ServiceResult.Fail(Describe(update));
        }

        // A linked user has no local password: one way in, and one place to close the door. Removing it also changes the
        // security stamp, so sessions that signed in with the password end within the validation interval.
        var passwordRemoved = await users.HasPasswordAsync(user);
        if (passwordRemoved)
        {
            var removed = await users.RemovePasswordAsync(user);
            if (!removed.Succeeded)
            {
                return ServiceResult.Fail(Describe(removed));
            }
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.UserLinked, "User", user.Id.ToString(), null,
            new { user.Email, EntraObjectId = entraObjectId, Account = cleanAccount, PasswordRemoved = passwordRemoved }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Removes the Entra ID link, after which the user signs in with a local account again.</summary>
    public async Task<ServiceResult> UnlinkEntraAsync(Caller caller, Guid userId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<FleetoDbContext>();
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("user");
        }

        if (!user.IsLinkedToEntra)
        {
            return ServiceResult.Ok();
        }

        var linked = user.EntraObjectId;
        user.EntraObjectId = null;
        user.EntraTenantId = null;
        user.EntraAccount = null;
        user.EntraLinkedAt = null;
        var update = await users.UpdateAsync(user);
        if (!update.Succeeded)
        {
            return ServiceResult.Fail(Describe(update));
        }

        var now = _time.GetUtcNow().UtcDateTime;
        // The link was the only way in; the user signs in again once an admin has set a password.
        await users.UpdateSecurityStampAsync(user);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.UserUnlinked, "User", user.Id.ToString(), null,
            new { user.Email, EntraObjectId = linked, HasPassword = await users.HasPasswordAsync(user) }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Gives a user a new password, which they sign in with at once (0.5.0). Needed for a user that was unlinked from Entra
    /// ID, and for the reset the sign-in page points at. Two-factor authentication is untouched: a password alone is never
    /// enough.
    /// </summary>
    public async Task<ServiceResult> SetPasswordAsync(Caller caller, Guid userId, string? password, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        if (AccountService.PasswordProblem(password) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var db = scope.ServiceProvider.GetRequiredService<FleetoDbContext>();
        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("user");
        }

        if (user.IsLinkedToEntra)
        {
            return ServiceResult.Fail("This user signs in with Microsoft Entra ID and has no password. Remove the link first.");
        }

        if (await users.HasPasswordAsync(user))
        {
            var removed = await users.RemovePasswordAsync(user);
            if (!removed.Succeeded)
            {
                return ServiceResult.Fail(Describe(removed));
            }
        }

        var added = await users.AddPasswordAsync(user, password!);
        if (!added.Succeeded)
        {
            return ServiceResult.Fail(Describe(added));
        }

        // Every session of this user ends within the security stamp validation interval.
        await users.UpdateSecurityStampAsync(user);
        var now = _time.GetUtcNow().UtcDateTime;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.PasswordChanged, "User", user.Id.ToString(), null,
            new { user.Email, By = "admin" }), now));
        await db.SaveChangesAsync(cancellationToken);
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
        var db = scope.ServiceProvider.GetRequiredService<FleetoDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdminChangeLockKey})", cancellationToken);

        var user = await users.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            return ServiceResult.NotFound("user");
        }

        if (await users.IsInRoleAsync(user, FleetoRoles.Admin))
        {
            if (await CountAdminsAsync(db, cancellationToken) <= 1)
            {
                return ServiceResult.Fail("This is the last admin and cannot be deleted. Give another user the admin role first.");
            }

            if (!user.IsLinkedToEntra && await CountLocalAdminsAsync(db, cancellationToken) <= 1)
            {
                return ServiceResult.Fail(LastLocalAdminProblem);
            }
        }

        var result = await users.DeleteAsync(user);
        if (!result.Succeeded)
        {
            return ServiceResult.Fail(Describe(result));
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await AddApprovedScriptChangesAsync(db, userId, now, cancellationToken);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.UserDeleted, "User", userId.ToString(), null, new { user.Email }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Script checks run the newest version approved by a current admin with two-factor authentication, so a change to an approver
    /// re-signs the configurations that use the scripts they approved.
    /// </summary>
    private static async Task AddApprovedScriptChangesAsync(FleetoDbContext db, Guid userId, DateTime now, CancellationToken cancellationToken)
    {
        var scriptIds = await db.ScriptVersions.IgnoreQueryFilters().AsNoTracking()
            .Where(v => v.ApprovedByUserId == userId)
            .Select(v => v.ScriptId)
            .Distinct()
            .ToListAsync(cancellationToken);
        db.ConfigChangeEvents.AddRange(scriptIds.Select(id => ScriptChecks.ChangeEvent(id, now)));
    }

    private static Task<int> CountAdminsAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var normalized = FleetoRoles.Admin.ToUpperInvariant();
        return db.UserRoles.Join(db.Roles.Where(r => r.NormalizedName == normalized), ur => ur.RoleId, r => r.Id, (ur, _) => ur.UserId)
            .Distinct().CountAsync(cancellationToken);
    }

    /// <summary>
    /// Admins that sign in with a password of this instance (0.5.0): not linked to Entra ID and holding a password. One of
    /// them must always remain, so a problem at Microsoft never locks the customer out of its own instance.
    /// </summary>
    private static Task<int> CountLocalAdminsAsync(FleetoDbContext db, CancellationToken cancellationToken)
    {
        var normalized = FleetoRoles.Admin.ToUpperInvariant();
        return db.UserRoles.Join(db.Roles.Where(r => r.NormalizedName == normalized), ur => ur.RoleId, r => r.Id, (ur, _) => ur.UserId)
            .Distinct()
            .Join(db.Users.Where(u => u.EntraObjectId == null && u.PasswordHash != null), id => id, u => u.Id, (id, _) => id)
            .CountAsync(cancellationToken);
    }

    private static string? RoleProblem(IReadOnlyCollection<string> roles)
    {
        if (roles.Count == 0)
        {
            return "Choose at least one role.";
        }

        return roles.All(FleetoRoles.All.Contains) ? null : "Choose admin, technician or read-only.";
    }

    private static string Describe(IdentityResult result) =>
        string.Join(" ", result.Errors.Select(e => e.Description)) is { Length: > 0 } text ? text : ServiceSupport.GenericProblem;
}
