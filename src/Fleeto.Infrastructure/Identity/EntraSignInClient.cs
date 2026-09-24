using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Fleeto.Infrastructure.Identity;

/// <summary>
/// Exchanges an authorization code of a sign-in with Microsoft Entra ID for the claims Fleeto keeps (0.5.0). Lives in the
/// workers: web has no outbound access, so it never reaches Microsoft itself (ARCHITECTURE §1). The client secret stays
/// here too — web writes only the code and the PKCE verifier.
/// </summary>
public sealed class EntraSignInClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly EntraSignInSettings _settings;
    private readonly IConfigurationManager<OpenIdConnectConfiguration> _configuration;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ILogger _logger;

    public EntraSignInClient(EntraSignInSettings settings, IConfigurationManager<OpenIdConnectConfiguration> configuration,
        ILogger<EntraSignInClient>? logger = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _settings = settings;
        _configuration = configuration;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _ownsHttp = handler is null;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: _ownsHttp)
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// Trades the code for an id_token, validates it and returns what Fleeto keeps. A refusal states cause and next step and
    /// holds no token, code or secret, so it is safe to show to the person signing in.
    /// </summary>
    public async Task<SignInTokenOutcome> ExchangeAsync(string code, string codeVerifier, string redirectUri,
        CancellationToken cancellationToken = default)
    {
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _configuration.GetConfigurationAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The Microsoft Entra ID metadata of tenant {Tenant} could not be read", _settings.TenantId);
            return SignInTokenOutcome.Refuse("Microsoft could not be reached to finish the sign-in. Try again; a local account still works.");
        }

        string? idToken;
        try
        {
            idToken = await RequestIdTokenAsync(code, codeVerifier, redirectUri, configuration, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "The token request of a sign-in with Microsoft Entra ID failed");
            return SignInTokenOutcome.Refuse("Microsoft could not be reached to finish the sign-in. Try again; a local account still works.");
        }

        if (idToken is null)
        {
            return SignInTokenOutcome.Refuse("Microsoft refused the sign-in. Check the client secret and the redirect URI in Settings, Sign-in.");
        }

        var parameters = new TokenValidationParameters
        {
            ValidIssuer = configuration.Issuer,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidAudience = _settings.ClientId,
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(5)
        };

        var handler = new JsonWebTokenHandler();
        var result = await handler.ValidateTokenAsync(idToken, parameters);
        if (!result.IsValid)
        {
            _logger.LogWarning(result.Exception, "The id_token of a sign-in with Microsoft Entra ID did not validate");
            return SignInTokenOutcome.Refuse("The sign-in could not be verified. Try again; a local account still works.");
        }

        return SignInTokenRules.Check(FactsOf((JsonWebToken)result.SecurityToken), configuration.Issuer);
    }

    /// <summary>The claims Fleeto looks at, read from a validated token.</summary>
    internal static SignInTokenFacts FactsOf(JsonWebToken token)
    {
        var methods = token.TryGetClaim("amr", out _) ? token.GetPayloadValue<string[]>("amr") ?? [] : [];
        var accountType = token.TryGetClaim("acct", out var acct) && int.TryParse(acct.Value, out var parsed) ? parsed : (int?)null;
        return new SignInTokenFacts(
            token.Issuer,
            Claim(token, "tid"),
            Claim(token, "oid"),
            methods,
            Claim(token, "idp"),
            accountType,
            Claim(token, "nonce"),
            Claim(token, "preferred_username") ?? Claim(token, "upn") ?? Claim(token, "email"),
            Claim(token, "name"),
            Claim(token, "acr"),
            Strings(token, "acrs"));
    }

    /// <summary>A claim that is an array of strings, or a single string, as a list; empty when the token does not have it.</summary>
    private static IReadOnlyList<string> Strings(JsonWebToken token, string type)
    {
        if (!token.TryGetClaim(type, out _))
        {
            return [];
        }

        return token.TryGetPayloadValue<string[]>(type, out var values) && values is not null
            ? values
            : token.TryGetPayloadValue<string>(type, out var value) && value is not null ? [value] : [];
    }

    private static string? Claim(JsonWebToken token, string type) => token.TryGetClaim(type, out var claim) ? claim.Value : null;

    private async Task<string?> RequestIdTokenAsync(string code, string codeVerifier, string redirectUri,
        OpenIdConnectConfiguration configuration, CancellationToken cancellationToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "authorization_code"),
            new("client_id", _settings.ClientId),
            new("code", code),
            new("redirect_uri", redirectUri),
            new("code_verifier", codeVerifier),
            new("scope", EntraSignIn.Scope),
            new("client_secret", _settings.ClientSecret ?? string.Empty)
        };

        var endpoint = string.IsNullOrWhiteSpace(configuration.TokenEndpoint)
            ? MicrosoftIdentity.TokenEndpoint(_settings.TenantId).AbsoluteUri
            : configuration.TokenEndpoint;
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint) { Content = new FormUrlEncodedContent(form) };
        using var response = await _http.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Microsoft's own message can name the app registration and the tenant, so it goes to the log, never to the
            // browser: the person signing in gets the message of the caller instead.
            _logger.LogWarning("Microsoft refused the token request with {Status}: {Body}", (int)response.StatusCode,
                body.Length > 500 ? body[..500] : body);
            return null;
        }

        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("id_token", out var idToken) ? idToken.GetString() : null;
        }
        catch (JsonException)
        {
            _logger.LogWarning("The token endpoint answered with something that is not JSON");
            return null;
        }
    }

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Creates <see cref="EntraSignInClient"/> instances and keeps the OpenID Connect metadata of a tenant cached, so the
/// signing keys are read once rather than per sign-in. One instance per process.
/// </summary>
public sealed class EntraSignInClientFactory : IDisposable
{
    private readonly ConcurrentDictionary<string, IConfigurationManager<OpenIdConnectConfiguration>> _configurations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ILoggerFactory _loggers;
    private readonly Func<HttpMessageHandler>? _handlerFactory;
    private readonly Func<string, IConfigurationManager<OpenIdConnectConfiguration>>? _configurationFactory;
    private readonly HttpClient _metadataHttp;

    /// <param name="handlerFactory">Only for tests: the HTTP handler the token request uses.</param>
    /// <param name="configurationFactory">Only for tests: the metadata of a tenant, instead of reading it from Microsoft.</param>
    public EntraSignInClientFactory(ILoggerFactory loggers, Func<HttpMessageHandler>? handlerFactory = null,
        Func<string, IConfigurationManager<OpenIdConnectConfiguration>>? configurationFactory = null)
    {
        _loggers = loggers;
        _handlerFactory = handlerFactory;
        _configurationFactory = configurationFactory;
        _metadataHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
    }

    public EntraSignInClient Create(EntraSignInSettings settings)
    {
        var configuration = _configurations.GetOrAdd(settings.TenantId, tenant => _configurationFactory is not null
            ? _configurationFactory(tenant)
            : new ConfigurationManager<OpenIdConnectConfiguration>(
                MicrosoftIdentity.MetadataAddress(tenant).AbsoluteUri,
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(_metadataHttp) { RequireHttps = true }));
        return new EntraSignInClient(settings, configuration, _loggers.CreateLogger<EntraSignInClient>(), _handlerFactory?.Invoke());
    }

    public void Dispose() => _metadataHttp.Dispose();
}
