using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Infrastructure.Integrations.Action1;

/// <summary>A call to Action1 that did not succeed. Permanent failures are not worth retrying without a change by an admin.</summary>
public sealed class Action1Exception : Exception
{
    public Action1Exception(string message, bool permanent, Exception? inner = null, HttpStatusCode? status = null, string? detail = null)
        : base(message, inner)
    {
        IsPermanent = permanent;
        Status = status;
        Detail = detail;
    }

    public bool IsPermanent { get; }

    /// <summary>The HTTP status Action1 answered, when it answered at all.</summary>
    public HttpStatusCode? Status { get; }

    /// <summary>What Action1 said about the refusal (its <c>user_message</c>), shortened; null when it said nothing.</summary>
    public string? Detail { get; }
}

/// <summary>
/// Talks to one Action1 enterprise over its REST API (0.4.0).
///
/// It holds the bearer token of the OAuth2 client credentials (one hour, renewed with a margin and once more when a call
/// is refused), paces every call through a <see cref="RequestBudget"/> because Action1 counts its whole API against one
/// budget per enterprise, and honours the <c>retry_after</c> of a 429 over its own bookkeeping. Failures carry cause and
/// next step for the admin and never hold a token, a secret or the client id.
/// </summary>
public sealed class Action1Client : IIntegration, IDisposable
{
    /// <summary>A token is renewed this long before it expires, so a call never travels with one that dies on the way.</summary>
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(5);

    /// <summary>Attempts of one call when Action1 answers "too many requests"; its own guidance is to stop after three.</summary>
    private const int RateLimitAttempts = 3;

    /// <summary>A pause Fleeto accepts from Action1; anything longer is treated as "try again in the next round".</summary>
    private static readonly TimeSpan MaxRetryAfter = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly Action1Credentials _credentials;
    private readonly RequestBudget _budget;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _token;
    private DateTimeOffset _tokenExpiresAt;

    public Action1Client(Action1Credentials credentials, Action1Region region, RequestBudget budget, TimeProvider time,
        ILogger<Action1Client>? logger = null, HttpMessageHandler? handler = null, TimeSpan? timeout = null)
    {
        _credentials = credentials;
        _budget = budget;
        _time = time;
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _ownsHttp = handler is null;
        _http = new HttpClient(handler ?? CreateHandler(), disposeHandler: _ownsHttp)
        {
            BaseAddress = Action1Api.BaseAddress(region),
            Timeout = timeout ?? TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public IntegrationType Type => IntegrationType.Action1;

    public async Task<IntegrationResult> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // One page of one record: it proves the credentials, the region and the role all work, for one request.
            using var page = await GetAsync("organizations", "from=0&limit=1", cancellationToken);
            var total = TotalItems(page.RootElement);
            return IntegrationResult.Success(total == 1
                ? "Action1 answered. The credentials reach 1 organization."
                : $"Action1 answered. The credentials reach {total} organizations.");
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult.Fail(ex.Message, ex.IsPermanent);
        }
    }

    public async Task<IntegrationResult<IReadOnlyList<ExternalTenant>>> ListTenantsAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var tenants = new List<ExternalTenant>();
            for (var page = 0; page < Action1Api.MaxPages; page++)
            {
                using var document = await GetAsync("organizations",
                    $"from={page * Action1Api.PageSize}&limit={Action1Api.PageSize}", cancellationToken);
                var items = Items(document.RootElement);
                foreach (var item in items)
                {
                    var id = Text(item, "id");
                    if (!string.IsNullOrEmpty(id))
                    {
                        tenants.Add(new ExternalTenant(id, Text(item, "name") is { Length: > 0 } name ? name : id));
                    }
                }

                if (items.Count < Action1Api.PageSize)
                {
                    break;
                }
            }

