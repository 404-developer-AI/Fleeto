namespace Fleeto.Core.Entities;

/// <summary>Where an exchange of an authorization code stands (0.5.0).</summary>
public enum SignInExchangeState
{
    /// <summary>Web wrote the code; the workers have not finished it yet.</summary>
    Requested,

    /// <summary>The workers exchanged the code and wrote the claims they validated.</summary>
    Completed,

    /// <summary>The exchange failed; <see cref="SignInExchange.FailureReason"/> says why, without secrets.</summary>
    Failed
}

/// <summary>
/// One authorization code a sign-in with Microsoft Entra ID waits for (0.5.0). fleeto-web has no outbound access, so it
/// writes the code and the PKCE verifier here — encrypted and bound to this row — and the workers exchange them at the
/// token endpoint, validate the id_token and write back only the claims Fleeto needs, never a token. A row lives for the
/// seconds a sign-in takes and is deleted as soon as it is used; retention removes what an abandoned browser left behind.
/// </summary>
public class SignInExchange
{
    public Guid Id { get; set; }

    public DateTime CreatedAt { get; set; }

    /// <summary>The authorization code and the PKCE verifier as JSON, encrypted and bound to this row.</summary>
    public string EncryptedRequest { get; set; } = string.Empty;

    /// <summary>The redirect URI the code was issued for; the token request must repeat it exactly.</summary>
    public string RedirectUri { get; set; } = string.Empty;

    public SignInExchangeState State { get; set; } = SignInExchangeState.Requested;

    /// <summary>
    /// The claims the workers validated, as JSON, encrypted and bound to this row: the object id, the tenant, the account
    /// name, whether the token proves multi-factor authentication and the nonce web has to recognise.
    /// </summary>
    public string? EncryptedClaims { get; set; }

    /// <summary>Cause and next step of a failure, safe to show to the person signing in. Never a token, secret or code.</summary>
    public string? FailureReason { get; set; }

    public DateTime? CompletedAt { get; set; }
}
