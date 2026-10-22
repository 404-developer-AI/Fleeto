using System.Net;
using System.Net.Sockets;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Fleeto.Infrastructure.Security;

/// <summary>What the security monitor decided about one address.</summary>
public enum IpVerdict
{
    /// <summary>Neither blocked nor allowed.</summary>
    None,
    /// <summary>On the allowlist: never blocked, whatever else lists it.</summary>
    Allowed,
    /// <summary>On a blocklist an admin switched on (step 3). Not applied to an agent that presents a valid certificate.</summary>
    BlockedFeed,
    /// <summary>Blocked by the security monitor. Not applied to an agent that presents a valid certificate.</summary>
    BlockedAutomatic,
    /// <summary>Blocked by an admin. Applied everywhere, the agent port included.</summary>
    BlockedManual
}

/// <summary>
/// The blocked and allowed addresses of the instance, in memory, for web and the gateway (0.7.0, security monitor). Loaded at
/// start, reloaded every minute, and at once on <see cref="NotificationChannels.IpBlocks"/> (raised by a database trigger on
/// every change) and after a listener reconnect. A lookup is a binary search over sorted ranges, without a database call. The
/// entries of the blocklists (step 3) are read again only when a list was switched or replaced, since they can be many.
/// <para>
/// Fails open: while the list was never loaded nothing is blocked, because refusing every caller when the database is down would
/// be worse than the attack. Ranges that may never be blocked (<see cref="IpNetworks.Reserved"/>) are left out on load, whatever
/// the table holds, so the containers of the instance and the host proxy can never be cut off.
/// </para>
/// </summary>
public sealed class IpBlockList : BackgroundService
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<IpBlockList> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private volatile Snapshot? _snapshot;
    private string? _feedVersion;
    private List<string> _feedNetworks = [];

    public IpBlockList(NpgsqlDataSource dataSource, INotificationBus bus, TimeProvider time, ILogger<IpBlockList> logger)
    {
        _dataSource = dataSource;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    public bool IsLoaded => _snapshot is not null;

    public int BlockCount => _snapshot?.BlockCount ?? 0;

    public int AllowCount => _snapshot?.AllowCount ?? 0;

    public int FeedCount => _snapshot?.FeedCount ?? 0;

    /// <summary>Decides about one address. An allowed address is never blocked; a manual block wins over an automatic one.</summary>
    public IpVerdict Check(IPAddress? address)
    {
        var snapshot = _snapshot;
        if (snapshot is null || address is null)
        {
            return IpVerdict.None;
        }

        address = IpNetworks.Normalize(address);
        var tables = address.AddressFamily switch
        {
            AddressFamily.InterNetwork => snapshot.V4,
            AddressFamily.InterNetworkV6 => snapshot.V6,
            _ => null
        };
        if (tables is null)
        {
            return IpVerdict.None;
        }

        var number = IpNetworks.ToNumber(address);
        if (tables.Allows.Find(number, DateTime.MinValue) is not null)
        {
            return IpVerdict.Allowed;
        }

        return tables.Blocks.Find(number, _time.GetUtcNow().UtcDateTime) switch
        {
            IpBlockSource.Manual => IpVerdict.BlockedManual,
            IpBlockSource.Automatic => IpVerdict.BlockedAutomatic,
            _ => tables.Feeds.Find(number, DateTime.MinValue) is not null ? IpVerdict.BlockedFeed : IpVerdict.None
        };
    }

    /// <summary>Builds the list from rows, as a reload does; for tests and for the load itself.</summary>
    public void Load(IEnumerable<(string Network, IpBlockSource Source, DateTime? ExpiresAt)> blocks, IEnumerable<string> allows,
        IEnumerable<string>? feeds = null)
    {
        var v4 = new TableBuilder();
        var v6 = new TableBuilder();
        foreach (var (text, source, expiresAt) in blocks)
        {
            if (Usable(text) is { } network)
            {
                (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? v4 : v6).AddBlock(network, source, expiresAt);
            }
        }

        foreach (var text in allows)
        {
            if (Usable(text) is { } network)
            {
                (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? v4 : v6).AddAllow(network);
            }
        }

        foreach (var text in feeds ?? [])
        {
            if (Usable(text) is { } network)
            {
                (network.BaseAddress.AddressFamily == AddressFamily.InterNetwork ? v4 : v6).AddFeed(network);
            }
        }

        _snapshot = new Snapshot(v4.Build(), v6.Build(), v4.BlockCount + v6.BlockCount, v4.AllowCount + v6.AllowCount, v4.FeedCount + v6.FeedCount);
    }

    /// <summary>Loads the list from the database. Returns false (and keeps the previous list) on failure.</summary>
    public async Task<bool> ReloadAsync(CancellationToken cancellationToken)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var blocks = new List<(string, IpBlockSource, DateTime?)>();
            var allows = new List<string>();
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using (var command = new NpgsqlCommand("""
                SELECT "Network", "Source", "ExpiresAt" FROM "IpBlocks" WHERE "ExpiresAt" IS NULL OR "ExpiresAt" > $1
                """, connection))
            {
                command.Parameters.Add(new NpgsqlParameter<DateTime> { TypedValue = now });
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    var source = Enum.TryParse<IpBlockSource>(reader.GetString(1), out var parsed) ? parsed : IpBlockSource.Manual;
                    blocks.Add((reader.GetString(0), source, reader.IsDBNull(2) ? null : reader.GetDateTime(2)));
                }
            }

            await using (var command = new NpgsqlCommand("""SELECT "Network" FROM "IpAllows" """, connection))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    allows.Add(reader.GetString(0));
                }
            }

            // Blocklists: their entries only when a list was switched or replaced since the last reload.
            var versions = new List<string>();
            await using (var command = new NpgsqlCommand("""SELECT "Key", "Version" FROM "IpFeeds" WHERE "Enabled" ORDER BY "Key" """, connection))
            {
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    versions.Add($"{reader.GetString(0)}:{reader.GetInt64(1)}");
                }
            }

            var version = string.Join(',', versions);
            if (version != _feedVersion)
            {
                var networks = new List<string>();
                await using var command = new NpgsqlCommand("""
                    SELECT e."Network" FROM "IpFeedEntries" e JOIN "IpFeeds" f ON f."Key" = e."Feed" WHERE f."Enabled"
                    """, connection);
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    networks.Add(reader.GetString(0));
                }

                _feedNetworks = networks;
                _feedVersion = version;
            }

            Load(blocks, allows, _feedNetworks);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not reload the blocked and allowed addresses; keeping the previous list");
            return false;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _bus.Subscribe(NotificationChannels.IpBlocks, async (_, token) => await ReloadAsync(token));

        while (!stoppingToken.IsCancellationRequested)
        {
            var loaded = await ReloadAsync(stoppingToken);
            try
            {
                await Task.Delay(loaded || IsLoaded ? ReloadInterval : RetryInterval, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private static IPNetwork? Usable(string text) =>
        IpNetworks.TryParse(text, out var network) && !IpNetworks.TouchesReserved(network) ? network : null;

    private sealed record Snapshot(FamilyTables V4, FamilyTables V6, int BlockCount, int AllowCount, int FeedCount);

    private sealed record FamilyTables(RangeTable Allows, RangeTable Blocks, RangeTable Feeds);

    private readonly record struct Entry(UInt128 First, UInt128 Last, IpBlockSource Source, DateTime? ExpiresAt);

    /// <summary>
    /// Ranges sorted by their first address, with the highest last address seen so far at every position, so a lookup finds the
    /// last range that starts at or before the address and walks back only while an earlier range can still reach it.
    /// </summary>
    private sealed class RangeTable
    {
        private readonly Entry[] _entries;
        private readonly UInt128[] _maxLast;

        public RangeTable(List<Entry> entries)
        {
            entries.Sort((a, b) => a.First.CompareTo(b.First));
            _entries = [.. entries];
            _maxLast = new UInt128[_entries.Length];
            var max = UInt128.Zero;
            for (var i = 0; i < _entries.Length; i++)
            {
                max = i == 0 || _entries[i].Last > max ? _entries[i].Last : max;
                _maxLast[i] = max;
            }
        }

        /// <summary>The source of the strongest range holding <paramref name="number"/> that has not expired at <paramref name="now"/>.</summary>
        public IpBlockSource? Find(UInt128 number, DateTime now)
        {
            int low = 0, high = _entries.Length - 1, last = -1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (_entries[middle].First <= number)
                {
                    last = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            IpBlockSource? found = null;
            for (var i = last; i >= 0 && _maxLast[i] >= number; i--)
            {
                var entry = _entries[i];
                if (entry.Last < number || entry.ExpiresAt is { } expiresAt && expiresAt <= now)
                {
                    continue;
                }

                if (entry.Source == IpBlockSource.Manual)
                {
                    return IpBlockSource.Manual;
                }

                found = entry.Source;
            }

            return found;
        }
    }

    private sealed class TableBuilder
    {
        private readonly List<Entry> _allows = [];
        private readonly List<Entry> _blocks = [];
        private readonly List<Entry> _feeds = [];

        public int BlockCount => _blocks.Count;

        public int FeedCount => _feeds.Count;

        public void AddFeed(IPNetwork network)
        {
            var (first, last) = IpNetworks.Range(network);
            _feeds.Add(new Entry(first, last, IpBlockSource.Automatic, null));
        }

        public int AllowCount => _allows.Count;

        public void AddAllow(IPNetwork network)
        {
            var (first, last) = IpNetworks.Range(network);
            _allows.Add(new Entry(first, last, IpBlockSource.Manual, null));
        }

        public void AddBlock(IPNetwork network, IpBlockSource source, DateTime? expiresAt)
        {
            var (first, last) = IpNetworks.Range(network);
            _blocks.Add(new Entry(first, last, source, expiresAt));
        }

        public FamilyTables Build() => new(new RangeTable(_allows), new RangeTable(_blocks), new RangeTable(_feeds));
    }
}
