using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

/// <summary>
/// The Entra ID sign-in of the instance without its secret (0.5.0).
/// </summary>
/// <param name="RedirectUri">The URI to register as a redirect URI of the app registration.</param>
/// <param name="LinkedUsers">How many users sign in with Entra ID today.</param>
public sealed record SignInView(bool Enabled, string TenantId, string ClientId, bool HasClientSecret, DateTime? ClientSecretExpiresAt,
    bool IsComplete, string RedirectUri, int LinkedUsers);

/// <param name="NewClientSecret">A new secret value; blank keeps the stored one.</param>
/// <param name="ClientSecretExpiresOn">The end date of the secret as the app registration shows it (a date, UTC).</param>
public sealed record SignInInput(bool Enabled, string? TenantId, string? ClientId, string? NewClientSecret, DateTime? ClientSecretExpiresOn);

/// <summary>
/// Sign-in with Microsoft Entra ID for admins (0.5.0). The client secret is write-only: it is stored encrypted and never
/// returned, and a blank field keeps the stored one. Fleeto never calls Microsoft from here — the workers do that when
/// somebody signs in.
/// </summary>
public sealed class SignInSettingsService
{
    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;

    public SignInSettingsService(IFleetoDbContextFactory dbFactory, SettingsStore settings, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _time = time;
    }

    public async Task<SignInView> GetAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        var settings = await _settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, cancellationToken) ?? new EntraSignInSettings();
        await using var db = _dbFactory.CreateSystem();
        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        var linked = await db.Users.AsNoTracking().CountAsync(u => u.EntraObjectId != null, cancellationToken);
        return new SignInView(settings.Enabled, settings.TenantId, settings.ClientId, !string.IsNullOrEmpty(settings.ClientSecret),
            settings.ClientSecretExpiresAt, settings.IsComplete, EntraSignIn.RedirectUri(instance.WebBaseUrl), linked);
    }

    /// <summary>
    /// Whether the sign-in page offers Entra ID. Read without a caller: the button is visible to everybody who can reach the
    /// sign-in page anyway, and it tells nothing about who has an account.
    /// </summary>
    public async Task<bool> IsEntraOfferedAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, cancellationToken);
        return settings?.IsUsable == true;
    }

    /// <summary>Saves the app registration of the sign-in. Turning it on needs a complete configuration.</summary>
    public async Task<ServiceResult> SaveAsync(Caller caller, SignInInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        var tenantId = ServiceSupport.Clean(input.TenantId);
        if (!MicrosoftIdentity.IsValidTenantId(tenantId))
        {
            return ServiceResult.Fail("Enter the directory (tenant) ID of the app registration, or the tenant domain such as contoso.onmicrosoft.com.");
        }

        var clientId = ServiceSupport.Clean(input.ClientId);
        if (!MicrosoftIdentity.IsValidClientId(clientId))
        {
            return ServiceResult.Fail("Enter the application (client) ID of the app registration. It looks like 00000000-0000-0000-0000-000000000000.");
        }

        if (input.NewClientSecret is { Length: > 1000 })
        {
            return ServiceResult.Fail("The client secret is at most 1000 characters. Copy the secret value, not the secret ID.");
        }

        var current = await _settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, cancellationToken) ?? new EntraSignInSettings();
        var secretChanged = !string.IsNullOrWhiteSpace(input.NewClientSecret);
        var secret = secretChanged ? input.NewClientSecret!.Trim() : current.ClientSecret;
        if (string.IsNullOrEmpty(secret))
        {
            return ServiceResult.Fail("Enter the client secret value of the app registration.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var expiresAt = current.ClientSecretExpiresAt;
        if (input.ClientSecretExpiresOn is { } expiresOn)
        {
            // The end of the chosen day, UTC: Entra ID shows the date only.
            expiresAt = DateTime.SpecifyKind(expiresOn.Date, DateTimeKind.Utc).AddDays(1).AddSeconds(-1);
        }

        if (expiresAt is null)
        {
            return ServiceResult.Fail("Enter the end date of the client secret, as shown under Certificates & secrets in the app registration.");
        }

        if (secretChanged && expiresAt <= now)
        {
            return ServiceResult.Fail("The end date of the new client secret has passed. Create a new secret and enter its end date.");
        }

        if (expiresAt > now.AddYears(5))
        {
            return ServiceResult.Fail("Enter the real end date of the client secret; it is at most a few years away.");
        }

        var updated = current with
        {
            Enabled = input.Enabled,
            TenantId = tenantId!,
            ClientId = clientId!,
            CredentialType = EntraCredentialType.ClientSecret,
            ClientSecret = secret,
            ClientSecretExpiresAt = expiresAt
        };

        if (updated.Enabled && !updated.IsComplete)
        {
            return ServiceResult.Fail("Fill in the tenant, the client id and the client secret before switching the sign-in on.");
        }

        await _settings.SetAsync(SettingKeys.EntraSignIn, updated, encrypted: true, caller.UserId, cancellationToken);
        await using var db = _dbFactory.CreateSystem();
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(secretChanged ? AuditActions.CredentialChanged : AuditActions.SignInConfigured,
            "Setting", SettingKeys.EntraSignIn, null,
            new { updated.Enabled, updated.TenantId, updated.ClientId, SecretChanged = secretChanged, updated.ClientSecretExpiresAt }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Turns the sign-in off without touching the configuration, and on again when it is complete.</summary>
    public async Task<ServiceResult> SetEnabledAsync(Caller caller, bool enabled, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        var current = await _settings.GetAsync<EntraSignInSettings>(SettingKeys.EntraSignIn, cancellationToken);
        if (current is null || !current.IsComplete)
        {
            return ServiceResult.Fail("Configure the tenant, the client id and the client secret before switching the sign-in on.");
        }

        if (current.Enabled == enabled)
        {
            return ServiceResult.Ok();
        }

        var updated = current with { Enabled = enabled };
        await _settings.SetAsync(SettingKeys.EntraSignIn, updated, encrypted: true, caller.UserId, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        await using var db = _dbFactory.CreateSystem();
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SignInConfigured, "Setting", SettingKeys.EntraSignIn, null,
            new { Enabled = enabled }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }
}