            return IntegrationResult<IReadOnlyList<ExternalTenant>>.Success(tenants);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<IReadOnlyList<ExternalTenant>>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// The endpoints of one organization with their patch counts (0.4.0 step 2). Action1 returns the counts only when they
    /// are asked for by name, which costs it more work, so nothing else asks for extended fields.
    /// </summary>
    public async Task<IntegrationResult<IReadOnlyList<Action1Endpoint>>> ListEndpointsAsync(string organizationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoints = new List<Action1Endpoint>();
            for (var page = 0; page < Action1Api.MaxPages; page++)
            {
                using var document = await GetAsync($"endpoints/managed/{Uri.EscapeDataString(organizationId)}",
                    $"from={page * Action1Api.PageSize}&limit={Action1Api.PageSize}&fields=missing_updates", cancellationToken);
                var items = Items(document.RootElement);
                foreach (var item in items)
                {
                    if (Action1Endpoint.From(item, organizationId) is { } endpoint)
                    {
                        endpoints.Add(endpoint);
                    }
                }

                if (items.Count < Action1Api.PageSize)
                {
                    break;
                }
            }

            return IntegrationResult<IReadOnlyList<Action1Endpoint>>.Success(endpoints);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<IReadOnlyList<Action1Endpoint>>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>The updates one endpoint is missing (0.4.0 step 2). Only asked for an endpoint that misses something.</summary>
    public async Task<IntegrationResult<IReadOnlyList<Action1MissingUpdate>>> ListMissingUpdatesAsync(string organizationId,
        string endpointId, CancellationToken cancellationToken = default)
    {
        try
        {
            var updates = new List<Action1MissingUpdate>();
            for (var page = 0; page < Action1Api.MaxPages; page++)
            {
                using var document = await GetAsync(
                    $"endpoints/managed/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(endpointId)}/missing-updates",
                    $"from={page * Action1Api.PageSize}&limit={Action1Api.PageSize}", cancellationToken);
                var items = Items(document.RootElement);
                foreach (var item in items)
                {
                    if (Action1MissingUpdate.From(item) is { } update)
                    {
                        updates.Add(update);
                    }
                }

                if (items.Count < Action1Api.PageSize)
                {
                    break;
                }
            }

            return IntegrationResult<IReadOnlyList<Action1MissingUpdate>>.Success(updates);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<IReadOnlyList<Action1MissingUpdate>>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// Where the Action1 agent installer of one organization is downloaded (0.4.0 step 3), or an empty string when
    /// Action1 does not hand it out over the API. Action1 documents no contract for this, so a refusal is not an error
    /// here: an admin can paste the link from the Action1 console instead, and Settings says so.
    /// </summary>
    public async Task<IntegrationResult<string>> GetAgentInstallerUrlAsync(string organizationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var document = await GetAsync($"endpoints/agent-installation/{Uri.EscapeDataString(organizationId)}", null, cancellationToken);
            return IntegrationResult<string>.Success(WindowsInstaller(document.RootElement) ?? string.Empty);
        }
        catch (Action1Exception ex) when (ex.IsPermanent)
        {
            // The route is not there, or these credentials may not read it: Fleeto asks the admin for the link instead.
            _logger.LogInformation("Action1 did not hand out an agent installer link for organization {Tenant}: {Message}",
                organizationId, ex.Message);
            return IntegrationResult<string>.Success(string.Empty);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<string>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// The Windows installer link in an answer whose shape Action1 does not document: a URL under a field that names the
    /// platform, in a list of items, or one URL field at the top. Only an https link to the Action1 account's own region
    /// is accepted, so a wrong answer can never turn into a download from somewhere else.
    /// </summary>
    private string? WindowsInstaller(JsonElement root)
    {
        foreach (var candidate in Urls(root))
        {
            if (Uri.TryCreate(candidate, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps &&
                url.Host.Equals(_http.BaseAddress!.Host, StringComparison.OrdinalIgnoreCase) &&
                url.AbsolutePath.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> Urls(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String when element.GetString() is { Length: > 0 } text:
                yield return text;
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var url in Urls(item))
                    {
                        yield return url;
                    }
                }

                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var url in Urls(property.Value))
                    {
                        yield return url;
                    }
                }

                break;
        }
    }

    /// <summary>
    /// Starts a deployment of updates now (0.4.0 step 3) and returns the id Action1 gave it, which is what the workers
    /// follow. Action1 runs a deployment inside one organization: <c>orgId=all</c> reads but never runs.
    /// </summary>
    public async Task<IntegrationResult<string>> StartDeploymentAsync(string organizationId, Action1Deployment deployment,
        CancellationToken cancellationToken = default)
    {
        if (deployment.EndpointIds.Count == 0)
        {
            return IntegrationResult<string>.Fail("A deployment needs at least one endpoint.", permanent: true);
        }

        try
        {
            using var document = await RequestAsync(HttpMethod.Post, $"policies/instances/{Uri.EscapeDataString(organizationId)}", null,
                deployment.ToJson(), cancellationToken);
            var id = Text(document.RootElement, "id");
            if (string.IsNullOrEmpty(id))
            {
                // Without an id Fleeto cannot follow the deployment, and claiming it runs would be a guess. Action1 may
                // well have accepted it, so the message says where to look.
                return IntegrationResult<string>.Fail(
                    "Action1 accepted the deployment but did not name it, so Fleeto cannot follow it. Check the automation in the Action1 console.",
                    permanent: true);
            }

            return IntegrationResult<string>.Success(id);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<string>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// What Action1 reports per endpoint for a running deployment (0.4.0 step 3). An empty list means Action1 knows the
    /// deployment but has nothing to say about its endpoints yet.
    /// </summary>
    public async Task<IntegrationResult<IReadOnlyList<Action1EndpointResult>>> ListDeploymentResultsAsync(string organizationId,
        string deploymentId, CancellationToken cancellationToken = default)
    {
        try
        {
            var results = new List<Action1EndpointResult>();
            for (var page = 0; page < Action1Api.MaxPages; page++)
            {
                using var document = await GetAsync(
                    $"policies/instances/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(deploymentId)}/endpoint_results",
                    $"from={page * Action1Api.PageSize}&limit={Action1Api.PageSize}", cancellationToken);
                var items = Items(document.RootElement);
                foreach (var item in items)
                {
                    if (Action1EndpointResult.From(item) is { } result)
                    {
                        results.Add(result);
                    }
                }

                if (items.Count < Action1Api.PageSize)
                {
                    break;
                }
            }

            return IntegrationResult<IReadOnlyList<Action1EndpointResult>>.Success(results);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<IReadOnlyList<Action1EndpointResult>>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// The history Action1 keeps for one endpoint of a deployment (0.6.0), in the order Action1 lists it. Read only when
    /// something changed for that endpoint, because every page counts against the budget of the whole instance.
    /// </summary>
    public async Task<IntegrationResult<IReadOnlyList<Action1DeploymentStep>>> ListDeploymentStepsAsync(string organizationId,
        string deploymentId, string endpointId, CancellationToken cancellationToken = default)
    {
        try
        {
            var steps = new List<Action1DeploymentStep>();
            for (var page = 0; page < Action1Api.MaxPages; page++)
            {
                using var document = await GetAsync(
                    $"automations/instances/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(deploymentId)}/endpoint-results/" +
                    $"{Uri.EscapeDataString(endpointId)}/details",
                    $"from={page * Action1Api.PageSize}&limit={Action1Api.PageSize}", cancellationToken);
                var items = Items(document.RootElement);
                steps.AddRange(items.Select(Action1DeploymentStep.From));
                if (items.Count < Action1Api.PageSize)
                {
                    break;
                }
            }

            return IntegrationResult<IReadOnlyList<Action1DeploymentStep>>.Success(steps);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<IReadOnlyList<Action1DeploymentStep>>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// The name of the organization of a client while Action1 follows the clients of Fleeto (0.6.0): the client code in
    /// brackets before the client name, <c>[ACME] Acme Corporation</c>. The code is unique and never changes, so two
    /// clients with the same name still get two recognisable organizations.
    /// </summary>
    public static string OrganizationName(string clientCode, string clientName)
    {
        var name = $"[{clientCode}] {clientName.Trim()}";
        return name.Length <= 200 ? name : name[..200];
    }

    /// <summary>
    /// Creates an organization in the enterprise (0.6.0) and returns its id. Needs the <c>manage_organizations</c>
    /// permission on the role of the API credentials; Action1 creates the default roles of the organization with it.
    /// </summary>
    public Task<IntegrationResult<string>> CreateOrganizationAsync(string name, string description,
        CancellationToken cancellationToken = default) =>
        CreateAsync("organizations", new JsonObject { ["name"] = name, ["description"] = description },
            "Action1 created the organization but did not name its id. Map it by hand in Settings, Integrations.", cancellationToken);

    /// <summary>Renames an organization (0.6.0).</summary>
    public Task<IntegrationResult> RenameOrganizationAsync(string organizationId, string name, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Patch, $"organizations/{Uri.EscapeDataString(organizationId)}", new JsonObject { ["name"] = name },
            cancellationToken);

    /// <summary>
    /// Removes an organization (0.6.0). Action1 refuses while the organization still holds endpoints, and for the last
    /// organization of the enterprise; one that no longer exists counts as removed.
    /// </summary>
    public Task<IntegrationResult> DeleteOrganizationAsync(string organizationId, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Delete, $"organizations/{Uri.EscapeDataString(organizationId)}", null, cancellationToken, goneIsDone: true);

    /// <summary>
    /// Moves an endpoint to another organization of the enterprise (0.6.0). Needs <c>manage_endpoints</c>. An endpoint the
    /// organization no longer holds counts as moved: it went elsewhere already.
    /// </summary>
    public Task<IntegrationResult> MoveEndpointAsync(string organizationId, string endpointId, string targetOrganizationId,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Post, $"endpoints/managed/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(endpointId)}/move",
            new JsonObject { ["target_organization_id"] = targetOrganizationId }, cancellationToken, goneIsDone: true);

    /// <summary>The endpoint groups of one organization (0.6.0), with their id and name.</summary>
    public async Task<IntegrationResult<IReadOnlyList<ExternalTenant>>> ListGroupsAsync(string organizationId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var groups = new List<ExternalTenant>();
            for (var page = 0; page < Action1Api.MaxPages; page++)
            {
                using var document = await GetAsync($"endpoints/groups/{Uri.EscapeDataString(organizationId)}",
                    $"from={page * Action1Api.PageSize}&limit={Action1Api.PageSize}", cancellationToken);
                var items = Items(document.RootElement);
                foreach (var item in items)
                {
                    if (Text(item, "id") is { Length: > 0 } id)
                    {
                        groups.Add(new ExternalTenant(id, Text(item, "name")));
                    }
                }

                if (items.Count < Action1Api.PageSize)
                {
                    break;
                }
            }

            return IntegrationResult<IReadOnlyList<ExternalTenant>>.Success(groups);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<IReadOnlyList<ExternalTenant>>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// Creates an endpoint group without filters (0.6.0) and returns its id: Fleeto adds and removes its members by hand.
    /// </summary>
    public Task<IntegrationResult<string>> CreateGroupAsync(string organizationId, string name, string description,
        CancellationToken cancellationToken = default) =>
        CreateAsync($"endpoints/groups/{Uri.EscapeDataString(organizationId)}", new JsonObject { ["name"] = name, ["description"] = description },
            "Action1 created the endpoint group but did not name its id. Remove it in the Action1 console; Fleeto creates it again.",
            cancellationToken);

    /// <summary>Renames an endpoint group (0.6.0); its filters and members stay.</summary>
    public Task<IntegrationResult> RenameGroupAsync(string organizationId, string groupId, string name,
        CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Patch, $"endpoints/groups/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(groupId)}",
            new JsonObject { ["name"] = name }, cancellationToken);

    /// <summary>Removes an endpoint group (0.6.0); one that no longer exists counts as removed.</summary>
    public Task<IntegrationResult> DeleteGroupAsync(string organizationId, string groupId, CancellationToken cancellationToken = default) =>
        ChangeAsync(HttpMethod.Delete, $"endpoints/groups/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(groupId)}", null,
            cancellationToken, goneIsDone: true);

    /// <summary>
    /// The members of an endpoint group (0.6.0): the endpoint id and whether it was added by hand, which is how Fleeto
    /// adds them, or by the filters of the group. A group that no longer exists gives a null value, so the caller can
    /// create it again.
    /// </summary>
    public async Task<IntegrationResult<IReadOnlyList<Action1GroupMember>?>> ListGroupMembersAsync(string organizationId, string groupId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var members = new List<Action1GroupMember>();
            for (var page = 0; page < Action1Api.MaxPages; page++)
            {
                using var document = await GetAsync(
                    $"endpoints/groups/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(groupId)}/contents",
                    $"from={page * Action1Api.PageSize}&limit={Action1Api.PageSize}", cancellationToken);
                var items = Items(document.RootElement);
                foreach (var item in items)
                {
                    if (Text(item, "id") is { Length: > 0 } id)
                    {
                        members.Add(new Action1GroupMember(id,
                            !string.Equals(Text(item, "added_via"), "criteria", StringComparison.OrdinalIgnoreCase)));
                    }
                }

                if (items.Count < Action1Api.PageSize)
                {
                    break;
                }
            }

            return IntegrationResult<IReadOnlyList<Action1GroupMember>?>.Success(members);
        }
        catch (Action1Exception ex) when (ex.Status == HttpStatusCode.NotFound)
        {
            return IntegrationResult<IReadOnlyList<Action1GroupMember>?>.Success(null);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<IReadOnlyList<Action1GroupMember>?>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// Adds endpoints to an endpoint group and removes others by hand (0.6.0), in one request per
    /// <see cref="Action1Api.PageSize"/> changes.
    /// </summary>
    public async Task<IntegrationResult> ChangeGroupMembersAsync(string organizationId, string groupId, IReadOnlyCollection<string> add,
        IReadOnlyCollection<string> remove, CancellationToken cancellationToken = default)
    {
        var changes = add.Select(id => (JsonNode)new JsonObject
            {
                ["method"] = "POST",
                ["data"] = new JsonObject { ["endpoint_id"] = id, ["type"] = "Endpoint" }
            })
            .Concat(remove.Select(id => (JsonNode)new JsonObject { ["method"] = "DELETE", ["endpoint_id"] = id }))
            .ToList();

        foreach (var chunk in changes.Chunk(Action1Api.PageSize))
        {
            var result = await ChangeAsync(HttpMethod.Post,
                $"endpoints/groups/{Uri.EscapeDataString(organizationId)}/{Uri.EscapeDataString(groupId)}/contents",
                new JsonArray(chunk), cancellationToken);
            if (!result.Ok)
            {
                return result;
            }
        }

        return IntegrationResult.Success();
    }

    /// <summary>Creates something and returns the id Action1 gave it. A 400 is permanent: Action1 refuses the request itself.</summary>
    private async Task<IntegrationResult<string>> CreateAsync(string path, JsonNode body, string withoutId,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await RequestAsync(HttpMethod.Post, path, null, body, cancellationToken);
            var id = Text(document.RootElement, "id");
            return string.IsNullOrEmpty(id) ? IntegrationResult<string>.Fail(withoutId, permanent: true) : IntegrationResult<string>.Success(id);
        }
        catch (Action1Exception ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            return IntegrationResult<string>.Fail(Refused(ex), permanent: true);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult<string>.Fail(ex.Message, ex.IsPermanent);
        }
    }

    private static string Refused(Action1Exception ex) =>
        ex.Detail is null ? "Action1 refused the change." : $"Action1 refused the change: {ex.Detail}";

    /// <summary>
    /// One change without an answer Fleeto needs. <paramref name="goneIsDone"/> makes a 404 a success, as for a deletion.
    /// A 400 is permanent here: Action1 refuses the change itself, and asking again changes nothing.
    /// </summary>
    private async Task<IntegrationResult> ChangeAsync(HttpMethod method, string path, JsonNode? body, CancellationToken cancellationToken,
        bool goneIsDone = false)
    {
        try
        {
            using var document = await RequestAsync(method, path, null, body, cancellationToken);
            return IntegrationResult.Success();
        }
        catch (Action1Exception ex) when (goneIsDone && ex.Status == HttpStatusCode.NotFound)
        {
            return IntegrationResult.Success();
        }
        catch (Action1Exception ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            return IntegrationResult.Fail(Refused(ex), permanent: true);
        }
        catch (Action1Exception ex)
        {
            return IntegrationResult.Fail(ex.Message, ex.IsPermanent);
        }
    }

    /// <summary>
    /// One GET against the API, with the budget, the bearer token and the error mapping. The caller owns the returned
    /// document. Throws <see cref="Action1Exception"/>; the patch steps of 0.4.0 build on this.
    /// </summary>
    internal Task<JsonDocument> GetAsync(string path, string? query, CancellationToken cancellationToken) =>
        RequestAsync(HttpMethod.Get, path, query, body: null, cancellationToken);

    /// <summary>
    /// One call against the API, with the budget, the bearer token and the error mapping. The caller owns the returned
    /// document. A <paramref name="body"/> is sent as JSON, which is how a deployment is started (0.4.0 step 3).
    /// </summary>
    private async Task<JsonDocument> RequestAsync(HttpMethod method, string path, string? query, JsonNode? body,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            await _budget.AcquireAsync(cancellationToken);
            using var request = new HttpRequestMessage(method, query is null ? path : $"{path}?{query}");
            if (body is not null)
            {
                request.Content = new StringContent(body.ToJsonString(Json), Encoding.UTF8, "application/json");
            }

            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await GetTokenAsync(force: false, cancellationToken));

            using var response = await SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var text = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(text))
                {
                    // A change such as a deletion may answer without a body.
                    return JsonDocument.Parse("{}");
                }

                try
                {
                    return JsonDocument.Parse(text);
                }
                catch (JsonException ex)
                {
                    throw new Action1Exception("Action1 answered something Fleeto could not read. Fleeto tries again.", permanent: false, ex);
                }
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized && attempt == 1)
            {
                // The token was refused (revoked, rotated or clock skew): get a new one and try this call once more.
                await GetTokenAsync(force: true, cancellationToken);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < RateLimitAttempts)
            {
                var pause = await RetryAfterAsync(response, attempt, cancellationToken);
                _budget.Pause(pause);
                _logger.LogInformation("Action1 asked to slow down; waiting {Seconds}s before attempt {Attempt}", pause.TotalSeconds, attempt + 1);
                await Task.Delay(pause, _time, cancellationToken);
                continue;
            }

            throw await FailureAsync(response, cancellationToken);
        }
    }

    private async Task<string> GetTokenAsync(bool force, CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();
        if (!force && _token is not null && now < _tokenExpiresAt - TokenMargin)
        {
            return _token;
        }

        await _tokenLock.WaitAsync(cancellationToken);
        try
        {
            now = _time.GetUtcNow();
            if (!force && _token is not null && now < _tokenExpiresAt - TokenMargin)
            {
                return _token;
            }

            // The token call counts against the same budget: Action1 counts requests across the whole API.
            await _budget.AcquireAsync(cancellationToken);
            using var request = new HttpRequestMessage(HttpMethod.Post, "oauth2/token")
            {
                Content = new FormUrlEncodedContent([
                    new KeyValuePair<string, string>("client_id", _credentials.ClientId),
                    new KeyValuePair<string, string>("client_secret", _credentials.ClientSecret)
                ])
            };

            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw response.StatusCode switch
                {
                    HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new Action1Exception(
                        "Action1 refused the API credentials. Check the client id, the client secret and the region in Settings, Integrations, " +
                        "and that the credentials still exist in the Action1 console.", permanent: true),
                    HttpStatusCode.TooManyRequests => new Action1Exception(
                        "Action1 is limiting the number of requests. Fleeto tries again.", permanent: false),
                    _ => await FailureAsync(response, cancellationToken)
                };
            }

            var body = await response.Content.ReadFromJsonSafeAsync(cancellationToken);
            var token = body is null ? null : Text(body.Value, "access_token");
            if (string.IsNullOrEmpty(token))
            {
                throw new Action1Exception("Action1 answered without a token. Fleeto tries again.", permanent: false);
            }

            var lifetime = body!.Value.TryGetProperty("expires_in", out var expires) && expires.TryGetInt32(out var seconds) && seconds > 0
                ? TimeSpan.FromSeconds(seconds)
                : TimeSpan.FromHours(1);
            _token = token;
            _tokenExpiresAt = _time.GetUtcNow() + lifetime;
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        try
        {
            return await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new Action1Exception("Action1 did not answer in time. Fleeto tries again.", permanent: false, ex);
        }
        catch (HttpRequestException ex)
        {
            throw new Action1Exception("Fleeto could not reach Action1. Check that the instance can reach the internet, then try again.",
                permanent: false, ex);
        }
    }

    /// <summary>The pause after a 429: Action1's own <c>retry_after</c> when it is sane, else its documented fallback of 2, 4, 8 seconds.</summary>
    private static async Task<TimeSpan> RetryAfterAsync(HttpResponseMessage response, int attempt, CancellationToken cancellationToken)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
        {
            return delta < MaxRetryAfter ? delta : MaxRetryAfter;
        }

        var body = await response.Content.ReadFromJsonSafeAsync(cancellationToken);
        if (body is { } root && root.TryGetProperty("details", out var details) &&
            details.ValueKind == JsonValueKind.Object && details.TryGetProperty("retry_after", out var retryAfter) &&
            retryAfter.TryGetInt32(out var seconds) && seconds > 0)
        {
            var pause = TimeSpan.FromSeconds(seconds);
            return pause < MaxRetryAfter ? pause : MaxRetryAfter;
        }

        return TimeSpan.FromSeconds(Math.Pow(2, attempt) * 2);
    }

    private static async Task<Action1Exception> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadFromJsonSafeAsync(cancellationToken);
        var detail = body is { } root ? Text(root, "user_message") : null;

        var status = response.StatusCode;
        return status switch
        {
            HttpStatusCode.Unauthorized => new Action1Exception(
                "Action1 refused the API credentials. Check them in Settings, Integrations.", permanent: true, status: status),
            HttpStatusCode.Forbidden => new Action1Exception(
                "The Action1 API credentials may not read this. Give their role access to the organizations Fleeto manages, in the Action1 console.",
                permanent: true, status: status),
            HttpStatusCode.NotFound => new Action1Exception(
                "Action1 does not know this organization. Check the mapping in Settings, Integrations.", permanent: true, status: status),
            HttpStatusCode.TooManyRequests => new Action1Exception(
                "Action1 is limiting the number of requests. Fleeto slows down and tries again.", permanent: false, status: status),
            _ => new Action1Exception(
                $"Action1 answered {(int)status}{(string.IsNullOrEmpty(detail) ? "" : $" ({Cut(detail, 300)})")}. Fleeto tries again.",
                permanent: false, status: status, detail: string.IsNullOrEmpty(detail) ? null : Cut(detail, 300))
        };
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    internal static IReadOnlyList<JsonElement> Items(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return items.EnumerateArray().ToList();
    }

    private static int TotalItems(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("total_items", out var total) && total.TryGetInt32(out var value)
            ? value
            : Items(root).Count;

    internal static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static HttpMessageHandler CreateHandler() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        UseCookies = false,
        ConnectTimeout = TimeSpan.FromSeconds(15),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    };

    public void Dispose()
    {
        _http.Dispose();
        _tokenLock.Dispose();
    }
}

internal static class Action1Json
{
    /// <summary>Reads a JSON body, giving null when there is none or it is not JSON. Error bodies are never trusted to be well formed.</summary>
    public static async Task<JsonElement?> ReadFromJsonSafeAsync(this HttpContent content, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            return document.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or IOException or ObjectDisposedException)
        {
            return null;
        }
    }
}
