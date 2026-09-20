using System.Net;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Microsoft.Extensions.Time.Testing;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The Action1 client against a stand-in server (0.4.0): Action1 publishes no downloadable contract, so what Fleeto
/// depends on is pinned here — the token, the request budget, what a 429 does, and how a failure reads for an admin.
/// </summary>
public class Action1ClientTests
{
    // Values that no message would use by itself, so a test can prove they are never repeated back to the admin.
    private static readonly Action1Credentials Credentials = new("api-key-a1b2c3d4@action1.com", "n0tinamessage-9f8e7d");

    [Fact]
    public async Task One_token_serves_every_call_until_it_is_about_to_expire()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(time);
        using var client = Create(handler, time);

        Assert.True((await client.TestConnectionAsync()).Ok);
        Assert.True((await client.TestConnectionAsync()).Ok);

        Assert.Equal(1, handler.TokenRequests);
        Assert.Equal(2, handler.ApiRequests);

        // Inside the hour the token lasts, but past the margin Fleeto keeps: a new token, without a refused call.
        time.Advance(TimeSpan.FromMinutes(56));
        Assert.True((await client.TestConnectionAsync()).Ok);
        Assert.Equal(2, handler.TokenRequests);
    }

    [Fact]
    public async Task A_refused_token_is_renewed_once_and_the_call_is_made_again()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(time) { RefuseNextApiCallOnce = true };
        using var client = Create(handler, time);

        var result = await client.TestConnectionAsync();

        Assert.True(result.Ok);
        Assert.Equal(2, handler.TokenRequests);
        Assert.Equal(2, handler.ApiRequests);
    }

    [Fact]
    public async Task Action1_asking_to_slow_down_is_obeyed_with_its_own_retry_after()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(time) { RateLimitNextApiCallOnce = true, RetryAfterSeconds = 7 };
        using var client = Create(handler, time);

        var call = client.TestConnectionAsync();
        // Nothing happens until the pause Action1 asked for has passed.
        Assert.False(call.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(7));
        var result = await call;

        Assert.True(result.Ok);
        Assert.Equal(2, handler.ApiRequests);
        Assert.Equal(TimeSpan.FromSeconds(7), handler.LastGap);
    }

    [Fact]
    public async Task A_failure_tells_the_admin_what_to_do_and_never_repeats_the_credentials()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(time) { TokenStatus = HttpStatusCode.Unauthorized };
        using var client = Create(handler, time);

        var result = await client.TestConnectionAsync();

        Assert.False(result.Ok);
        Assert.Contains("Settings, Integrations", result.Message);
        Assert.DoesNotContain(Credentials.ClientSecret, result.Message);
        Assert.DoesNotContain(Credentials.ClientId, result.Message);
    }

    [Fact]
    public async Task The_role_of_the_credentials_not_being_allowed_points_at_the_Action1_console()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(time) { ApiStatus = HttpStatusCode.Forbidden };
        using var client = Create(handler, time);

        var result = await client.TestConnectionAsync();

        Assert.False(result.Ok);
        Assert.Contains("Action1 console", result.Message);
    }

    [Fact]
    public async Task Every_organization_is_read_over_as_many_pages_as_Action1_returns()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        var handler = new StubHandler(time) { Organizations = Enumerable.Range(1, 60).ToList() };
        using var client = Create(handler, time, permitsPerMinute: 60);

        var result = await client.ListTenantsAsync();

        Assert.True(result.Ok);
        Assert.Equal(60, result.Value!.Count);
        Assert.Equal("org-1", result.Value[0].Id);
        Assert.Equal("Organization 60", result.Value[59].Name);
        // 50 plus 10: two pages, and no third call once a page is short.
        Assert.Equal(2, handler.ApiRequests);
    }

    [Fact]
    public async Task The_budget_paces_calls_so_Action1_is_not_asked_more_than_it_wants()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 20, 12, 0, 0, TimeSpan.Zero));
        // Two permits a minute: one call takes both (token plus request), the second waits for a refill.
        var handler = new StubHandler(time);
        using var client = Create(handler, time, permitsPerMinute: 2, capacity: 2);

        Assert.True((await client.TestConnectionAsync()).Ok);
        var second = client.TestConnectionAsync();

        Assert.False(second.IsCompleted);
        time.Advance(TimeSpan.FromSeconds(30));
        Assert.True((await second).Ok);
    }

    [Fact]
    public void A_time_from_Action1_is_read_as_UTC_and_nonsense_gives_nothing()
    {
        Assert.Equal(new DateTime(2026, 9, 20, 14, 11, 14, DateTimeKind.Utc), Action1Api.ParseTime("2026-09-20_14-11-14"));
        Assert.Equal(DateTimeKind.Utc, Action1Api.ParseTime("2026-09-20_14-11-14")!.Value.Kind);
        Assert.Null(Action1Api.ParseTime(""));
        Assert.Null(Action1Api.ParseTime("2026-09-20T14:11:14Z"));
        Assert.Null(Action1Api.ParseTime("yesterday"));
    }

    [Fact]
    public void Every_region_has_its_own_base_address_and_Europe_is_the_EU_one()
    {
        Assert.Equal("https://app.eu.action1.com/api/3.0/", Action1Api.BaseAddress(Action1Region.Europe).ToString());
        var addresses = Enum.GetValues<Action1Region>().Select(r => Action1Api.BaseAddress(r).ToString()).ToList();
        Assert.Equal(addresses.Count, addresses.Distinct().Count());
        Assert.All(addresses, a => Assert.StartsWith("https://", a));
    }

    private static Action1Client Create(StubHandler handler, TimeProvider time, int permitsPerMinute = 60, int? capacity = null) =>
        new(Credentials, Action1Region.Europe, new RequestBudget(permitsPerMinute, time, capacity), time, handler: handler);

    /// <summary>Answers like Action1 does, and records what was asked and when.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly FakeTimeProvider _time;
        private DateTimeOffset? _lastRequest;

        public StubHandler(FakeTimeProvider time) => _time = time;

        public int TokenRequests { get; private set; }
        public int ApiRequests { get; private set; }
        public TimeSpan? LastGap { get; private set; }
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;
        public HttpStatusCode ApiStatus { get; init; } = HttpStatusCode.OK;
        public bool RefuseNextApiCallOnce { get; set; }
        public bool RateLimitNextApiCallOnce { get; set; }
        public int RetryAfterSeconds { get; init; } = 3;
        public List<int> Organizations { get; init; } = [1];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var now = _time.GetUtcNow();
            LastGap = _lastRequest is { } previous ? now - previous : null;
            _lastRequest = now;

            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                TokenRequests++;
                return Task.FromResult(TokenStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, $$"""{"access_token":"token-{{TokenRequests}}","expires_in":3600,"token_type":"Bearer"}""")
                    : Json(TokenStatus, """{"user_message":"no"}"""));
            }

            ApiRequests++;
            if (RefuseNextApiCallOnce)
            {
                RefuseNextApiCallOnce = false;
                return Task.FromResult(Json(HttpStatusCode.Unauthorized, """{"user_message":"token expired"}"""));
            }

            if (RateLimitNextApiCallOnce)
            {
                RateLimitNextApiCallOnce = false;
                return Task.FromResult(Json(HttpStatusCode.TooManyRequests,
                    $$$"""{"status":429,"user_message":"slow down","details":{"retry_after":{{{RetryAfterSeconds}}}}}"""));
            }

            if (ApiStatus != HttpStatusCode.OK)
            {
                return Task.FromResult(Json(ApiStatus, """{"user_message":"not allowed"}"""));
            }

            var query = System.Web.HttpUtility.ParseQueryString(request.RequestUri.Query);
            var from = int.TryParse(query["from"], out var f) ? f : 0;
            var limit = int.TryParse(query["limit"], out var l) ? l : Action1Api.PageSize;
            var items = Organizations.Skip(from).Take(limit)
                .Select(i => $$$"""{"id":"org-{{{i}}}","name":"Organization {{{i}}}"}""");
            return Task.FromResult(Json(HttpStatusCode.OK,
                $$"""{"items":[{{string.Join(",", items)}}],"total_items":{{Organizations.Count}}}"""));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
