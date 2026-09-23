namespace Fleeto.Infrastructure.Identity;

/// <summary>
/// The claims of an id_token that Fleeto looks at, read from a token whose signature, audience and lifetime are already
/// validated. Separated from the token library so the rules below can be tested without building tokens.
/// </summary>
/// <param name="Issuer">The <c>iss</c> claim.</param>
/// <param name="TenantId">The <c>tid</c> claim.</param>
/// <param name="ObjectId">The <c>oid</c> claim.</param>
/// <param name="AuthenticationMethods">The <c>amr</c> claim, empty when the token does not have it.</param>
/// <param name="IdentityProvider">The <c>idp</c> claim: present and different from the issuer for a guest of the tenant.</param>
/// <param name="AccountType">The <c>acct</c> claim: 0 for a member of the tenant, 1 for a guest.</param>
public sealed record SignInTokenFacts(
    string? Issuer,
    string? TenantId,
    string? ObjectId,
    IReadOnlyList<string> AuthenticationMethods,
    string? IdentityProvider,
    int? AccountType,
    string? Nonce,
    string? Account,
    string? DisplayName);

/// <summary>Either the claims Fleeto keeps, or why the token is refused.</summary>
public sealed record SignInTokenOutcome(SignInClaims? Claims, string? Refusal)
{
    public static SignInTokenOutcome Accept(SignInClaims claims) => new(claims, null);

    public static SignInTokenOutcome Refuse(string refusal) => new(null, refusal);
}

/// <summary>
/// What Fleeto demands of an Entra ID token beyond signature, audience and lifetime (0.5.0): the token comes from the one
/// tenant of this instance, the account is a member of that tenant and not a guest, and it names the account. Sign-in with
/// Entra ID is for the customer that owns the instance; everybody else uses a local account (CLAUDE.md, Sign-in).
/// </summary>
public static class SignInTokenRules
{
    public static SignInTokenOutcome Check(SignInTokenFacts facts, string expectedIssuer)
    {
        if (!string.Equals(facts.Issuer, expectedIssuer, StringComparison.Ordinal))
        {
            return SignInTokenOutcome.Refuse("The sign-in came from another Microsoft tenant than the one this instance is configured for.");
        }

        var expectedTenantId = TenantIdOf(expectedIssuer);
        if (expectedTenantId is null || !string.Equals(facts.TenantId, expectedTenantId, StringComparison.OrdinalIgnoreCase))
        {
            return SignInTokenOutcome.Refuse("The sign-in came from another Microsoft tenant than the one this instance is configured for.");
        }

        // A guest of the tenant is an external identity: the people of the clients this customer manages sign in with a
        // local account. Entra ID marks a guest with acct=1, and names the tenant that authenticated it in idp.
        var guestByAccountType = facts.AccountType is 1;
        var guestByProvider = facts.IdentityProvider is { Length: > 0 } idp && !string.Equals(idp, facts.Issuer, StringComparison.Ordinal);
        if (guestByAccountType || guestByProvider)
        {
            return SignInTokenOutcome.Refuse("This account is a guest of the Microsoft tenant. A guest signs in to Fleeto with a local account.");
        }

        if (!Guid.TryParse(facts.ObjectId, out var objectId) || objectId == Guid.Empty)
        {
            return SignInTokenOutcome.Refuse("The sign-in did not name the account. Check that the app registration asks for the profile scope.");
        }

        var mfaProven = facts.AuthenticationMethods.Any(EntraSignIn.MultiFactorMethods.Contains);
        return SignInTokenOutcome.Accept(new SignInClaims(objectId.ToString("D"), facts.TenantId!, Trim(facts.Account, 320),
            Trim(facts.DisplayName, 200), mfaProven, facts.Nonce));
    }

    /// <summary>The tenant of an issuer such as <c>https://login.microsoftonline.com/&lt;tenant&gt;/v2.0</c>.</summary>
    public static string? TenantIdOf(string? issuer)
    {
        if (!Uri.TryCreate(issuer, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var segments = uri.Segments.Select(s => s.Trim('/')).Where(s => s.Length > 0).ToArray();
        return segments.Length > 0 && Guid.TryParse(segments[0], out var tenant) ? tenant.ToString("D") : null;
    }

    private static string? Trim(string? value, int max) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Length <= max ? value : value[..max];
}
