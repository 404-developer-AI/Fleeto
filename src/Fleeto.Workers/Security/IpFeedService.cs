using System.Net.Http.Headers;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Common;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Fleeto.Workers.Security;

/// <summary>
/// Fetches the blocklists an admin switched on (0.7.0, security monitor step 3, ARCHITECTURE.md §5): every 6 hours, an hour after
/// a failure, and at once when an admin asks. Only the workers reach the internet, as for every external product. A list is
/// cleaned of what may never be blocked (<see cref="IpFeedParser"/>), refused when it looks broken (too large, or shrunk to less
/// than half), and otherwise replaces the one before in one transaction, after which web and the gateway load it. A failed or
/// refused fetch keeps the previous list and states why on Settings, Security. A switched-off list is emptied.
/// </summary>
public sealed class IpFeedService : WorkerLoop
{
    /// <summary>The largest list Fleeto downloads.</summary>
    public const long MaxDownloadBytes = 64L * 1024 * 1024;

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly NpgsqlDataSource _dataSource;
    private readonly SettingsStore _settings;
    private readonly INotificationBus _bus;
    private readonly HttpClient _http;
    private readonly Dictionary<string, CircuitBreaker> _breakers = new(StringComparer.Ordinal);
    private IDisposable? _subscription;

    public IpFeedService(IFleetoDbContextFactory dbFactory, NpgsqlDataSource dataSource, SettingsStore settings, INotificationBus bus,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<IpFeedService> logger, HttpMessageHandler? handler = null)
        : base("ip-feeds", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _dataSource = dataSource;
        _settings = settings;
        _bus = bus;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), AllowAutoRedirect = true, MaxAutomaticRedirections = 3 })
        {
            Timeout = TimeSpan.FromSeconds(60)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"Fleeto/{FleetoVersion.Current.Split('+')[0]}");
    }

    protected override TimeSpan Interval => CheckInterval;

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.IpFeeds, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await RefreshDueAsync(cancellationToken);
        return false;
    }

    /// <summary>Fetches every list that is due, and empties the ones switched off. Returns the keys it fetched.</summary>
    public async Task<IReadOnlyList<string>> RefreshDueAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        List<IpFeed> feeds;
        await using (var db = _dbFactory.CreateSystem())
        {
            feeds = await db.IpFeeds.AsNoTracking().ToListAsync(cancellationToken);
            foreach (var off in feeds.Where(f => !f.Enabled && f.EntryCount > 0))
            {
                await db.IpFeedEntries.Where(e => e.Feed == off.Key).ExecuteDeleteAsync(cancellationToken);
                await db.IpFeeds.Where(f => f.Key == off.Key).ExecuteUpdateAsync(s => s.SetProperty(f => f.EntryCount, 0), cancellationToken);
            }
        }

        var fetched = new List<string>();
        foreach (var feed in feeds.Where(f => f.Enabled && IsDue(f, now)))
        {
            await RefreshAsync(feed, cancellationToken);
            fetched.Add(feed.Key);
        }

        return fetched;
    }

    internal static bool IsDue(IpFeed feed, DateTime now) =>
        feed.RefreshRequestedAt is not null || feed.LastAttemptAt is null ||
        now - feed.LastAttemptAt >= (feed.Problem is null ? IpFeeds.RefreshInterval : IpFeeds.RetryInterval);

    private async Task RefreshAsync(IpFeed feed, CancellationToken cancellationToken)
    {
        var info = IpFeeds.Find(feed.Key);
        var now = Time.GetUtcNow().UtcDateTime;
        if (info is null)
        {
            await WriteStateAsync(feed.Key, now, success: false, "Fleeto does not know this list any more. Switch it off.", cancellationToken);
            return;
        }

        var breaker = _breakers.TryGetValue(feed.Key, out var known) ? known : _breakers[feed.Key] = new CircuitBreaker(3, TimeSpan.FromHours(6), Time);
        if (breaker.IsOpen && feed.RefreshRequestedAt is null)
        {
            return;
        }

        try
        {
            var requests = await RequestsAsync(info, cancellationToken);
            if (requests is null)
            {
                await WriteStateAsync(feed.Key, now, success: false,
                    "No CrowdSec integration is set. Create a firewall integration in the CrowdSec Console and enter it in Settings, Security.",
                    cancellationToken);
                return;
            }

            var text = new StringBuilder();
            foreach (var request in requests)
            {
                text.Append(await DownloadAsync(request, cancellationToken)).Append('\n');
            }

            var parsed = IpFeedParser.Parse(info.Format, text.ToString());
            if (IpFeedParser.Refusal(feed.LastSuccessAt is null ? 0 : feed.EntryCount, parsed.Networks.Count) is { } refusal)
            {
                breaker.RecordFailure();
                await WriteStateAsync(feed.Key, now, success: false, refusal, cancellationToken);
                Logger.LogWarning("The blocklist {Feed} was refused: {Reason}", feed.Key, refusal);
                return;
            }

            await ReplaceAsync(feed.Key, parsed.Networks, now, cancellationToken);
            breaker.RecordSuccess();
            Logger.LogInformation("The blocklist {Feed} holds {Count} networks ({Skipped} lines left out)", feed.Key, parsed.Networks.Count, parsed.Skipped);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            breaker.RecordFailure();
            var problem = ex switch
            {
                HttpRequestException { StatusCode: System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden } =>
                    "The list refused the credentials. For CrowdSec, regenerate the credentials of the integration in the Console and enter them again.",
                HttpRequestException { StatusCode: { } status } => $"The list answered {(int)status}. Fleeto tries again in an hour; the previous list stays in use.",
                InvalidDataException => ex.Message,
                _ => "The list could not be reached in time. Fleeto tries again in an hour; the previous list stays in use."
            };
            await WriteStateAsync(feed.Key, now, success: false, problem, cancellationToken);
            Logger.LogWarning("The blocklist {Feed} could not be fetched: {Message}", feed.Key, ex.Message);
        }
    }

    /// <summary>The requests of a list, or null for CrowdSec without an integration.</summary>
    private async Task<IReadOnlyList<HttpRequestMessage>?> RequestsAsync(IpFeedInfo info, CancellationToken cancellationToken)
    {
        if (info.Key != IpFeeds.CrowdSec)
        {
            return info.Urls.Select(u => new HttpRequestMessage(HttpMethod.Get, u)).ToList();
        }

        var crowdSec = await _settings.GetAsync<CrowdSecFeedSettings>(SettingKeys.CrowdSecFeed, cancellationToken);
        if (crowdSec is null)
        {
            return null;
        }

        var request = new HttpRequestMessage(HttpMethod.Get, string.Format(System.Globalization.CultureInfo.InvariantCulture, IpFeeds.CrowdSecContentUrl,
            Uri.EscapeDataString(crowdSec.IntegrationId)));
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{crowdSec.Username}:{crowdSec.Password}")));
        return [request];
    }

    private async Task<string> DownloadAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using (request)
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxDownloadBytes)
            {
                throw new InvalidDataException($"The list is larger than {MaxDownloadBytes / 1024 / 1024} MB. The previous list stays in use.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxDownloadBytes)
                {
                    throw new InvalidDataException($"The list is larger than {MaxDownloadBytes / 1024 / 1024} MB. The previous list stays in use.");
                }

                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }
    }

    /// <summary>Replaces the entries of a list and raises its version, in one transaction.</summary>
    private async Task ReplaceAsync(string key, IReadOnlyList<string> networks, DateTime now, CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var delete = new NpgsqlCommand("""DELETE FROM "IpFeedEntries" WHERE "Feed" = $1""", connection, transaction))
        {
            delete.Parameters.Add(new NpgsqlParameter<string> { TypedValue = key });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var writer = await connection.BeginBinaryImportAsync("""COPY "IpFeedEntries" ("Feed", "Network") FROM STDIN (FORMAT BINARY)""", cancellationToken))
        {
            foreach (var network in networks)
            {
                await writer.StartRowAsync(cancellationToken);
                await writer.WriteAsync(key, NpgsqlDbType.Varchar, cancellationToken);
                await writer.WriteAsync(network, NpgsqlDbType.Varchar, cancellationToken);
            }

            await writer.CompleteAsync(cancellationToken);
        }

        await using (var update = new NpgsqlCommand("""
            UPDATE "IpFeeds" SET "LastAttemptAt" = $2, "LastSuccessAt" = $2, "EntryCount" = $3, "Problem" = NULL, "Version" = "Version" + 1,
                                 "RefreshRequestedAt" = NULL
            WHERE "Key" = $1
            """, connection, transaction))
        {
            update.Parameters.Add(new NpgsqlParameter<string> { TypedValue = key });
            update.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now, NpgsqlDbType = NpgsqlDbType.TimestampTz });
            update.Parameters.Add(new NpgsqlParameter<int> { TypedValue = networks.Count });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task WriteStateAsync(string key, DateTime now, bool success, string? problem, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        await db.IpFeeds.Where(f => f.Key == key).ExecuteUpdateAsync(s => s
            .SetProperty(f => f.LastAttemptAt, now)
            .SetProperty(f => f.Problem, success ? null : problem!.Length > 1000 ? problem[..1000] : problem)
            .SetProperty(f => f.RefreshRequestedAt, (DateTime?)null), cancellationToken);
    }

    public override void Dispose()
    {
        _http.Dispose();
        base.Dispose();
    }
}
