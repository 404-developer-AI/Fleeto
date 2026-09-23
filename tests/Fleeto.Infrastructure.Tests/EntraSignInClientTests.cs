using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fleeto.Infrastructure.Identity;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Guarantees the exchange of an authorization code for the claims Fleeto keeps (0.5.0), which runs in the workers: the code,
/// the PKCE verifier and the client secret go to the token endpoint, the id_token that comes back must be signed by the keys
/// of the tenant and be meant for this app registration, and only what Fleeto needs comes out of it — never a token.
/// </summary>
public sealed class EntraSignInClientTests : IDisposable
{
    private const string Tenant = "8fa1c6b2-91d2-4b4e-9a0e-2f3c4d5e6a7b";
    private const string ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d";
    private const string RedirectUri = "https://rmm.contoso.example/api/account/entra/callback";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);

    private static readonly EntraSignInSettings Settings = new()
    {
        Enabled = true,
        TenantId = Tenant,
        ClientId = ClientId,
        ClientSecret = "super-secret-value",
        ClientSecretExpiresAt = DateTime.UtcNow.AddDays(90)
    };

    [Fact]
    public async Task The_code_is_exchanged_and_the_claims_of_the_token_come_back()
    {
        var handler = new TokenHandler(IdToken());
        using var client = new EntraSignInClient(Settings, Configuration(), handler: handler);

        var outcome = await client.ExchangeAsync("the-code", "the-verifier", RedirectUri);

        Assert.Null(outcome.Refusal);
        var claims = Assert.IsType<SignInClaims>(outcome.Claims);
        Assert.Equal("a1b2c3d4-e5f6-4789-9abc-def012345678", claims.ObjectId);
        Assert.Equal(Tenant, claims.TenantId);
        Assert.Equal("tech@contoso.com", claims.Account);
        Assert.Equal("A Technician", claims.DisplayName);
        Assert.True(claims.MfaProven);
        Assert.Equal("nonce-value", claims.Nonce);

        // What the token endpoint was asked: the code, the verifier and the secret, and the same redirect URI as the code
        // was issued for.
        Assert.Contains("grant_type=authorization_code", handler.LastBody);
        Assert.Contains("code=the-code", handler.LastBody);
        Assert.Contains("code_verifier=the-verifier", handler.LastBody);
        Assert.Contains("client_secret=super-secret-value", handler.LastBody);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(RedirectUri), handler.LastBody);
    }

    [Fact]
    public async Task A_token_signed_with_another_key_is_refused()
    {
        using var client = new EntraSignInClient(Settings, Configuration(), handler: new TokenHandler(IdToken(signWithOtherKey: true)));

        var outcome = await client.ExchangeAsync("the-code", "the-verifier", RedirectUri);

        Assert.Null(outcome.Claims);
        Assert.NotNull(outcome.Refusal);
    }

    [Fact]
    public async Task A_token_for_another_app_registration_is_refused()
    {
        using var client = new EntraSignInClient(Settings, Configuration(),
            handler: new TokenHandler(IdToken(audience: "11111111-2222-3333-4444-555555555555")));

        Assert.Null((await client.ExchangeAsync("the-code", "the-verifier", RedirectUri)).Claims);
    }

    [Fact]
    public async Task An_expired_token_is_refused()
    {
        using var client = new EntraSignInClient(Settings, Configuration(), handler: new TokenHandler(IdToken(expired: true)));

        Assert.Null((await client.ExchangeAsync("the-code", "the-verifier", RedirectUri)).Claims);
    }

    [Fact]
    public async Task A_refused_token_request_says_what_to_do_and_names_nothing_from_microsoft()
    {
        var handler = new TokenHandler(idToken: null, status: HttpStatusCode.BadRequest,
            body: """{"error":"invalid_client","error_description":"AADSTS7000215: Invalid client secret provided for app 0b6f1c9e."}""");
        using var client = new EntraSignInClient(Settings, Configuration(), handler: handler);

        var outcome = await client.ExchangeAsync("the-code", "the-verifier", RedirectUri);

        Assert.Null(outcome.Claims);
        Assert.NotNull(outcome.Refusal);
        Assert.DoesNotContain("AADSTS", outcome.Refusal);
        Assert.Contains("Settings, Sign-in", outcome.Refusal);
    }

    [Fact]
    public async Task Metadata_that_cannot_be_read_ends_the_sign_in_without_a_call()
    {
        var handler = new TokenHandler(IdToken());
        using var client = new EntraSignInClient(Settings, new FailingConfiguration(), handler: handler);

        var outcome = await client.ExchangeAsync("the-code", "the-verifier", RedirectUri);

        Assert.Null(outcome.Claims);
        Assert.NotNull(outcome.Refusal);
        Assert.Equal(0, handler.Requests);
    }

    private IConfigurationManager<OpenIdConnectConfiguration> Configuration()
    {
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token"
        };
        configuration.SigningKeys.Add(new RsaSecurityKey(_key) { KeyId = "test-key" });
        return new StaticConfiguration(configuration);
    }

    private string IdToken(string? audience = null, bool expired = false, bool signWithOtherKey = false)
    {
        var now = DateTime.UtcNow;
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience ?? ClientId,
            NotBefore = expired ? now.AddHours(-2) : now.AddMinutes(-1),
            Expires = expired ? now.AddHours(-1) : now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(signWithOtherKey ? _otherKey : _key) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["tid"] = Tenant,
                ["oid"] = "a1b2c3d4-e5f6-4789-9abc-def012345678",
                ["amr"] = new[] { "pwd", "mfa" },
                ["acct"] = 0,
                ["nonce"] = "nonce-value",
                ["preferred_username"] = "tech@contoso.com",
                ["name"] = "A Technician"
            }
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public void Dispose()
    {
        _key.Dispose();
        _otherKey.Dispose();
    }

    /// <summary>The metadata of a tenant, as the workers cache it.</summary>
    private sealed class StaticConfiguration : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration _configuration;

        public StaticConfiguration(OpenIdConnectConfiguration configuration) => _configuration = configuration;

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(_configuration);

        public void RequestRefresh()
        {
        }
    }

    /// <summary>Microsoft unreachable: the metadata cannot be read.</summary>
    private sealed class FailingConfiguration : IConfigurationManager<OpenIdConnectConfiguration>
    {
        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) =>
            Task.FromException<OpenIdConnectConfiguration>(new HttpRequestException("no route to host"));

        public void RequestRefresh()
        {
        }
    }

    /// <summary>Answers the token endpoint like Entra ID does, and records what was posted.</summary>
    private sealed class TokenHandler : HttpMessageHandler
    {
        private readonly string? _idToken;
        private readonly HttpStatusCode _status;
        private readonly string? _body;

        public TokenHandler(string? idToken, HttpStatusCode status = HttpStatusCode.OK, string? body = null)
        {
            _idToken = idToken;
            _status = status;
            _body = body;
        }

        public int Requests { get; private set; }
        public string LastBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            LastBody = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            var body = _body ?? $$"""{"token_type":"Bearer","expires_in":3600,"id_token":"{{_idToken}}"}""";
            return new HttpResponseMessage(_status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }
}
