using System.Net;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using QRCoder;

namespace Fleeto.Web.Services;

/// <summary>Password rules and two-factor setup helpers shared by the account endpoints and pages.</summary>
public static class AccountService
{
    public const int MinimumPasswordLength = 12;
    public const int MaximumPasswordLength = 256;

    /// <summary>Token (login provider, name) marking that fresh recovery codes still have to be shown once.</summary>
    public const string MarkerProvider = "[Fleeto]";
    public const string RecoveryCodesPendingToken = "RecoveryCodesPending";

    public const int RecoveryCodeCount = 10;

    private const string Issuer = "Fleeto";

    public static string? PasswordProblem(string? password)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinimumPasswordLength)
        {
            return $"Use a password of at least {MinimumPasswordLength} characters.";
        }

        return password.Length > MaximumPasswordLength ? $"Use a password of at most {MaximumPasswordLength} characters." : null;
    }

    /// <summary>Returns the user's authenticator key, creating one when none exists yet (the key is stored encrypted).</summary>
    public static async Task<string> GetOrCreateAuthenticatorKeyAsync(UserManager<ApplicationUser> users, ApplicationUser user)
    {
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key))
        {
            await users.ResetAuthenticatorKeyAsync(user);
            key = await users.GetAuthenticatorKeyAsync(user);
        }

        return key ?? throw new InvalidOperationException("No authenticator key could be created.");
    }

    public static string FormatKey(string key)
    {
        var builder = new StringBuilder();
        var upper = key.ToUpperInvariant();
        for (var i = 0; i < upper.Length; i += 4)
        {
            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(upper.AsSpan(i, Math.Min(4, upper.Length - i)));
        }

        return builder.ToString();
    }

    public static string BuildOtpAuthUri(string account, string key, string instanceName)
    {
        var label = WebUtility.UrlEncode($"{Issuer} ({instanceName})") + ":" + WebUtility.UrlEncode(account);
        return $"otpauth://totp/{label}?secret={key}&issuer={WebUtility.UrlEncode(Issuer)}&digits=6";
    }

    /// <summary>SVG QR code: crisp at any size and needs no script or external service.</summary>
    public static string BuildQrCodeSvg(string otpauthUri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(otpauthUri, QRCodeGenerator.ECCLevel.Q);
        return new SvgQRCode(data).GetGraphic(4, "#1C1917", "#FFFFFF", drawQuietZones: true, sizingMode: SvgQRCode.SizingMode.ViewBoxAttribute);
    }

    public static string NormalizeCode(string? code) =>
        (code ?? string.Empty).Replace(" ", string.Empty, StringComparison.Ordinal).Replace("-", string.Empty, StringComparison.Ordinal).Trim();
}

public enum SetupTokenState
{
    Valid,
    Invalid,
    Expired,
    Used,
    /// <summary>An admin exists: setup is complete and the link no longer does anything.</summary>
    AdminExists
}

/// <summary>Why the setup form was refused. A code rather than text, so a crafted link cannot put words on the page.</summary>
public enum SetupProblem
{
    None,
    Name,
    Email,
    Password,
    Mismatch,
    AccountRefused
}

public sealed record SetupResult(SetupTokenState State, SetupProblem Problem, ApplicationUser? User)
{
    public bool Success => State == SetupTokenState.Valid && Problem == SetupProblem.None && User is not null;

    public static string Describe(SetupProblem problem) => problem switch
    {
        SetupProblem.Name => "Enter your name, at most 200 characters.",
        SetupProblem.Email => "Enter a valid email address.",
        SetupProblem.Password => AccountService.PasswordProblem(null)!,
        SetupProblem.Mismatch => "The passwords do not match. Type the same password twice.",
        SetupProblem.AccountRefused => "The account could not be created with this email address and password. Check both and try again.",
        _ => string.Empty
    };
}

/// <summary>
/// First-admin setup from the one-time link printed by install.sh. The link works once, expires, and stops working as
/// soon as any admin exists.
/// </summary>
public sealed class SetupService
{
    private readonly UserManager<ApplicationUser> _users;
    private readonly FleetoDbContext _db;
    private readonly TimeProvider _time;

