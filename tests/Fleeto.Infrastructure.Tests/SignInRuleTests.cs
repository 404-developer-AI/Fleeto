using Fleeto.Infrastructure.Identity;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Guarantees the sign-in with Microsoft Entra ID (0.5.0): the flow web builds (PKCE, state, nonce) and what Fleeto demands
/// of a token beyond signature and lifetime — the one tenant of the instance, a member of it rather than a guest, and an
/// account it names. Sign-in with Entra ID is for the customer that owns the instance; everybody else uses a local account.
/// </summary>
public sealed class SignInRuleTests
{
    private const string Tenant = "8fa1c6b2-91d2-4b4e-9a0e-2f3c4d5e6a7b";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    private static SignInTokenFacts Facts(
        string? issuer = null,
        string? tenantId = Tenant,
        string? objectId = "a1b2c3d4-e5f6-4789-9abc-def012345678",
        IReadOnlyList<string>? amr = null,
        string? idp = null,
        int? acct = 0,
        string? nonce = "nonce-value",
        string? account = "tech@contoso.com",
        string? name = "A Technician") =>
        new(issuer ?? Issuer, tenantId, objectId, amr ?? ["pwd"], idp, acct, nonce, account, name);

    [Fact]
    public void How_Microsoft_says_the_person_signed_in_is_kept_for_the_audit_log_as_short_names_only()
    {
        var facts = Facts(amr: ["pwd", "mfa", "not a name", new string('x', 41)]) with
        {
            AuthenticationClass = "1",
            AuthenticationContexts = ["c1", "<script>"]
        };

        var claims = SignInTokenRules.Check(facts, Issuer).Claims!;

        Assert.Equal(["pwd", "mfa"], claims.Methods);
        Assert.Equal("1", claims.AuthenticationClass);
        Assert.Equal(["c1"], claims.AuthenticationContexts);
        Assert.True(claims.MfaProven);

        // A token without them leaves them empty rather than guessing.
        var bare = SignInTokenRules.Check(Facts(amr: []), Issuer).Claims!;
        Assert.Empty(bare.Methods);
        Assert.Null(bare.AuthenticationClass);
        Assert.Empty(bare.AuthenticationContexts);
        Assert.False(bare.MfaProven);
    }

    [Fact]
    public void The_second_factor_comes_from_the_token_or_from_the_setting_and_otherwise_from_Fleeto()
    {
        var proven = SignInTokenRules.Check(Facts(amr: ["pwd", "mfa"]), Issuer).Claims!;
        var single = SignInTokenRules.Check(Facts(amr: []), Issuer).Claims!;
        var strict = new EntraSignInSettings();
        var delegated = new EntraSignInSettings { MicrosoftHandlesSecondFactor = true };

        Assert.Equal(EntraSignIn.ProvenSecondFactor, EntraSignIn.SecondFactorSource(proven, strict));
        // A token that proves it wins over the setting, so the audit log says which of the two let the person in.
        Assert.Equal(EntraSignIn.ProvenSecondFactor, EntraSignIn.SecondFactorSource(proven, delegated));
        Assert.Equal(EntraSignIn.DelegatedSecondFactor, EntraSignIn.SecondFactorSource(single, delegated));
        // Off by default: Fleeto asks its own code.
        Assert.Null(EntraSignIn.SecondFactorSource(single, strict));
    }

    [Fact]
    public void A_token_of_the_configured_tenant_is_accepted()
    {
        var outcome = SignInTokenRules.Check(Facts(), Issuer);

        Assert.Null(outcome.Refusal);
        var claims = Assert.IsType<SignInClaims>(outcome.Claims);
        Assert.Equal("a1b2c3d4-e5f6-4789-9abc-def012345678", claims.ObjectId);
        Assert.Equal(Tenant, claims.TenantId);
        Assert.Equal("tech@contoso.com", claims.Account);
        Assert.Equal("nonce-value", claims.Nonce);
        // amr says pwd only: the sign-in has not proven a second factor.
        Assert.False(claims.MfaProven);
    }

    [Theory]
    [InlineData("mfa")]
    [InlineData("otp")]
    [InlineData("fido")]
    public void Multi_factor_is_read_from_the_amr_claim(string method)
    {
        var outcome = SignInTokenRules.Check(Facts(amr: ["pwd", method]), Issuer);

        Assert.True(Assert.IsType<SignInClaims>(outcome.Claims).MfaProven);
    }

    [Fact]
    public void A_token_without_amr_never_counts_as_multi_factor()
    {
        var outcome = SignInTokenRules.Check(Facts(amr: []), Issuer);

        Assert.False(Assert.IsType<SignInClaims>(outcome.Claims).MfaProven);
    }

