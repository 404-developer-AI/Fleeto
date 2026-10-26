using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Advisories;
using Fleeto.Infrastructure.Data;
using Fleeto.Workers.Common;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Security;

/// <summary>
/// Fetches the security advisories of every enabled source every hour (0.8.0 step 0, VULNERABILITIES.md §2). Only the workers reach
/// the internet, as for every external product, and only the feed itself: Fleeto never reads a page a source protects against
/// clients that are not a browser. A new advisory is added, a known one updated when its source changed it; an advisory the feed
/// drops stays until its retention ends, because a feed holds only its latest items. A failed fetch keeps what was fetched before
/// and states why; after 48 hours it is an instance health problem (<see cref="AdvisoryRules.HealthFindings"/>).
/// </summary>
public sealed class AdvisoryFeedService : WorkerLoop
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly HttpClient _http;
    private readonly Dictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);

    public AdvisoryFeedService(IFleetoDbContextFactory dbFactory, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<AdvisoryFeedService> logger,
        HttpMessageHandler? handler = null)
        : base("advisories", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = false })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        // A User-Agent that names Fleeto, so the source can see who fetches its feed.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Fleeto/{FleetoVersion.Current.Split('+')[0]}");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/xml;q=0.9, text/xml;q=0.8");
    }

    protected override TimeSpan Interval => CheckInterval;

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await RefreshDueAsync(cancellationToken);
        return false;
    }

    /// <summary>Fetches every enabled source that is due. Returns the keys it fetched.</summary>
    public async Task<IReadOnlyList<string>> RefreshDueAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        List<AdvisorySource> sources;
        await using (var db = _dbFactory.CreateSystem())
        {
            sources = await db.AdvisorySources.AsNoTracking().Where(s => s.Enabled).ToListAsync(cancellationToken);
        }

        var fetched = new List<string>();
        foreach (var source in sources.Where(s => AdvisoryRules.IsDue(s.LastAttemptAt, now)))
        {
            if (await RefreshAsync(source, cancellationToken))
            {
                fetched.Add(source.Key);
            }
        }

        return fetched;
    }

    private async Task<bool> RefreshAsync(AdvisorySource source, CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var info = AdvisorySources.Find(source.Key);
        if (info is null)
        {
            await WriteStateAsync(source.Key, now, "Fleeto does not know this source any more.", cancellationToken);
            return false;
        }

        var breaker = _breakers.TryGetValue(source.Key, out var known) ? known : _breakers[source.Key] = new CircuitBreaker(3, TimeSpan.FromHours(3), Time);
        if (breaker.IsOpen)
        {
            return false;
        }

        try
        {
            var parsed = AdvisoryFeedParser.Parse(info, await DownloadAsync(info.FeedUrl, cancellationToken));
            var (added, updated) = await SaveAsync(info, parsed.Advisories, now, cancellationToken);
            breaker.RecordSuccess();
            if (added > 0 || parsed.Skipped > 0)
            {
                Logger.LogInformation("The advisories of {Source}: {Added} new, {Updated} changed, {Skipped} items left out", info.Key, added, updated,
                    parsed.Skipped);
            }

            return true;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            breaker.RecordFailure();
            var problem = ex switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Forbidden } =>
                    $"{info.Name} refused the request (403). Fleeto tries again every hour; the advisories fetched before stay.",
                HttpRequestException { StatusCode: { } status } => $"{info.Name} answered {(int)status}. Fleeto tries again every hour; the advisories fetched before stay.",
                InvalidDataException => ex.Message,
                _ => $"{info.Name} could not be reached in time. Fleeto tries again every hour; the advisories fetched before stay."
            };
            await WriteStateAsync(source.Key, now, problem, cancellationToken);
            Logger.LogWarning("The advisories of {Source} could not be fetched: {Message}", info.Key, ex.Message);
            return false;
        }
    }

    private async Task<string> DownloadAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            // A feed that moves is followed by a release of Fleeto, not by itself: a redirect could lead anywhere.
            throw new InvalidDataException("The feed moved to another address. The advisories fetched before stay; a later version of Fleeto follows it.");
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > AdvisorySources.MaxDownloadBytes)
        {
            throw new InvalidDataException($"The feed is larger than {AdvisorySources.MaxDownloadBytes / 1024 / 1024} MB. The advisories fetched before stay.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > AdvisorySources.MaxDownloadBytes)
            {
                throw new InvalidDataException($"The feed is larger than {AdvisorySources.MaxDownloadBytes / 1024 / 1024} MB. The advisories fetched before stay.");
            }

            buffer.Write(chunk, 0, read);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    /// <summary>Adds the new advisories and updates the changed ones, and marks the source fetched, in one save.</summary>
    private async Task<(int Added, int Updated)> SaveAsync(AdvisorySourceInfo info, IReadOnlyList<ParsedAdvisory> advisories, DateTime now,
        CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var ids = advisories.Select(a => a.SourceId).ToList();
        var existing = await db.Advisories.Where(a => a.Source == info.Key && ids.Contains(a.SourceId)).ToDictionaryAsync(a => a.SourceId, StringComparer.Ordinal,
            cancellationToken);
        var added = 0;
        var updated = 0;
        foreach (var parsed in advisories)
        {
            if (existing.TryGetValue(parsed.SourceId, out var advisory))
            {
                var cves = parsed.Cves.ToArray();
                if (advisory.Title != parsed.Title || advisory.Summary != parsed.Summary || advisory.PublishedAt != parsed.PublishedAt ||
                    advisory.Link != parsed.Link || !advisory.Cves.SequenceEqual(cves))
                {
                    advisory.Title = parsed.Title;
                    advisory.Summary = parsed.Summary;
                    advisory.PublishedAt = parsed.PublishedAt;
                    advisory.Link = parsed.Link;
                    advisory.Cves = cves;
                    updated++;
                }

                continue;
            }

            db.Advisories.Add(new Advisory
            {
                Id = Guid.NewGuid(),
                Source = info.Key,
                SourceId = parsed.SourceId,
                Language = info.Language,
                Title = parsed.Title,
                Summary = parsed.Summary,
                PublishedAt = parsed.PublishedAt,
                Cves = parsed.Cves.ToArray(),
                Link = parsed.Link,
                FetchedAt = now
            });
            added++;
        }

        var state = await db.AdvisorySources.SingleAsync(s => s.Key == info.Key, cancellationToken);
        state.LastAttemptAt = now;
        state.LastSuccessAt = now;
        state.FailingSince = null;
        state.Problem = null;
        await db.SaveChangesAsync(cancellationToken);
        return (added, updated);
    }

    private async Task WriteStateAsync(string key, DateTime now, string problem, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        await db.AdvisorySources.Where(s => s.Key == key).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.LastAttemptAt, now)
            .SetProperty(x => x.FailingSince, x => x.FailingSince ?? now)
            .SetProperty(x => x.Problem, problem.Length > 1000 ? problem[..1000] : problem), cancellationToken);
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