    public SetupService(UserManager<ApplicationUser> users, FleetoDbContext db, TimeProvider time)
    {
        _users = users;
        _db = db;
        _time = time;
    }

    public async Task<SetupTokenState> CheckAsync(string? token, CancellationToken cancellationToken = default)
    {
        if (await AdminExistsAsync(cancellationToken))
        {
            return SetupTokenState.AdminExists;
        }

        var (state, _) = await FindTokenAsync(token, tracked: false, cancellationToken);
        return state;
    }

    public async Task<SetupResult> CompleteAsync(string? token, string? displayName, string? email, string? password, string? confirmPassword,
        string? ipAddress, CancellationToken cancellationToken = default)
    {
        var cleanName = ServiceSupport.Clean(displayName);
        var cleanEmail = ServiceSupport.Clean(email);

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        // Serialize with every other change that creates or removes admins.
        await _db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({UserAdminService.AdminChangeLockKey})", cancellationToken);

        if (await AdminExistsAsync(cancellationToken))
        {
            return new SetupResult(SetupTokenState.AdminExists, SetupProblem.None, null);
        }

        var (state, row) = await FindTokenAsync(token, tracked: true, cancellationToken);
        if (state != SetupTokenState.Valid || row is null)
        {
            return new SetupResult(state, SetupProblem.None, null);
        }

        if (cleanName is null || cleanName.Length > 200)
        {
            return new SetupResult(state, SetupProblem.Name, null);
        }

        if (!ServiceSupport.IsValidEmail(cleanEmail))
        {
            return new SetupResult(state, SetupProblem.Email, null);
        }

        if (AccountService.PasswordProblem(password) is not null)
        {
            return new SetupResult(state, SetupProblem.Password, null);
        }

        if (!string.Equals(password, confirmPassword, StringComparison.Ordinal))
        {
            return new SetupResult(state, SetupProblem.Mismatch, null);
        }

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

        var created = await _users.CreateAsync(user, password!);
        if (!created.Succeeded)
        {
            return new SetupResult(state, SetupProblem.AccountRefused, null);
        }

        var role = await _users.AddToRoleAsync(user, FleetoRoles.Admin);
        if (!role.Succeeded)
        {
            return new SetupResult(state, SetupProblem.AccountRefused, null);
        }

        row.UsedAt = now;
        _db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.FirstAdminCreated, "User", user.Id.ToString(), null,
            AuditActorType.User, user.Id.ToString(), cleanName, new { user.Email }, ipAddress), now));
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new SetupResult(SetupTokenState.Valid, SetupProblem.None, user);
    }

    private async Task<bool> AdminExistsAsync(CancellationToken cancellationToken)
    {
        var normalized = FleetoRoles.Admin.ToUpperInvariant();
        return await _db.UserRoles.AnyAsync(ur => _db.Roles.Any(r => r.Id == ur.RoleId && r.NormalizedName == normalized), cancellationToken);
    }

    private async Task<(SetupTokenState State, SetupToken? Row)> FindTokenAsync(string? token, bool tracked, CancellationToken cancellationToken)
    {
        if (!OpaqueTokens.TryParse(token, OpaqueTokens.SetupPrefix, out var id, out var hash))
        {
            return (SetupTokenState.Invalid, null);
        }

        var query = tracked ? _db.SetupTokens : _db.SetupTokens.AsNoTracking();
        var row = await query.SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (row is null || !SecureEquals(row.TokenHash, hash))
        {
            return (SetupTokenState.Invalid, null);
        }

        if (row.UsedAt is not null)
        {
            return (SetupTokenState.Used, row);
        }

        return row.ExpiresAt <= _time.GetUtcNow().UtcDateTime ? (SetupTokenState.Expired, row) : (SetupTokenState.Valid, row);
    }

    private static bool SecureEquals(string a, string b) =>
        System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(a), Encoding.ASCII.GetBytes(b));
}
