using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Fleeto.Infrastructure.Identity;
using Fleeto.Web.Security;
using Microsoft.AspNetCore.Identity;

namespace Fleeto.Web.Services;

/// <summary>
/// A fresh code of the Fleeto authenticator as proof that this user, now, takes an action that reaches many endpoints (0.9.0): the
/// same rule as approving a script (<see cref="ScriptService.ApproveAsync"/>). A code is used once per purpose: the hash of the last
/// one is kept as an authentication token of the user, so a code seen over a shoulder cannot be replayed within its window.
/// </summary>
internal static class FreshTotp
{
    /// <summary>Checks <paramref name="code"/> for the caller. Returns null when it is valid, otherwise the problem in words.</summary>
    /// <param name="purpose">The name of the token that remembers the last code, one per kind of action.</param>
    /// <param name="action">What the code is for, as in "Choosing the patch management".</param>
    public static async Task<string?> VerifyAsync(IServiceScopeFactory scopeFactory, Caller caller, string? code, string purpose, string action,
        DateTimeOffset now)
    {
        var normalized = new string((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
        if (normalized.Length != 6)
        {
            return "Enter the 6-digit code from your authenticator app.";
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var user = await users.FindByIdAsync(caller.UserId.ToString());
        if (user is null || !user.TwoFactorEnabled)
        {
            return user?.IsLinkedToEntra == true
                ? $"{action} asks a code from the Fleeto authenticator app, also when you sign in with Microsoft. Set one up at /account/setup-2fa, then try again."
                : "Your account has no two-factor authentication. Set it up, then try again.";
        }

        if (await users.IsLockedOutAsync(user))
        {
            return "Your account is locked after too many wrong codes. Try again later.";
        }

        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(caller.UserId.ToString("N") + ":" + normalized)));
        var last = await users.GetAuthenticationTokenAsync(user, ScriptService.ApprovalTokenProvider, purpose);
        if (last?.Split('|') is [var lastHash, var lastTicks] && lastHash == hash &&
            long.TryParse(lastTicks, NumberStyles.None, CultureInfo.InvariantCulture, out var ticks) &&
            now.UtcTicks - ticks < ScriptService.CodeReuseWindow.Ticks)
        {
            return "This code was already used. Wait for the next code in your authenticator app.";
        }

        if (!await users.VerifyTwoFactorTokenAsync(user, users.Options.Tokens.AuthenticatorTokenProvider, normalized))
        {
            await users.AccessFailedAsync(user);
            return "The code is not valid. Enter the current code from your authenticator app.";
        }

        await users.ResetAccessFailedCountAsync(user);
        await users.SetAuthenticationTokenAsync(user, ScriptService.ApprovalTokenProvider, purpose,
            hash + "|" + now.UtcTicks.ToString(CultureInfo.InvariantCulture));
        return null;
    }
}
