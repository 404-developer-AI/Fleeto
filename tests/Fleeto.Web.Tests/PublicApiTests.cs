using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Api;
using Fleeto.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Endpoint = Fleeto.Core.Entities.Endpoint;

namespace Fleeto.Web.Tests;

/// <summary>The public API served over HTTP in a test server, on its own database, wired as Program wires it.</summary>
public sealed class ApiFixture : WebFixtureBase
{
    /// <summary>Low enough to reach in a test, high enough for the longest test that uses one key.</summary>
    public const int RequestsPerMinute = 40;

    private WebApplication? _app;

    public ApiFixture() : base("web_api")
    {
    }

    public HttpClient Client { get; private set; } = null!;

    protected override async Task<IServiceProvider> BuildServicesAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration["PublicApi:RequestsPerMinute"] = RequestsPerMinute.ToString(System.Globalization.CultureInfo.InvariantCulture);
        AddServices(builder.Services);
        builder.Services.AddRateLimiter(options => options.RejectionStatusCode = StatusCodes.Status429TooManyRequests);
        builder.Services.AddFleetoPublicApi(builder.Configuration);

        _app = builder.Build();
        _app.UseStatusCodePages();
        _app.UseFleetoPublicApiProblems();
        _app.UseRouting();
        _app.UseRateLimiter();
        _app.MapFleetoPublicApi();
        await _app.StartAsync();
        Client = _app.GetTestClient();
        return _app.Services;
    }

    public override async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        await Database.DisposeAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class ApiCollection : ICollectionFixture<ApiFixture>
{
    public const string Name = "web-api-database";
}

/// <summary>
/// Guarantees of the public API (CLAUDE.md, Public API; ARCHITECTURE.md §6): only a valid, unrevoked, unexpired key gets in; a key
/// limited to clients never reads another client; agent-only endpoints expose no managed features; every call is audited with the key
/// as actor; lists page with keyset cursors; keys are rate limited; errors are problem details; and the OpenAPI document and
/// MD-Files/API.md describe exactly the same endpoints.
/// </summary>
[Collection(ApiCollection.Name)]
public sealed class PublicApiTests
{
    private readonly ApiFixture _fixture;

    public PublicApiTests(ApiFixture fixture)
    {
        _fixture = fixture;
    }

    private ApiKeyService Keys => _fixture.Services.GetRequiredService<ApiKeyService>();
    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    private async Task<(Guid Id, string Token)> CreateKeyAsync(params Guid[] clientIds)
    {
        var result = await Keys.CreateAsync(WebFixtureBase.Admin(), new ApiKeyInput("Test key", clientIds.Length == 0, clientIds, TimeSpan.FromDays(365)));
        Assert.True(result.Success, result.Problem);
        return (result.Value!.Id, result.Value.Token);
    }