    [Fact]
    public void A_token_of_another_tenant_is_refused()
    {
        var other = "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555/v2.0";

        Assert.Null(SignInTokenRules.Check(Facts(issuer: other, tenantId: "11111111-2222-3333-4444-555555555555"), Issuer).Claims);
        // The tid claim must match the issuer as well: an issuer of the right tenant with another tid is refused.
        Assert.Null(SignInTokenRules.Check(Facts(tenantId: "11111111-2222-3333-4444-555555555555"), Issuer).Claims);
    }

    [Fact]
    public void A_guest_of_the_tenant_is_refused()
    {
        var byAccountType = SignInTokenRules.Check(Facts(acct: 1), Issuer);
        var byProvider = SignInTokenRules.Check(Facts(idp: "https://sts.windows.net/99999999-8888-7777-6666-555555555555/"), Issuer);

        Assert.Null(byAccountType.Claims);
        Assert.Contains("guest", byAccountType.Refusal, StringComparison.OrdinalIgnoreCase);
        Assert.Null(byProvider.Claims);
        // A member whose idp is its own issuer stays welcome.
        Assert.NotNull(SignInTokenRules.Check(Facts(idp: Issuer), Issuer).Claims);
    }

    [Fact]
    public void A_token_that_does_not_name_the_account_is_refused()
    {
        Assert.Null(SignInTokenRules.Check(Facts(objectId: null), Issuer).Claims);
        Assert.Null(SignInTokenRules.Check(Facts(objectId: "not-a-guid"), Issuer).Claims);
        Assert.Null(SignInTokenRules.Check(Facts(objectId: Guid.Empty.ToString("D")), Issuer).Claims);
    }

    [Fact]
    public void The_tenant_of_an_issuer_is_read_from_it()
    {
        Assert.Equal(Tenant, SignInTokenRules.TenantIdOf(Issuer));
        Assert.Null(SignInTokenRules.TenantIdOf("https://login.microsoftonline.com/common/v2.0"));
        Assert.Null(SignInTokenRules.TenantIdOf("not a url"));
        Assert.Null(SignInTokenRules.TenantIdOf(null));
    }

    [Fact]
    public void The_authorize_url_carries_pkce_state_and_nonce_and_no_secret()
    {
        var settings = new EntraSignInSettings
        {
            Enabled = true,
            TenantId = Tenant,
            ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d",
            ClientSecret = "super-secret-value",
            ClientSecretExpiresAt = DateTime.UtcNow.AddDays(90)
        };
        var verifier = EntraSignIn.NewRandomValue();

        var url = EntraSignIn.AuthorizeUrl(settings, "https://rmm.contoso.example/api/account/entra/callback", "state-value", "nonce-value",
            EntraSignIn.CodeChallenge(verifier));

        Assert.StartsWith($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("state=state-value", url);
        Assert.Contains("nonce=nonce-value", url);
        Assert.Contains("client_id=" + settings.ClientId, url);
        Assert.Contains("redirect_uri=https%3A%2F%2Frmm.contoso.example%2Fapi%2Faccount%2Fentra%2Fcallback", url);
        Assert.DoesNotContain("super-secret-value", url);
        // The verifier stays with Fleeto; only its challenge travels.
        Assert.DoesNotContain(verifier, url);
    }

    [Fact]
    public void The_code_challenge_is_the_s256_of_the_verifier()
    {
        // RFC 7636 appendix B.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            EntraSignIn.CodeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
    }

    [Fact]
    public void Random_values_are_long_and_never_repeat()
    {
        var values = Enumerable.Range(0, 50).Select(_ => EntraSignIn.NewRandomValue()).ToList();

        Assert.All(values, value => Assert.True(value.Length >= 43, value));
        Assert.Equal(values.Count, values.Distinct().Count());
        Assert.All(values, value => Assert.DoesNotContain('=', value));
    }

    [Fact]
    public void Settings_are_only_usable_when_they_are_complete_and_switched_on()
    {
        var complete = new EntraSignInSettings
        {
            TenantId = "contoso.onmicrosoft.com",
            ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d",
            ClientSecret = "secret",
            ClientSecretExpiresAt = DateTime.UtcNow.AddDays(30)
        };

        Assert.True(complete.IsComplete);
        Assert.False(complete.IsUsable);
        Assert.True((complete with { Enabled = true }).IsUsable);
        Assert.False((complete with { Enabled = true, ClientSecret = null }).IsUsable);
        Assert.False((complete with { Enabled = true, TenantId = "not a tenant" }).IsUsable);
        Assert.False((complete with { Enabled = true, ClientId = "abc" }).IsUsable);
    }

    [Fact]
    public void The_redirect_uri_is_the_callback_of_the_instance()
    {
        Assert.Equal("https://rmm.contoso.example/api/account/entra/callback", EntraSignIn.RedirectUri("https://rmm.contoso.example"));
        Assert.Equal("https://rmm.contoso.example/api/account/entra/callback", EntraSignIn.RedirectUri("https://rmm.contoso.example/"));
    }
}
