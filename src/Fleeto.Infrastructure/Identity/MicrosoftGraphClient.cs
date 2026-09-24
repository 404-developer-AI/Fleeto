using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Infrastructure.Identity;

/// <summary>How one line of a test of an app registration turned out (0.5.0).</summary>
public enum MicrosoftCheckStatus
{
    Ok,

    /// <summary>Works, but something Fleeto offers is not available, or the registration can do more than Fleeto needs.</summary>
    Warning,

    Failed,

    /// <summary>Something Fleeto cannot check from here, and how to check it.</summary>
    Info
}

/// <summary>One line of a test of an app registration: what was checked and how it turned out, cause and next step included.</summary>
public sealed record MicrosoftCheck(MicrosoftCheckStatus Status, string Text);

/// <summary>A user of the instance's own tenant as a search finds it: a member with an enabled account, never a guest.</summary>
public sealed record DirectoryUser(Guid ObjectId, string DisplayName, string? UserPrincipalName, string? Mail);

/// <summary>The credential of an app registration: a client secret or the certificate Fleeto created for it.</summary>
public sealed record AppCredential(string TenantId, string ClientId, string? ClientSecret, string? CertificatePfx);

/// <summary>
/// Whether the tenant asks for a second factor at all, as far as the app registration may read it: Security Defaults on,
/// or how many Conditional Access policies are on (null when Fleeto could not read them).
/// </summary>
public sealed record TenantMfaState(bool SecurityDefaults, int? EnabledConditionalAccessPolicies);

/// <summary>An app-only access token for Microsoft Graph and the application permissions it carries.</summary>
public sealed record GraphToken(string AccessToken, DateTimeOffset ExpiresAt, IReadOnlyList<string> Roles);

/// <summary>
/// The answers of a <c>MicrosoftRequest</c> as the workers write them and web reads them (0.5.0), so neither side can drift.
/// </summary>
public static class MicrosoftAnswers
{
    /// <summary>How long web waits for an answer; Settings shows a clear message after it.</summary>
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(25);

    /// <summary>A row older than this is deleted by the workers, answered or not.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
    };

    public static string Serialize(IReadOnlyList<MicrosoftCheck> checks) => JsonSerializer.Serialize(checks, Json);

    public static string Serialize(IReadOnlyList<DirectoryUser> users) => JsonSerializer.Serialize(users, Json);

    public static IReadOnlyList<MicrosoftCheck> Checks(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<MicrosoftCheck>>(json, Json) ?? [];

    public static IReadOnlyList<DirectoryUser> Users(string? json) =>
        string.IsNullOrEmpty(json) ? [] : JsonSerializer.Deserialize<List<DirectoryUser>>(json, Json) ?? [];
}

/// <summary>A call to Microsoft that did not work. The message states cause and next step and never holds a token or a secret.</summary>
public sealed class MicrosoftGraphException : Exception
{
    public MicrosoftGraphException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// The app-only calls Fleeto makes to Microsoft Graph from Settings (0.5.0): a token with the client credentials flow, whether
/// the registration may read users, and a search of the members of the tenant. Lives in the workers, the only containers that
/// can reach Microsoft (ARCHITECTURE §1).
/// </summary>
public sealed class MicrosoftGraphClient : IDisposable
{
    public const string Scope = "https://graph.microsoft.com/.default";

    /// <summary>The application permission that lets Settings list the users of the tenant.</summary>
    public const string UserReadAll = "User.Read.All";

    /// <summary>The optional application permission that lets the test of the sign-in see how the tenant asks for MFA.</summary>
    public const string PolicyReadAll = "Policy.Read.All";

    /// <summary>The application permission Microsoft Graph email needs.</summary>
    public const string MailSend = "Mail.Send";

    /// <summary>At most this many users per search: enough to pick from while typing.</summary>
    public const int SearchLimit = 25;

    private const string GraphBase = "https://graph.microsoft.com/v1.0";

    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;