    private async Task<HttpResponseMessage> GetAsync(string? token, string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await _fixture.Client.SendAsync(request);
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await JsonAsync(response);
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("title").GetString()));
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("detail").GetString()));
    }

    private static List<string> Ids(JsonElement page) =>
        page.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetString()!).ToList();

    [Fact]
    public async Task Calls_without_a_valid_key_are_refused()
    {
        var (keyId, token) = await CreateKeyAsync();
        var wrongSecret = $"{OpaqueTokens.ApiKeyPrefix}_{keyId:N}_{Convert.ToBase64String(new byte[32]).TrimEnd('=').Replace('+', '-').Replace('/', '_')}";
        var (unknownToken, _, _) = OpaqueTokens.Create(OpaqueTokens.ApiKeyPrefix);

        foreach (var candidate in new string?[] { null, "not-a-key", unknownToken, wrongSecret, token.Replace("flt_", "fet_") })
        {
            var response = await GetAsync(candidate, "/api/v1/clients");
            await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ApiProblems.Unauthorized);
            Assert.Contains(response.Headers.WwwAuthenticate, h => h.Scheme == "Bearer");
        }

        // A wrong secret for a real key is audited; the wrong secret itself never is.
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var failure = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == AuditActions.ApiKeyAuthenticationFailed && a.TargetId == keyId.ToString());
            Assert.Equal(AuditActorType.ApiKey, failure.ActorType);
            Assert.DoesNotContain(wrongSecret.Split('_')[2], failure.DetailsJson, StringComparison.Ordinal);
        }

        Assert.Equal(HttpStatusCode.OK, (await GetAsync(token, "/api/v1/clients")).StatusCode);

        // Expired: refused from the moment it expires.
        var (expiredId, expiredToken) = await CreateKeyAsync();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.ApiKeys.Where(k => k.Id == expiredId).ExecuteUpdateAsync(s => s.SetProperty(k => k.ExpiresAt, Now.AddSeconds(-1)));
        }

        await AssertProblemAsync(await GetAsync(expiredToken, "/api/v1/clients"), HttpStatusCode.Unauthorized, ApiProblems.Unauthorized);

        // Revoked: refused on the very next call.
        Assert.True((await Keys.RevokeAsync(WebFixtureBase.Admin(), keyId)).Success);
        await AssertProblemAsync(await GetAsync(token, "/api/v1/clients"), HttpStatusCode.Unauthorized, ApiProblems.Unauthorized);
    }

    [Fact]
    public async Task Every_call_is_audited_with_the_key_as_actor_and_the_key_never_reaches_a_log()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var (keyId, token) = await CreateKeyAsync(client.Id);

        var response = await GetAsync(token, $"/api/v1/clients/{client.Id}?unused=1");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(client.Code, (await JsonAsync(response)).GetProperty("code").GetString());
        await AssertProblemAsync(await GetAsync(token, $"/api/v1/clients/{Guid.NewGuid()}"), HttpStatusCode.NotFound, ApiProblems.NotFound);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var entries = await db.AuditEntries.AsNoTracking().Where(a => a.Action == AuditActions.ApiRequest && a.ActorId == keyId.ToString())
            .OrderBy(a => a.Id).ToListAsync();
        Assert.Equal(2, entries.Count);
        Assert.All(entries, e => Assert.Equal(AuditActorType.ApiKey, e.ActorType));
        Assert.Equal("/api/v1/clients/{clientId:guid}", entries[0].TargetId);
        using (var details = JsonDocument.Parse(entries[0].DetailsJson))
        {
            Assert.Equal(200, details.RootElement.GetProperty("status").GetInt32());
            Assert.Equal("?unused=1", details.RootElement.GetProperty("query").GetString());
        }

        using (var details = JsonDocument.Parse(entries[1].DetailsJson))
        {
            Assert.Equal(404, details.RootElement.GetProperty("status").GetInt32());
        }

        Assert.NotNull((await db.ApiKeys.AsNoTracking().SingleAsync(k => k.Id == keyId)).LastUsedAt);
        var secret = token.Split('_', 3)[2];
        Assert.DoesNotContain(_fixture.Logs.Messages, m => m.Contains(secret, StringComparison.Ordinal));
        Assert.DoesNotContain(await db.AuditEntries.AsNoTracking().Select(a => a.DetailsJson).ToListAsync(), d => d.Contains(secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_key_limited_to_clients_cannot_read_another_client()
    {
        var database = _fixture.Database;
        await database.LoadTestLicenseAsync(1000);
        var clientA = await database.CreateClientAsync();
        var clientB = await database.CreateClientAsync();
        var siteA = await database.CreateSiteAsync(clientA.Id);
        var siteB = await database.CreateSiteAsync(clientB.Id);
        var endpointA = await database.CreateEndpointAsync(siteA, EndpointTier.Managed, "A-SERVER");
        var endpointB = await database.CreateEndpointAsync(siteB, EndpointTier.Managed, "B-SERVER");
        var alertB = await CreateAlertAsync(endpointB);
        var jobB = await CreateJobAsync(endpointB);
        var noteB = await _fixture.Services.GetRequiredService<NoteService>().CreateAsync(WebFixtureBase.Technician(), endpointB.Id, "Note of client B");
        Assert.True(noteB.Success);

        var (_, token) = await CreateKeyAsync(clientA.Id);

        // Lists: only client A, also when client B is asked for explicitly.
        Assert.Equal([clientA.Id.ToString()], Ids(await JsonAsync(await GetAsync(token, "/api/v1/clients"))));
        Assert.Equal([siteA.Id.ToString()], Ids(await JsonAsync(await GetAsync(token, "/api/v1/sites"))));
        Assert.Empty(Ids(await JsonAsync(await GetAsync(token, $"/api/v1/sites?clientId={clientB.Id}"))));
        Assert.Equal([endpointA.Id.ToString()], Ids(await JsonAsync(await GetAsync(token, "/api/v1/endpoints"))));
        Assert.Empty(Ids(await JsonAsync(await GetAsync(token, $"/api/v1/endpoints?clientId={clientB.Id}"))));
        Assert.DoesNotContain(alertB.Id.ToString(), Ids(await JsonAsync(await GetAsync(token, "/api/v1/alerts"))));
        Assert.Empty(Ids(await JsonAsync(await GetAsync(token, $"/api/v1/alerts?endpointId={endpointB.Id}"))));
        Assert.DoesNotContain(jobB.Id.ToString(), Ids(await JsonAsync(await GetAsync(token, "/api/v1/jobs"))));
        Assert.Empty(Ids(await JsonAsync(await GetAsync(token, $"/api/v1/jobs?clientId={clientB.Id}"))));

        // Single resources: not found, the same answer as an id that does not exist.
        foreach (var path in new[]
                 {
                     $"/api/v1/clients/{clientB.Id}", $"/api/v1/sites/{siteB.Id}", $"/api/v1/endpoints/{endpointB.Id}",
                     $"/api/v1/endpoints/{endpointB.Id}/inventory", $"/api/v1/endpoints/{endpointB.Id}/checks", $"/api/v1/endpoints/{endpointB.Id}/notes",
                     $"/api/v1/alerts/{alertB.Id}", $"/api/v1/jobs/{jobB.Id}", $"/api/v1/jobs/{jobB.Id}/output"
                 })
        {
            await AssertProblemAsync(await GetAsync(token, path), HttpStatusCode.NotFound, ApiProblems.NotFound);
        }

        // Sanity: the same kinds of reads work for client A.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(token, $"/api/v1/endpoints/{endpointA.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(token, $"/api/v1/endpoints/{endpointA.Id}/checks")).StatusCode);
    }

    [Fact]
    public async Task Agent_only_endpoints_expose_no_checks_or_notes()
    {
        var database = _fixture.Database;
        await database.LoadTestLicenseAsync(1000);
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        var agentOnly = await database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-FREE");
        var managed = await database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-MANAGED");
        Assert.True((await _fixture.Services.GetRequiredService<NoteService>().CreateAsync(WebFixtureBase.Technician(), managed.Id, "Replaced the fan.")).Success);
        var (_, token) = await CreateKeyAsync(client.Id);

        await AssertProblemAsync(await GetAsync(token, $"/api/v1/endpoints/{agentOnly.Id}/checks"), HttpStatusCode.Conflict, ApiProblems.EndpointNotManaged);
        await AssertProblemAsync(await GetAsync(token, $"/api/v1/endpoints/{agentOnly.Id}/notes"), HttpStatusCode.Conflict, ApiProblems.EndpointNotManaged);

        var checks = await JsonAsync(await GetAsync(token, $"/api/v1/endpoints/{managed.Id}/checks"));
        Assert.Equal(managed.Id.ToString(), checks.GetProperty("endpointId").GetString());
        var notes = await JsonAsync(await GetAsync(token, $"/api/v1/endpoints/{managed.Id}/notes"));
        Assert.Equal("Replaced the fan.", notes.GetProperty("items")[0].GetProperty("body").GetString());

        // The inventory is part of the free tier; an endpoint that has not reported one yet says so.
        await AssertProblemAsync(await GetAsync(token, $"/api/v1/endpoints/{agentOnly.Id}/inventory"), HttpStatusCode.NotFound, ApiProblems.NotFound);
    }

    [Fact]
    public async Task Endpoints_use_the_documented_json_shape_and_are_not_cached()
    {
        var database = _fixture.Database;
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        var endpoint = await database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "SRV-SHAPE", EndpointClass.Server);
        var (_, token) = await CreateKeyAsync(client.Id);

        var response = await GetAsync(token, $"/api/v1/endpoints/{endpoint.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var json = await JsonAsync(response);
        Assert.Equal("SRV-SHAPE", json.GetProperty("hostname").GetString());
        Assert.Equal("server", json.GetProperty("class").GetString());
        Assert.Equal("agent_only", json.GetProperty("tier").GetString());
        Assert.Equal("agent", json.GetProperty("source").GetString());
        Assert.False(json.GetProperty("online").GetBoolean());
        Assert.Equal("windows", json.GetProperty("os").GetProperty("platform").GetString());
        Assert.EndsWith("Z", json.GetProperty("enrolledAt").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("maintenance").ValueKind);
    }

    [Fact]
    public async Task Lists_page_with_a_keyset_cursor_and_refuse_invalid_parameters()
    {
        var database = _fixture.Database;
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        foreach (var hostname in new[] { "PAGE-C", "PAGE-A", "PAGE-B" })
        {
            await database.CreateEndpointAsync(site, hostname: hostname);
        }

        var (_, token) = await CreateKeyAsync(client.Id);
        var first = await JsonAsync(await GetAsync(token, "/api/v1/endpoints?limit=2"));
        Assert.Equal(["PAGE-A", "PAGE-B"], first.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("hostname").GetString()!).ToArray());
        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.False(string.IsNullOrEmpty(cursor));

        var second = await JsonAsync(await GetAsync(token, $"/api/v1/endpoints?limit=2&cursor={Uri.EscapeDataString(cursor!)}"));
        Assert.Equal(["PAGE-C"], second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("hostname").GetString()!).ToArray());
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);

        await AssertProblemAsync(await GetAsync(token, "/api/v1/endpoints?cursor=not-a-cursor"), HttpStatusCode.BadRequest, ApiProblems.InvalidParameter);
        await AssertProblemAsync(await GetAsync(token, "/api/v1/endpoints?limit=0"), HttpStatusCode.BadRequest, ApiProblems.InvalidParameter);
        await AssertProblemAsync(await GetAsync(token, "/api/v1/endpoints?limit=201"), HttpStatusCode.BadRequest, ApiProblems.InvalidParameter);
        await AssertProblemAsync(await GetAsync(token, "/api/v1/endpoints?class=laptop"), HttpStatusCode.BadRequest, ApiProblems.InvalidParameter);
        await AssertProblemAsync(await GetAsync(token, "/api/v1/alerts?state=snoozed"), HttpStatusCode.BadRequest, ApiProblems.InvalidParameter);
        await AssertProblemAsync(await GetAsync(token, "/api/v1/endpoints?clientId=not-a-uuid"), HttpStatusCode.BadRequest, ApiProblems.InvalidParameter);
    }

    [Fact]
    public async Task Unknown_paths_and_other_methods_answer_with_problem_details()
    {
        var (_, token) = await CreateKeyAsync();
        await AssertProblemAsync(await GetAsync(token, "/api/v1/does-not-exist"), HttpStatusCode.NotFound, ApiProblems.NotFound);

        using var post = new HttpRequestMessage(HttpMethod.Post, "/api/v1/clients");
        post.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await AssertProblemAsync(await _fixture.Client.SendAsync(post), HttpStatusCode.MethodNotAllowed, ApiProblems.MethodNotAllowed);
    }

    [Fact]
    public async Task A_key_is_rate_limited_on_its_own_budget()
    {
        var (_, token) = await CreateKeyAsync();
        var (_, other) = await CreateKeyAsync();
        HttpResponseMessage? limited = null;
        for (var i = 0; i < ApiFixture.RequestsPerMinute + 5 && limited is null; i++)
        {
            var response = await GetAsync(token, "/api/v1/clients?limit=1");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                Assert.True(i >= ApiFixture.RequestsPerMinute, $"Limited after {i} requests, expected at least {ApiFixture.RequestsPerMinute}.");
                limited = response;
            }
        }

        Assert.NotNull(limited);
        await AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, ApiProblems.RateLimited);
        Assert.NotNull(limited.Headers.RetryAfter);

        // Another key is not affected.
        Assert.Equal(HttpStatusCode.OK, (await GetAsync(other, "/api/v1/clients?limit=1")).StatusCode);
    }

    [Fact]
    public async Task Api_keys_are_created_by_admins_only_and_only_a_hash_is_stored()
    {
        var client = await _fixture.Database.CreateClientAsync();
        Assert.False((await Keys.CreateAsync(WebFixtureBase.Technician(), new ApiKeyInput("Nope", true, [], null))).Success);
        Assert.False((await Keys.CreateAsync(WebFixtureBase.Admin(), new ApiKeyInput(" ", true, [], null))).Success);
        Assert.False((await Keys.CreateAsync(WebFixtureBase.Admin(), new ApiKeyInput("No clients", false, [], null))).Success);
        Assert.False((await Keys.CreateAsync(WebFixtureBase.Admin(), new ApiKeyInput("Unknown client", false, [Guid.NewGuid()], null))).Success);
        Assert.False((await Keys.CreateAsync(WebFixtureBase.Admin(), new ApiKeyInput("Odd expiry", true, [], TimeSpan.FromDays(3)))).Success);

        var created = await Keys.CreateAsync(WebFixtureBase.Admin(), new ApiKeyInput("PSA sync", false, [client.Id], null));
        Assert.True(created.Success, created.Problem);
        Assert.True(OpaqueTokens.TryParse(created.Value!.Token, OpaqueTokens.ApiKeyPrefix, out var id, out var hash));
        Assert.Equal(created.Value.Id, id);
        var secret = created.Value.Token.Split('_', 3)[2];

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var row = await db.ApiKeys.AsNoTracking().Include(k => k.Clients).SingleAsync(k => k.Id == id);
        Assert.Equal(hash, row.SecretHash);
        Assert.DoesNotContain(secret, row.SecretHash, StringComparison.Ordinal);
        Assert.Null(row.ExpiresAt);
        Assert.Equal([client.Id], row.Clients.Select(c => c.ClientId).ToArray());
        var audit = await db.AuditEntries.AsNoTracking().SingleAsync(a => a.Action == AuditActions.ApiKeyCreated && a.TargetId == id.ToString());
        Assert.DoesNotContain(secret, audit.DetailsJson, StringComparison.Ordinal);
        Assert.DoesNotContain(row.SecretHash, audit.DetailsJson, StringComparison.Ordinal);

        var listed = (await Keys.ListAsync(WebFixtureBase.Admin())).Single(k => k.Id == id);
        Assert.Equal(ApiKeyState.Active, listed.State);
        Assert.DoesNotContain(secret, listed.DisplayPrefix, StringComparison.Ordinal);
        await Assert.ThrowsAsync<Security.AccessDeniedException>(() => Keys.ListAsync(WebFixtureBase.Technician()));

        Assert.False((await Keys.RevokeAsync(WebFixtureBase.Technician(), id)).Success);
        Assert.True((await Keys.RevokeAsync(WebFixtureBase.Admin(), id)).Success);
        Assert.False((await Keys.RevokeAsync(WebFixtureBase.Admin(), id)).Success);
        Assert.Equal(ApiKeyState.Revoked, (await Keys.ListAsync(WebFixtureBase.Admin())).Single(k => k.Id == id).State);
    }

    [Fact]
    public async Task The_openapi_document_and_the_api_documentation_describe_the_same_endpoints()
    {
        var response = await GetAsync(null, PublicApi.OpenApiPath);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await JsonAsync(response);
        var built = document.GetProperty("paths").EnumerateObject()
            .SelectMany(p => p.Value.EnumerateObject().Select(o => $"{o.Name.ToUpperInvariant()} {p.Name}"))
            .ToHashSet();
        Assert.True(built.Count >= 14, "The OpenAPI document lists fewer operations than the API has; the test has stopped testing anything.");

        // Enumeration values leave as documented snake_case strings, not C# names.
        var schemas = document.GetProperty("components").GetProperty("schemas").GetRawText();
        Assert.Contains("\"agent_only\"", schemas, StringComparison.Ordinal);
        Assert.Contains("\"pending_signature\"", schemas, StringComparison.Ordinal);
        Assert.DoesNotContain("\"string\",\"integer\"", schemas.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.DoesNotContain("\"integer\",\"string\"", schemas.Replace(" ", string.Empty), StringComparison.Ordinal);
        Assert.Contains("apiKey", document.GetProperty("components").GetProperty("securitySchemes").GetRawText(), StringComparison.Ordinal);

        var markdown = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "MD-Files", "API.md"));
        var documented = Regex.Matches(markdown, @"^#{2,4} (GET|POST|PUT|PATCH|DELETE) (/api/v1/\S+)\s*$", RegexOptions.Multiline)
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}")
            .ToHashSet();

        var undocumented = built.Except(documented).Order().ToList();
        var notBuilt = documented.Except(built).Order().ToList();
        Assert.True(undocumented.Count == 0, "Endpoints missing from MD-Files/API.md (add a '### GET /api/v1/...' section):\n" + string.Join("\n", undocumented));
        Assert.True(notBuilt.Count == 0, "Endpoints documented in MD-Files/API.md that the API does not have:\n" + string.Join("\n", notBuilt));
    }

    [Fact]
    public async Task Clients_carry_their_tags_and_filter_on_a_tag_within_the_scope_of_the_key()
    {
        var database = _fixture.Database;
        var clientA = await database.CreateClientAsync();
        var clientB = await database.CreateClientAsync();
        var tag = "gold-" + Guid.NewGuid().ToString("N")[..6];
        var tags = _fixture.Services.GetRequiredService<TagService>();
        Assert.True((await tags.SetClientTagsAsync(WebFixtureBase.Technician(), clientA.Id, [tag, "Zeta"])).Success);
        Assert.True((await tags.SetClientTagsAsync(WebFixtureBase.Technician(), clientB.Id, [tag])).Success);

        var (_, all) = await CreateKeyAsync();
        var client = await JsonAsync(await GetAsync(all, $"/api/v1/clients/{clientA.Id}"));
        var shown = client.GetProperty("tags").EnumerateArray().Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Equal([tag, "Zeta"], shown);
        Assert.Equal(TagRules.DefaultColor(tag).ToString().ToLowerInvariant(), client.GetProperty("tags")[0].GetProperty("color").GetString());

        // The filter matches the full name without regard to case, never a part of it.
        Assert.Equal(new[] { clientA.Id.ToString(), clientB.Id.ToString() }.Order().ToList(),
            Ids(await JsonAsync(await GetAsync(all, $"/api/v1/clients?tag={tag.ToUpperInvariant()}&limit=200"))).Order().ToList());
        Assert.Empty(Ids(await JsonAsync(await GetAsync(all, $"/api/v1/clients?tag={tag[..4]}"))));
        await AssertProblemAsync(await GetAsync(all, $"/api/v1/clients?tag={new string('a', TagRules.MaxLength + 1)}"),
            HttpStatusCode.BadRequest, ApiProblems.InvalidParameter);

        // A key limited to client A finds only client A with the shared tag.
        var (_, limited) = await CreateKeyAsync(clientA.Id);
        Assert.Equal([clientA.Id.ToString()], Ids(await JsonAsync(await GetAsync(limited, $"/api/v1/clients?tag={tag}"))));
    }

    [Fact]
    public void Every_internal_value_has_a_public_api_value()
    {
        foreach (var value in Enum.GetValues<EndpointClass>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<EndpointTier>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<EndpointSource>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<AlertKind>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<AlertSeverity>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<AlertState>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<CheckStatus>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<CheckSource>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<JobState>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<JobResult>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<JobOutputState>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<ScriptLanguage>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<PatchSeverity>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<PatchCoverage>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<TagColor>()) PublicApiQueries.Map(value);
        foreach (var value in Enum.GetValues<MaintenanceSource>())
        {
            PublicApiQueries.Map(new EffectiveMaintenance(value, DateTime.UtcNow, null, null, null));
        }

        // Query filters accept every state the responses can contain.
        Assert.Equal(Enum.GetValues<JobState>().Length, PublicApiEndpoints.JobStateValues.Count);
        Assert.Equal("disk_free", PublicApiQueries.CheckTypeName(CheckType.DiskFree));
    }

    private async Task<Alert> CreateAlertAsync(Endpoint endpoint)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var alert = new Alert
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = AlertKind.Offline, Severity = AlertSeverity.Critical,
            State = AlertState.Open, Title = $"{endpoint.Hostname} is offline", OpenedAt = Now, UpdatedAt = Now
        };
        db.Alerts.Add(alert);
        await db.SaveChangesAsync();
        return alert;
    }

    [Fact]
    public async Task A_job_that_ran_as_the_signed_in_user_names_the_account()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var endpoint = await _fixture.Database.CreateEndpointAsync(await _fixture.Database.CreateSiteAsync(client.Id), EndpointTier.Managed);
        var job = await CreateJobAsync(endpoint, JobRunAs.LoggedOnUser, @"CONTOSO\jan");
        var (_, token) = await CreateKeyAsync();

        var json = await JsonAsync(await GetAsync(token, $"/api/v1/jobs/{job.Id}"));

        Assert.Equal("logged_on_user", json.GetProperty("runAs").GetString());
        Assert.Equal(job.RunAsAccount, json.GetProperty("runAsAccount").GetString());
        Assert.Equal(job.RunAsChosenAccount, json.GetProperty("runAsChosenAccount").GetString());
    }

    private async Task<Job> CreateJobAsync(Endpoint endpoint, JobRunAs runAs = JobRunAs.Service, string? runAsAccount = null)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = Guid.NewGuid(), ScriptName = "Inventory refresh",
            ScriptVersionNumber = 1, Language = ScriptLanguage.PowerShell, ScriptSha256 = new string('a', 64), TimeoutSeconds = 600,
            MaxOutputBytes = ScriptRules.DefaultMaxOutputBytes, CreatedAt = Now, ValidUntil = Now.AddHours(1), InitiatedByUserId = Guid.NewGuid(),
            InitiatedByName = "Technician", RunAs = runAs, RunAsAccount = runAsAccount,
            RunAsUserId = runAsAccount is null ? null : "S-1-5-21-1-1001", RunAsChosenAccount = runAsAccount
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CLAUDE.md")) && Directory.Exists(Path.Combine(directory.FullName, "MD-Files")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("The repository root (with CLAUDE.md and MD-Files) was not found above the test output directory.");
    }
}
