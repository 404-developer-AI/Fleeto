using System.Net;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Workers.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Security advisories in the workers (0.8.0 step 0): the feed of an enabled source is fetched every hour, a new item is added once
/// and a known one updated, an item the feed drops stays, and a failure or a redirect keeps what was fetched and says why.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AdvisoryFeedServiceTests
{
    private static readonly string FeedUrl = AdvisorySources.Find(AdvisorySources.Ccb)!.FeedUrl;

    private readonly WorkersFixture _fixture;

    public AdvisoryFeedServiceTests(WorkersFixture fixture) => _fixture = fixture;

    private sealed class FakeHandler : HttpMessageHandler
    {
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string Body { get; set; } = string.Empty;
        public string? Location { get; set; }
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var response = new HttpResponseMessage(Status) { Content = new StringContent(Body, Encoding.UTF8, "application/rss+xml") };
            if (Location is not null)
            {
                response.Headers.Location = new Uri(Location);
            }

            return Task.FromResult(response);
        }
    }

    private static string Feed(params (string Slug, string Title)[] items) => DatedFeed("Mon, 26 Oct 2026 08:15:40 +0100", items);

    private static string DatedFeed(string published, params (string Slug, string Title)[] items) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <rss version="2.0"><channel><title>CCB Advisories Feed</title><description>CCB Advisories</description>
        {string.Concat(items.Select(i => $"<item><title>{i.Title}</title><link>https://ccb.belgium.be/advisories/{i.Slug}</link><description>CCB Advisories</description><guid isPermaLink=\"true\">https://ccb.belgium.be/advisories/{i.Slug}</guid><pubDate>{published}</pubDate></item>"))}
        </channel></rss>
        """;

    private AdvisoryFeedService Service(FakeHandler handler) =>
        new(_fixture.Db.DbFactory, _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<AdvisoryFeedService>.Instance, handler);

    private async Task ResetAsync()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Advisories.ExecuteDeleteAsync();
        await db.AdvisorySources.ExecuteUpdateAsync(s => s.SetProperty(x => x.Enabled, true).SetProperty(x => x.LastAttemptAt, (DateTime?)null)
            .SetProperty(x => x.LastSuccessAt, (DateTime?)null).SetProperty(x => x.FailingSince, (DateTime?)null).SetProperty(x => x.Problem, (string?)null));
    }

    [Fact]
    public async Task The_feed_is_fetched_hourly_and_adds_each_advisory_once_and_keeps_the_ones_it_drops()
    {
        await ResetAsync();
        var handler = new FakeHandler { Body = Feed(("bind", "Warning: BIND"), ("gitlab", "Warning: GitLab")) };
        var service = Service(handler);

        Assert.Equal([AdvisorySources.Ccb], await service.RefreshDueAsync(CancellationToken.None));
        Assert.Equal(FeedUrl, handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("Fleeto/", handler.Requests[0].Headers.UserAgent.ToString());
        // Not due again within the hour.
        Assert.Empty(await service.RefreshDueAsync(CancellationToken.None));

        _fixture.Db.Time.Advance(AdvisorySources.RefreshInterval);
        handler.Body = Feed(("php", "Warning: PHP"), ("bind", "Warning: BIND 9, CVE-2026-40001"));
        await service.RefreshDueAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var advisories = await db.Advisories.AsNoTracking().OrderBy(a => a.Title).ToListAsync();
        Assert.Equal(["Warning: BIND 9, CVE-2026-40001", "Warning: GitLab", "Warning: PHP"], advisories.Select(a => a.Title));
        Assert.Equal(["CVE-2026-40001"], advisories[0].Cves);
        var source = await db.AdvisorySources.AsNoTracking().SingleAsync(s => s.Key == AdvisorySources.Ccb);
        Assert.Equal(((DateTime?)null, (string?)null), (source.FailingSince, source.Problem));
        // PostgreSQL stores microseconds.
        Assert.InRange((source.LastSuccessAt!.Value - _fixture.Db.Time.GetUtcNow().UtcDateTime).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
    }

    [Fact]
    public async Task An_advisory_keeps_its_earliest_date_when_the_source_dates_it_again()
    {
        await ResetAsync();
        var handler = new FakeHandler { Body = DatedFeed("Wed, 4 Nov 2026 10:00:00 +0100", ("barracuda", "Warning: Barracuda")) };
        var service = Service(handler);
        await service.RefreshDueAsync(CancellationToken.None);

        // The CCB edits the item and moves its date (seen 2026-11-09): the advisory keeps the first one and does not move up the list.
        _fixture.Db.Time.Advance(AdvisorySources.RefreshInterval);
        handler.Body = DatedFeed("Fri, 13 Nov 2026 13:26:00 +0100", ("barracuda", "Warning: Barracuda, updated"));
        await service.RefreshDueAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var advisory = await db.Advisories.AsNoTracking().SingleAsync();
            Assert.Equal(("Warning: Barracuda, updated", new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc)), (advisory.Title, advisory.PublishedAt));
        }

        // An earlier date is a correction and is taken.
        _fixture.Db.Time.Advance(AdvisorySources.RefreshInterval);
        handler.Body = DatedFeed("Tue, 3 Nov 2026 10:00:00 +0100", ("barracuda", "Warning: Barracuda, updated"));
        await service.RefreshDueAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(new DateTime(2026, 11, 3, 9, 0, 0, DateTimeKind.Utc), (await db.Advisories.AsNoTracking().SingleAsync()).PublishedAt);
        }
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, null, "refused")]
    [InlineData(HttpStatusCode.Found, "https://elsewhere.example/feed.xml", "moved")]
    [InlineData(HttpStatusCode.OK, null, "not an RSS feed")]
    public async Task A_failure_keeps_the_advisories_and_says_why(HttpStatusCode status, string? location, string problem)
    {
        await ResetAsync();
        var handler = new FakeHandler { Body = Feed(("bind", "Warning: BIND")) };
        var service = Service(handler);
        await service.RefreshDueAsync(CancellationToken.None);
        var failedAt = _fixture.Db.Time.GetUtcNow().UtcDateTime + AdvisorySources.RefreshInterval;

        _fixture.Db.Time.Advance(AdvisorySources.RefreshInterval);
        (handler.Status, handler.Location, handler.Body) = (status, location, "<html>Access Denied</html>");
        await service.RefreshDueAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Equal(1, await db.Advisories.CountAsync());
        var source = await db.AdvisorySources.AsNoTracking().SingleAsync(s => s.Key == AdvisorySources.Ccb);
        Assert.Contains(problem, source.Problem);
        Assert.InRange((source.FailingSince!.Value - failedAt).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.True(source.LastSuccessAt < failedAt - TimeSpan.FromMinutes(59));
    }
}
