using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Fleeto.Infrastructure.Identity;

/// <summary>How the app registration of the sign-in proves who it is (0.5.0).</summary>
public enum EntraCredentialType
{
    ClientSecret
}

/// <summary>
/// Sign-in with Microsoft Entra ID for one instance (0.5.0): the app registration of the customer that owns the instance.
/// The secret is write-only in the UI and the whole record is stored encrypted, like every other credential.
/// </summary>
public sealed record EntraSignInSettings
{
    /// <summary>False keeps the configuration but hides the button and refuses every Entra ID sign-in.</summary>
    public bool Enabled { get; init; }

    /// <summary>The one tenant of this instance. A token from another tenant is refused.</summary>
    public string TenantId { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public EntraCredentialType CredentialType { get; init; } = EntraCredentialType.ClientSecret;

    public string? ClientSecret { get; init; }

    /// <summary>The end date entered with the secret; Entra ID does not reveal it to the application.</summary>
    public DateTime? ClientSecretExpiresAt { get; init; }

    /// <summary>The expiry of the credential in use, or null when there is none.</summary>
    [JsonIgnore]
    public DateTime? CredentialExpiresAt => string.IsNullOrEmpty(ClientSecret) ? null : ClientSecretExpiresAt;

    /// <summary>Everything a sign-in needs is present.</summary>
    [JsonIgnore]
    public bool IsComplete => MicrosoftIdentity.IsValidTenantId(TenantId) && MicrosoftIdentity.IsValidClientId(ClientId) &&
                              !string.IsNullOrEmpty(ClientSecret);

    /// <summary>Configured, complete and switched on: the sign-in page offers Entra ID.</summary>
    [JsonIgnore]
    public bool IsUsable => Enabled && IsComplete;
}

/// <summary>What web asks the workers to exchange. Encrypted in the row; never in a log or an audit entry.</summary>
public sealed record SignInExchangeRequest(string Code, string CodeVerifier);

/// <summary>
/// What the workers validated and write back: what Fleeto needs to recognise the account, and nothing else. No token, no
/// refresh token, no group or directory data.
/// </summary>
/// <param name="ObjectId">The <c>oid</c> claim: the account in its tenant, immutable.</param>
/// <param name="TenantId">The <c>tid</c> claim, checked against the configured tenant before this is written.</param>
/// <param name="Account">The account name of the token (<c>preferred_username</c>), for display in Settings and the audit log.</param>
/// <param name="DisplayName">The <c>name</c> claim, for display.</param>
/// <param name="MfaProven">The <c>amr</c> claim names multi-factor authentication; false whenever it does not say so.</param>
/// <param name="Nonce">The nonce of the token, which web compares with the one it started the sign-in with.</param>
public sealed record SignInClaims(string ObjectId, string TenantId, string? Account, string? DisplayName, bool MfaProven, string? Nonce);

/// <summary>
/// The sign-in flow with Entra ID (0.5.0): the authorization code flow with PKCE. Web builds the redirect to Microsoft and
/// receives the code; the workers exchange it, because web has no outbound access (ARCHITECTURE §1). This class holds what
/// both sides need so neither can drift from the other.
/// </summary>
public static class EntraSignIn
{
    /// <summary>Only what a sign-in needs: who the person is. Fleeto asks for no access to anything in the tenant.</summary>
    public const string Scope = "openid profile email";

    /// <summary>The path Microsoft redirects back to; it is registered in the app registration.</summary>
    public const string CallbackPath = "/api/account/entra/callback";

    /// <summary>How long a started sign-in may take before its row is worthless.</summary>
    public static readonly TimeSpan ExchangeLifetime = TimeSpan.FromMinutes(10);

    /// <summary>How long web waits for the workers to finish an exchange before it gives up.</summary>
    public static readonly TimeSpan ExchangeWait = TimeSpan.FromSeconds(20);

    /// <summary>The <c>amr</c> values Entra ID uses for a second factor.</summary>
    public static readonly IReadOnlySet<string> MultiFactorMethods =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mfa", "otp", "fido", "hwk", "sms", "phr", "phm" };

    public static string RedirectUri(string webBaseUrl) => webBaseUrl.TrimEnd('/') + CallbackPath;

    /// <summary>A random value for the state, the nonce and the PKCE verifier: 256 bits, URL-safe.</summary>
    public static string NewRandomValue() => Base64Url(RandomNumberGenerator.GetBytes(32));

    /// <summary>The S256 challenge of a PKCE verifier (RFC 7636).</summary>
    public static string CodeChallenge(string verifier) => Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));

    /// <summary>Where the browser is sent to sign in. Nothing secret travels in it.</summary>
    public static string AuthorizeUrl(EntraSignInSettings settings, string redirectUri, string state, string nonce, string codeChallenge)
    {
        var query = new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["response_type"] = "code",
            ["redirect_uri"] = redirectUri,
            ["response_mode"] = "query",
            ["scope"] = Scope,
            ["state"] = state,
            ["nonce"] = nonce,
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            // The account picker of the configured tenant only; a personal Microsoft account is not a tenant member.
            ["prompt"] = "select_account"
        };
        return MicrosoftIdentity.AuthorizeEndpoint(settings.TenantId) + "?" +
               string.Join('&', query.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