    /// <param name="handler">Only for tests: the HTTP handler every call uses.</param>
    public MicrosoftGraphClient(TimeProvider time, ILogger<MicrosoftGraphClient>? logger = null, HttpMessageHandler? handler = null)
    {
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _http = new HttpClient(handler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        }, disposeHandler: handler is null)
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    /// <summary>
    /// An app-only token for Microsoft Graph. <paramref name="settingsPage"/> names where the admin corrects the credential
    /// (for example "Settings, Sign-in"), so the message of a refusal points at the right page.
    /// </summary>
    public async Task<GraphToken> GetTokenAsync(AppCredential credential, string settingsPage, CancellationToken cancellationToken = default)
    {
        var now = _time.GetUtcNow();
        var form = new List<KeyValuePair<string, string>>
        {
            new("client_id", credential.ClientId),
            new("scope", Scope),
            new("grant_type", "client_credentials")
        };
        if (credential.CertificatePfx is { Length: > 0 } pfx)
        {
            form.Add(new("client_assertion_type", "urn:ietf:params:oauth:client-assertion-type:jwt-bearer"));
            form.Add(new("client_assertion", MicrosoftIdentity.ClientAssertion(pfx, credential.TenantId, credential.ClientId, now)));
        }
        else
        {
            form.Add(new("client_secret", credential.ClientSecret ?? string.Empty));
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, MicrosoftIdentity.TokenEndpoint(credential.TenantId))
        {
            Content = new FormUrlEncodedContent(form)
        };
        using var response = await SendAsync(request, "Microsoft Entra ID", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new MicrosoftGraphException(TokenRefusal(EntraError(body), (int)response.StatusCode, settingsPage));
        }

        string? accessToken;
        int lifetime;
        try
        {
            using var json = JsonDocument.Parse(body);
            accessToken = json.RootElement.TryGetProperty("access_token", out var value) ? value.GetString() : null;
            lifetime = json.RootElement.TryGetProperty("expires_in", out var expiresIn) && expiresIn.TryGetInt32(out var seconds) ? seconds : 3599;
        }
        catch (JsonException ex)
        {
            throw new MicrosoftGraphException("Microsoft Entra ID answered with something Fleeto could not read. Try again in a minute.", ex);
        }

        if (string.IsNullOrEmpty(accessToken))
        {
            throw new MicrosoftGraphException("Microsoft Entra ID answered without an access token. Try again in a minute.");
        }

        return new GraphToken(accessToken, now.AddSeconds(lifetime), RolesOf(accessToken));
    }

    /// <summary>
    /// Whether the token may list the users of the tenant, by asking for one: the roles in a token say what was granted,
    /// this says what Microsoft actually allows.
    /// </summary>
    public async Task<bool> CanReadUsersAsync(GraphToken token, CancellationToken cancellationToken = default)
    {
        using var request = GraphRequest(token, $"{GraphBase}/users?$top=1&$select=id");
        using var response = await SendAsync(request, "Microsoft Graph", cancellationToken);
        if (response.IsSuccessStatusCode)
        {
            return true;
        }

        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return false;
        }

        throw new MicrosoftGraphException($"Microsoft Graph answered {(int)response.StatusCode}. Try again in a minute.");
    }

    /// <summary>
    /// Whether the tenant asks for a second factor: Security Defaults, else the Conditional Access policies that are on.
    /// Null when the registration may not read the policies (no <see cref="PolicyReadAll"/>). Fleeto only reports this to
    /// the admin; it never decides a sign-in on it.
    /// </summary>
    public async Task<TenantMfaState?> ReadTenantMfaAsync(GraphToken token, CancellationToken cancellationToken = default)
    {
        using var defaultsRequest = GraphRequest(token, $"{GraphBase}/policies/identitySecurityDefaultsEnforcementPolicy");
        using var defaults = await SendAsync(defaultsRequest, "Microsoft Graph", cancellationToken);
        if (defaults.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return null;
        }

        if (!defaults.IsSuccessStatusCode)
        {
            throw new MicrosoftGraphException($"Microsoft Graph answered {(int)defaults.StatusCode}. Try again in a minute.");
        }

        if (ReadBool(await defaults.Content.ReadAsStringAsync(cancellationToken), "isEnabled"))
        {
            return new TenantMfaState(true, null);
        }

        // Conditional Access needs Entra ID P1; without it, or without the permission, Graph refuses the list.
        using var policiesRequest = GraphRequest(token, $"{GraphBase}/identity/conditionalAccess/policies?$select=id,state");
        using var policies = await SendAsync(policiesRequest, "Microsoft Graph", cancellationToken);
        if (!policies.IsSuccessStatusCode)
        {
            return new TenantMfaState(false, null);
        }

        try
        {
            using var json = JsonDocument.Parse(await policies.Content.ReadAsStringAsync(cancellationToken));
            var enabled = json.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray().Count(v => string.Equals(Text(v, "state"), "enabled", StringComparison.OrdinalIgnoreCase))
                : 0;
            return new TenantMfaState(false, enabled);
        }
        catch (JsonException)
        {
            return new TenantMfaState(false, null);
        }
    }

