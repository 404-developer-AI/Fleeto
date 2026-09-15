using System.Security.Cryptography;
using System.Text;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Security;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;

namespace Fleeto.Web.Security;

/// <summary>
/// Identity user store with the two-factor secrets protected at rest (CLAUDE.md, Secrets):
/// <list type="bullet">
/// <item>The authenticator (TOTP) key is encrypted with the secret protector, purpose Identity, bound to the user id,
/// so a copied row cannot be moved to another account.</item>
/// <item>Recovery codes are stored as SHA-256 hashes only; a code is shown once and can be redeemed once.</item>
/// </list>
/// </summary>
public sealed class FleetoUserStore : UserStore<ApplicationUser, ApplicationRole, FleetoDbContext, Guid>
{
    /// <summary>Token provider name Identity uses for its own tokens (authenticator key, recovery codes).</summary>
    private const string InternalLoginProvider = "[AspNetUserStore]";
    private const string AuthenticatorKeyTokenName = "AuthenticatorKey";
    private const string RecoveryCodeTokenName = "RecoveryCodes";

    private readonly ISecretProtector _protector;

    public FleetoUserStore(FleetoDbContext context, ISecretProtector protector, IdentityErrorDescriber? describer = null)
        : base(context, describer)
    {
        _protector = protector;
    }

    public override Task SetAuthenticatorKeyAsync(ApplicationUser user, string key, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var protectedKey = _protector.Protect(SecretPurposes.Identity, key, AuthenticatorKeyAssociatedData(user.Id));
        return SetTokenAsync(user, InternalLoginProvider, AuthenticatorKeyTokenName, protectedKey, cancellationToken);
    }

    public override async Task<string?> GetAuthenticatorKeyAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var stored = await GetTokenAsync(user, InternalLoginProvider, AuthenticatorKeyTokenName, cancellationToken);
        if (string.IsNullOrEmpty(stored))
        {
            return null;
        }

        return _protector.Unprotect(SecretPurposes.Identity, stored, AuthenticatorKeyAssociatedData(user.Id));
    }

    public override Task ReplaceCodesAsync(ApplicationUser user, IEnumerable<string> recoveryCodes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentNullException.ThrowIfNull(recoveryCodes);
        var hashes = string.Join(";", recoveryCodes.Select(HashRecoveryCode));
        return SetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, hashes, cancellationToken);
    }

    public override async Task<bool> RedeemCodeAsync(ApplicationUser user, string code, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var stored = await GetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, cancellationToken) ?? string.Empty;
        var hashes = stored.Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();
        var candidate = Encoding.ASCII.GetBytes(HashRecoveryCode(code));

        // Compare against every stored hash in constant time, so timing does not reveal a partial match.
        var index = -1;
        for (var i = 0; i < hashes.Count; i++)
        {
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(hashes[i]), candidate) && index < 0)
            {
                index = i;
            }
        }

        if (index < 0)
        {
            return false;
        }

        hashes.RemoveAt(index);
        await SetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, string.Join(";", hashes), cancellationToken);
        return true;
    }

    public override async Task<int> CountCodesAsync(ApplicationUser user, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(user);
        var stored = await GetTokenAsync(user, InternalLoginProvider, RecoveryCodeTokenName, cancellationToken) ?? string.Empty;
        return stored.Split(';', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// SHA-256 of the normalized code. Recovery codes are random and high-entropy, so a fast hash is enough; users may
    /// type them with or without the hyphen, in any case.
    /// </summary>
    internal static string HashRecoveryCode(string code)
    {
        var normalized = code.Replace("-", string.Empty, StringComparison.Ordinal).Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim().ToUpperInvariant();
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    internal static string AuthenticatorKeyAssociatedData(Guid userId) => "AuthenticatorKey|" + userId.ToString("D");
}
