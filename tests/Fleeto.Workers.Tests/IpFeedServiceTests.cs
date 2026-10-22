using System.Net;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The blocklists in the workers (0.7.0, security monitor step 3): a list that is on is fetched and replaces its entries in one go,
/// a broken or failing fetch keeps the previous list and says why, CrowdSec is asked with Basic Auth at its own address only, and
/// a list switched off is emptied.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class IpFeedServiceTests
{
    private readonly WorkersFixture _fixture;

    public IpFeedServiceTests(WorkersFixture fixture) => _fixture = fixture;

    private sealed class FakeHandler : HttpMessageHandler
    {
        public Dictionary<string, (HttpStatusCode Status, string Body)> Responses { get; } = new(StringComparer.Ordinal);
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var (status, body) = Responses.GetValueOrDefault(request.RequestUri!.ToString(), (HttpStatusCode.NotFound, ""));
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "text/plain") });
        }
    }

    private IpFeedService Service(FakeHandler handler) =>
        new(_fixture.Db.DbFactory, _fixture.Db.DataSource, _fixture.Settings(), _fixture.Db.Bus, _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<IpFeedService>.Instance, handler);

    private async Task ResetAsync(params string[] enabled)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.IpFeedEntries.ExecuteDeleteAsync();
        await db.IpFeeds.ExecuteUpdateAsync(s => s.SetProperty(f => f.Enabled, f => enabled.Contains(f.Key)).SetProperty(f => f.EntryCount, 0)
            .SetProperty(f => f.LastAttemptAt, (DateTime?)null).SetProperty(f => f.LastSuccessAt, (DateTime?)null)
            .SetProperty(f => f.Problem, (string?)null).SetProperty(f => f.RefreshRequestedAt, (DateTime?)null));
        await db.Settings.Where(s => s.Key == SettingKeys.CrowdSecFeed).ExecuteDeleteAsync();
    }

    private static string Lines(int count, int third = 69) =>
        string.Join('\n', Enumerable.Range(0, count).Select(i => $"81.{2 + i / 250}.{third}.{i % 250}"));

    [Fact]
    public async Task A_list_is_fetched_replaced_in_one_go_and_kept_when_the_next_one_looks_broken()
    {
        await ResetAsync(IpFeeds.FeodoTracker);
        var url = IpFeeds.Find(IpFeeds.FeodoTracker)!.Urls[0];
        var handler = new FakeHandler();
        handler.Responses[url] = (HttpStatusCode.OK, "# header\n" + Lines(200) + "\n10.0.0.1\n");
        var service = Service(handler);

        Assert.Equal([IpFeeds.FeodoTracker], await service.RefreshDueAsync(CancellationToken.None));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var feed = await db.IpFeeds.SingleAsync(f => f.Key == IpFeeds.FeodoTracker);
            Assert.Equal((200, (string?)null), (feed.EntryCount, feed.Problem));
            Assert.Equal(200, await db.IpFeedEntries.CountAsync(e => e.Feed == IpFeeds.FeodoTracker));
            Assert.Contains("Fleeto/", handler.Requests[0].Headers.UserAgent.ToString());
        }

        // Not due again for six hours.
        Assert.Empty(await service.RefreshDueAsync(CancellationToken.None));

        // Six hours later the source sends half a list: refused, the 200 stay.
        _fixture.Db.Time.Advance(IpFeeds.RefreshInterval);
        handler.Responses[url] = (HttpStatusCode.OK, Lines(50));
        await service.RefreshDueAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var feed = await db.IpFeeds.SingleAsync(f => f.Key == IpFeeds.FeodoTracker);
            Assert.Contains("shrank", feed.Problem);
            Assert.Equal(200, await db.IpFeedEntries.CountAsync(e => e.Feed == IpFeeds.FeodoTracker));
        }

        // An admin switches it off: emptied.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.IpFeeds.Where(f => f.Key == IpFeeds.FeodoTracker).ExecuteUpdateAsync(s => s.SetProperty(f => f.Enabled, false));
        }

        await service.RefreshDueAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(0, await db.IpFeedEntries.CountAsync(e => e.Feed == IpFeeds.FeodoTracker));
        }
    }

    [Fact]
    public async Task CrowdSec_is_asked_with_basic_auth_at_its_own_address_and_a_refused_login_is_explained()
    {
        await ResetAsync(IpFeeds.CrowdSec);
        var handler = new FakeHandler();
        var service = Service(handler);
        await service.RefreshDueAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Contains("No CrowdSec integration", (await db.IpFeeds.SingleAsync(f => f.Key == IpFeeds.CrowdSec)).Problem);
        }

        Assert.Empty(handler.Requests);
        await _fixture.Settings().SetAsync(SettingKeys.CrowdSecFeed, new CrowdSecFeedSettings("abc-123", "fleeto", "s3cret"), encrypted: true, userId: null);
        var url = "https://admin.api.crowdsec.net/v1/integrations/abc-123/content";
        handler.Responses[url] = (HttpStatusCode.Unauthorized, "");
        _fixture.Db.Time.Advance(IpFeeds.RetryInterval);
        await service.RefreshDueAsync(CancellationToken.None);

        var request = Assert.Single(handler.Requests);
        Assert.Equal(url, request.RequestUri!.ToString());
        Assert.Equal(("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("fleeto:s3cret"))), (request.Headers.Authorization!.Scheme, request.Headers.Authorization.Parameter));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Contains("refused the credentials", (await db.IpFeeds.SingleAsync(f => f.Key == IpFeeds.CrowdSec)).Problem);
        }

        handler.Responses[url] = (HttpStatusCode.OK, Lines(30, third: 70));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.IpFeeds.Where(f => f.Key == IpFeeds.CrowdSec).ExecuteUpdateAsync(s => s.SetProperty(f => f.RefreshRequestedAt, _fixture.Now));
        }

        await service.RefreshDueAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var feed = await db.IpFeeds.SingleAsync(f => f.Key == IpFeeds.CrowdSec);
            Assert.Equal((30, (string?)null, (DateTime?)null), (feed.EntryCount, feed.Problem, feed.RefreshRequestedAt));
        }
    }
}
