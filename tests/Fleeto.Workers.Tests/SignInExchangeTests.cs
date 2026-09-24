using System.Net;
using System.Security.Cryptography;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees of the sign-in exchange (0.5.0): the workers are the only containers that can reach Microsoft, so the
/// authorization code web wrote is traded here and only the claims Fleeto needs are written back. A sign-in that is switched
/// off, or a token that does not belong to this tenant, leaves a reason the person can act on, and a code never lingers in the
/// database.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class SignInExchangeTests : IDisposable
{
    private const string Tenant = "8fa1c6b2-91d2-4b4e-9a0e-2f3c4d5e6a7b";
    private const string ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d";
    private const string ObjectId = "a1b2c3d4-e5f6-4789-9abc-def012345678";
    private static readonly string Issuer = $"https://login.microsoftonline.com/{Tenant}/v2.0";

    private readonly WorkersFixture _fixture;
    private readonly RSA _key = RSA.Create(2048);

    public SignInExchangeTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task The_code_is_exchanged_and_only_the_claims_are_written_back()
    {
        await ConfigureAsync(enabled: true);
        var exchangeId = await WriteRequestAsync("the-code", "the-verifier");

        await Service(new TokenHandler(IdToken())).ExchangeWaitingAsync(CancellationToken.None);

        var exchange = await ReadAsync(exchangeId);
        Assert.NotNull(exchange);
        Assert.Equal(SignInExchangeState.Completed, exchange!.State);
        Assert.NotNull(exchange.CompletedAt);
        Assert.Null(exchange.FailureReason);

        var claims = SignInExchangeContents.UnprotectClaims(_fixture.Db.SecretProtector, exchangeId, exchange.EncryptedClaims!);
        Assert.Equal(ObjectId, claims.ObjectId);
        Assert.Equal(Tenant, claims.TenantId);
        Assert.Equal("tech@contoso.com", claims.Account);
        Assert.True(claims.MfaProven);

        // No token, code or secret is kept: the row holds the request and the claims, both encrypted and bound to the row.
        Assert.DoesNotContain("the-code", exchange.EncryptedClaims);
        Assert.DoesNotContain("eyJ", exchange.EncryptedClaims);
        Assert.ThrowsAny<CryptographicException>(() =>
            SignInExchangeContents.UnprotectClaims(_fixture.Db.SecretProtector, Guid.NewGuid(), exchange.EncryptedClaims!));
    }

    [Fact]
    public async Task A_sign_in_that_is_switched_off_is_refused_with_a_reason()
    {
        await ConfigureAsync(enabled: false);
        var exchangeId = await WriteRequestAsync("the-code", "the-verifier");
        var handler = new TokenHandler(IdToken());

        await Service(handler).ExchangeWaitingAsync(CancellationToken.None);

        var exchange = await ReadAsync(exchangeId);
        Assert.Equal(SignInExchangeState.Failed, exchange!.State);
        Assert.Contains("local account", exchange.FailureReason);
        Assert.Null(exchange.EncryptedClaims);
        // Nothing was asked of Microsoft.
        Assert.Equal(0, handler.Requests);
    }

    [Fact]
    public async Task A_token_of_another_tenant_leaves_no_claims()
    {
        await ConfigureAsync(enabled: true);
        var exchangeId = await WriteRequestAsync("the-code", "the-verifier");

        await Service(new TokenHandler(IdToken(tenantId: "11111111-2222-3333-4444-555555555555"))).ExchangeWaitingAsync(CancellationToken.None);

        var exchange = await ReadAsync(exchangeId);
        Assert.Equal(SignInExchangeState.Failed, exchange!.State);
        Assert.Null(exchange.EncryptedClaims);
        Assert.Contains("tenant", exchange.FailureReason);
    }

    [Fact]
    public async Task A_code_nobody_finished_is_deleted()
    {
        await ConfigureAsync(enabled: true);
        var abandoned = await WriteRequestAsync("old-code", "old-verifier",
            createdAt: _fixture.Now - EntraSignIn.ExchangeLifetime - TimeSpan.FromMinutes(1));
        var fresh = await WriteRequestAsync("the-code", "the-verifier");

        await Service(new TokenHandler(IdToken())).ExchangeWaitingAsync(CancellationToken.None);

        Assert.Null(await ReadAsync(abandoned));
        Assert.Equal(SignInExchangeState.Completed, (await ReadAsync(fresh))!.State);
    }

    private SignInExchangeService Service(TokenHandler handler) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus, _fixture.Settings(), _fixture.Db.SecretProtector,
            new EntraSignInClientFactory(NullLoggerFactory.Instance, () => handler, _ => new StaticConfiguration(Configuration())),
            _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<SignInExchangeService>.Instance);

    private async Task ConfigureAsync(bool enabled)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.SignInExchanges.RemoveRange(await db.SignInExchanges.ToListAsync());
        await db.SaveChangesAsync();

        await _fixture.Settings().SetAsync(SettingKeys.EntraSignIn, new EntraSignInSettings
        {
            Enabled = enabled,
            TenantId = Tenant,
            ClientId = ClientId,
            ClientSecret = "super-secret-value",
            ClientSecretExpiresAt = _fixture.Now.AddDays(90)
        }, encrypted: true, userId: null);
    }

    private async Task<Guid> WriteRequestAsync(string code, string verifier, DateTime? createdAt = null)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var exchange = new SignInExchange
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = createdAt ?? _fixture.Now,
            RedirectUri = "https://rmm.contoso.example/api/account/entra/callback",
            State = SignInExchangeState.Requested
        };
        exchange.EncryptedRequest = SignInExchangeContents.ProtectRequest(_fixture.Db.SecretProtector, exchange.Id,
            new SignInExchangeRequest(code, verifier));
        db.SignInExchanges.Add(exchange);
        await db.SaveChangesAsync();
        return exchange.Id;
    }

    private async Task<SignInExchange?> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.SignInExchanges.AsNoTracking().SingleOrDefaultAsync(e => e.Id == id);
    }

    private OpenIdConnectConfiguration Configuration()
    {
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = Issuer,
            TokenEndpoint = $"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/token"
        };
        configuration.SigningKeys.Add(new RsaSecurityKey(_key) { KeyId = "test-key" });
        return configuration;
    }

    private string IdToken(string tenantId = Tenant)
    {
        var now = DateTime.UtcNow;
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = ClientId,
            NotBefore = now.AddMinutes(-1),
            Expires = now.AddMinutes(10),
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_key) { KeyId = "test-key" }, SecurityAlgorithms.RsaSha256),
            Claims = new Dictionary<string, object>
            {
                ["tid"] = tenantId,
                ["oid"] = ObjectId,
                ["amr"] = new[] { "pwd", "mfa" },
                ["acct"] = 0,
                ["nonce"] = "nonce-value",
                ["preferred_username"] = "tech@contoso.com",
                ["name"] = "A Technician"
            }
        });
    }

    public void Dispose()
    {
        _key.Dispose();
        // The client secret of these tests would otherwise raise expiry warnings in tests that move the clock by months.
        using var db = _fixture.Db.DbFactory.CreateSystem();
        db.Settings.Where(s => s.Key == SettingKeys.EntraSignIn).ExecuteDelete();
    }

    /// <summary>The metadata of the tenant as the workers cache it.</summary>
    private sealed class StaticConfiguration : IConfigurationManager<OpenIdConnectConfiguration>
    {
        private readonly OpenIdConnectConfiguration _configuration;

        public StaticConfiguration(OpenIdConnectConfiguration configuration) => _configuration = configuration;

        public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(_configuration);

        public void RequestRefresh()
        {
        }
    }

    /// <summary>Answers the token endpoint like Entra ID does.</summary>
    private sealed class TokenHandler : HttpMessageHandler
    {
        private readonly string _idToken;

        public TokenHandler(string idToken) => _idToken = idToken;

        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var body = $$"""{"token_type":"Bearer","expires_in":3600,"id_token":"{{_idToken}}"}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