    private static bool ReadBool(string body, string name)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// Members of the tenant with an enabled account whose name, account or address starts with <paramref name="query"/>;
    /// blank lists the first ones by name. Guests are never returned: they sign in with a local account.
    /// </summary>
    public async Task<IReadOnlyList<DirectoryUser>> SearchUsersAsync(GraphToken token, string? query, CancellationToken cancellationToken = default)
    {
        var search = CleanQuery(query);
        var url = new StringBuilder($"{GraphBase}/users?$select=id,displayName,userPrincipalName,mail&$count=true")
            .Append("&$top=").Append(SearchLimit.ToString(CultureInfo.InvariantCulture))
            .Append("&$filter=").Append(Uri.EscapeDataString("userType eq 'Member' and accountEnabled eq true"));
        if (search is null)
        {
            url.Append("&$orderby=displayName");
        }
        else
        {
            url.Append("&$search=").Append(Uri.EscapeDataString($"\"displayName:{search}\" OR \"userPrincipalName:{search}\" OR \"mail:{search}\""));
        }

        using var request = GraphRequest(token, url.ToString());
        // Filtering on userType and accountEnabled, $search and $count are advanced queries in Microsoft Graph.
        request.Headers.Add("ConsistencyLevel", "eventual");
        using var response = await SendAsync(request, "Microsoft Graph", cancellationToken);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            throw new MicrosoftGraphException(
                $"Microsoft refused to list the users of the tenant. Give the app registration of the sign-in the {UserReadAll} application permission with admin consent, or enter the object ID by hand.");
        }

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("Microsoft Graph answered {Status} to a search of users", (int)response.StatusCode);
            throw new MicrosoftGraphException($"Microsoft Graph answered {(int)response.StatusCode}. Try again in a minute.");
        }

        var users = new List<DirectoryUser>();
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (json.RootElement.TryGetProperty("value", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var value in values.EnumerateArray())
                {
                    if (!Guid.TryParse(Text(value, "id"), out var objectId))
                    {
                        continue;
                    }

                    var principal = Text(value, "userPrincipalName");
                    // A guest has #EXT# in its account name; the filter already leaves guests out, this makes sure.
                    if (principal?.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        continue;
                    }

                    users.Add(new DirectoryUser(objectId, Text(value, "displayName") ?? principal ?? objectId.ToString("D"), principal, Text(value, "mail")));
                }
            }
        }
        catch (JsonException ex)
        {
            throw new MicrosoftGraphException("Microsoft Graph answered with something Fleeto could not read. Try again in a minute.", ex);
        }

        return users.OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase).Take(SearchLimit).ToList();
    }

    /// <summary>
    /// The search text as it may go into a <c>$search</c> clause: letters, digits and the characters of an account name, at
    /// most 64 of them; null when nothing is left. Quotes and colons would change the clause, so they never pass.
    /// </summary>
    public static string? CleanQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        var kept = new string(query.Trim().Where(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '_' or '-' or '@').Take(64).ToArray()).Trim();
        return kept.Length == 0 ? null : kept;
    }

    /// <summary>
    /// The application permissions in an access token (its <c>roles</c> claim). Read without validating the token: it came
    /// straight from the token endpoint over TLS, and it is only used to tell the admin what was granted.
    /// </summary>
    public static IReadOnlyList<string> RolesOf(string accessToken)
    {
        var parts = accessToken.Split('.');
        if (parts.Length != 3)
        {
            return [];
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var json = JsonDocument.Parse(Convert.FromBase64String(payload));
            return json.RootElement.TryGetProperty("roles", out var roles) && roles.ValueKind == JsonValueKind.Array
                ? roles.EnumerateArray().Select(r => r.GetString()).OfType<string>().ToList()
                : [];
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            return [];
        }
    }

    private static HttpRequestMessage GraphRequest(GraphToken token, string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, string service, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new MicrosoftGraphException($"{service} did not answer in time. Try again in a minute.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new MicrosoftGraphException($"Fleeto could not reach {service} ({ex.HttpRequestError}). Try again in a minute.", ex);
        }
    }

    private static string TokenRefusal(string? aadsts, int status, string settingsPage) => aadsts switch
    {
        "AADSTS7000222" => $"The client secret has expired. Create a new secret in the app registration and save it in {settingsPage}.",
        "AADSTS7000215" => $"Microsoft Entra ID refused the client secret. Enter the secret value (not its ID) in {settingsPage}.",
        "AADSTS700027" or "AADSTS700024" => $"Microsoft Entra ID refused the certificate. Upload the certificate from {settingsPage} to the app registration.",
        "AADSTS700016" => $"Microsoft Entra ID does not know the application (client) ID in this tenant. Check it in {settingsPage}.",
        "AADSTS90002" or "AADSTS900023" => $"Microsoft Entra ID does not know the tenant. Check the directory (tenant) ID in {settingsPage}.",
        _ => $"Microsoft Entra ID refused the sign-in of the app registration ({aadsts ?? status.ToString(CultureInfo.InvariantCulture)}). Check the tenant ID, client ID and credential in {settingsPage}."
    };

    /// <summary>The AADSTS code of a token error; the description is not kept (it carries trace and correlation ids).</summary>
    private static string? EntraError(string body)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            return json.RootElement.TryGetProperty("error_codes", out var codes) && codes.ValueKind == JsonValueKind.Array &&
                   codes.GetArrayLength() > 0 && codes[0].TryGetInt32(out var first)
                ? "AADSTS" + first.ToString(CultureInfo.InvariantCulture)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
            ? text.Length > 320 ? text[..320] : text
            : null;

    public void Dispose() => _http.Dispose();
}
