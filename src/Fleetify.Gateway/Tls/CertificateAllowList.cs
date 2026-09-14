using System.Security.Cryptography.X509Certificates;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Security;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Fleetify.Gateway.Tls;

public enum AllowListDecision
{
    Accepted,
    /// <summary>The list has never been loaded: every agent connection is refused until it is.</summary>
    NotLoaded,
    Refused
}

/// <summary>The identity of an accepted agent certificate.</summary>
public sealed record AgentIdentity(Guid EndpointId, string Fingerprint, string PublicKeyFingerprint, DateTime ExpiresAt);

/// <summary>
/// The set of agent certificates the gateway accepts: issued, not revoked, not expired, endpoint not deleted
/// (certificates cascade away with their endpoint). An allow list rather than a deny list, so a certificate the
/// signer never recorded can never connect (ARCHITECTURE.md §4, Endpoint removal and agent revocation).
/// <para>
/// Loaded at start, reloaded every minute, and at once on <see cref="NotificationChannels.Revocations"/> and after a
/// listener reconnect. Also holds the non-retired instance CA certificates used for chain validation.
/// </para>
/// </summary>
public sealed class CertificateAllowList : BackgroundService
{
    private static readonly TimeSpan ReloadInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(5);

    private readonly NpgsqlDataSource _dataSource;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;
    private readonly ILogger<CertificateAllowList> _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);

    // Certificates issued through this gateway since the last reload (enrollment, renewal), so an agent can connect
    // right after receiving its certificate instead of waiting up to a minute for the next reload.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (Entry Entry, long AddedAt)> _recent = new(StringComparer.Ordinal);
    private volatile Snapshot? _snapshot;

    public CertificateAllowList(NpgsqlDataSource dataSource, INotificationBus bus, TimeProvider time, ILogger<CertificateAllowList> logger)
    {
        _dataSource = dataSource;
        _bus = bus;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Raised after every successful reload. The argument is the endpoint named by a revocation, or null for a
    /// periodic or resync reload (every live session must be re-checked).
    /// </summary>
    public event Action<Guid?>? Reloaded;

    public bool IsLoaded => _snapshot is not null;

    public DateTime? LoadedAt => _snapshot?.LoadedAt;

    public int Count => _snapshot?.Entries.Count ?? 0;

    /// <summary>Non-retired CA certificates; empty until loaded. Replaced, never mutated, on reload.</summary>
    public X509Certificate2Collection CaCertificates => _snapshot?.CaCertificates ?? [];

    /// <summary>Changes whenever the CA set changes, so TLS options can be rebuilt.</summary>
    public string CaSetKey => _snapshot?.CaSetKey ?? string.Empty;

    /// <summary>
    /// Decides whether a client certificate may open a session: SHA-256 fingerprint on the list, not expired, chains to
    /// an instance CA, and the endpoint id in its SAN equals the endpoint the certificate was issued to.
    /// </summary>
    public AllowListDecision Authorize(X509Certificate2? certificate, out AgentIdentity? identity)
    {
        identity = null;
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return AllowListDecision.NotLoaded;
        }

        if (certificate is null)
        {
            return AllowListDecision.Refused;
        }

        var now = _time.GetUtcNow().UtcDateTime;
        var fingerprint = KeyIds.Sha256Hex(certificate.RawDataMemory.Span);
        if (!TryGetEntry(snapshot, fingerprint, out var entry) || entry.ExpiresAt <= now ||
            certificate.NotAfter.ToUniversalTime() <= now)
        {
            return AllowListDecision.Refused;
        }

        if (InternalCertificateAuthority.EndpointIdFromCertificate(certificate) != entry.EndpointId)
        {
            return AllowListDecision.Refused;
        }

        var issuers = CertificateChains.Build(certificate, snapshot.CaCertificates, CertificateChains.ClientAuthOid, now);
        if (issuers is null)
        {
            return AllowListDecision.Refused;
        }

        foreach (var issuer in issuers)
        {
            issuer.Dispose();
        }

        identity = new AgentIdentity(entry.EndpointId, fingerprint, InternalCertificateAuthority.PublicKeyFingerprint(certificate),
            entry.ExpiresAt < certificate.NotAfter.ToUniversalTime() ? entry.ExpiresAt : certificate.NotAfter.ToUniversalTime());
        return AllowListDecision.Accepted;
    }

    /// <summary>
    /// Re-check for a live session. Returns true while the list has not been loaded, so a database outage never drops
    /// sessions that were valid when they connected.
    /// </summary>
    public bool IsStillAllowed(string fingerprint, Guid endpointId)
    {
        var snapshot = _snapshot;
        if (snapshot is null)
        {
            return true;
        }

        return TryGetEntry(snapshot, fingerprint, out var entry) && entry.EndpointId == endpointId &&
               entry.ExpiresAt > _time.GetUtcNow().UtcDateTime;
    }

    /// <summary>
    /// Adds a certificate fleetify-signer just issued and returned through this gateway (read from the signing request
    /// row, never from the agent). The next reload replaces it with the database state, so a revocation still wins.
    /// </summary>
    public void AddIssued(byte[] certificateDer, Guid endpointId)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
        if (InternalCertificateAuthority.EndpointIdFromCertificate(certificate) != endpointId)
        {
            return;
        }

        var fingerprint = KeyIds.Sha256Hex(certificateDer);
        _recent[fingerprint] = (new Entry(endpointId, certificate.NotAfter.ToUniversalTime()), System.Diagnostics.Stopwatch.GetTimestamp());
    }

    private bool TryGetEntry(Snapshot snapshot, string fingerprint, out Entry entry)
    {
        if (snapshot.Entries.TryGetValue(fingerprint, out entry))
        {
            return true;
        }

        if (_recent.TryGetValue(fingerprint, out var recent))
        {
            entry = recent.Entry;
            return true;
        }

        return false;
    }

    /// <summary>Loads the list from the database. Returns false (and keeps the previous list) on failure.</summary>
    public async Task<bool> ReloadAsync(CancellationToken cancellationToken, Guid? endpointHint = null)
    {
        await _reloadLock.WaitAsync(cancellationToken);
        try
        {
            var now = _time.GetUtcNow().UtcDateTime;
            var startedAt = System.Diagnostics.Stopwatch.GetTimestamp();
            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
            await using var batch = new NpgsqlBatch(connection)
            {
                BatchCommands =
                {
                    new NpgsqlBatchCommand("""
                        SELECT "Fingerprint", "EndpointId", "ExpiresAt" FROM "AgentCertificates"
                        WHERE "RevokedAt" IS NULL AND "ExpiresAt" > $1
                        """)
                    {
                        Parameters = { new NpgsqlParameter<DateTime> { TypedValue = now } }
                    },
                    new NpgsqlBatchCommand("""
                        SELECT "Fingerprint", "CertificateDer" FROM "CertificateAuthorities"
                        WHERE "RetiredAt" IS NULL ORDER BY "CreatedAt"
                        """)
                }
            };

            var previous = _snapshot;
            var entries = new Dictionary<string, Entry>(Math.Max(16, previous?.Entries.Count ?? 0), StringComparer.Ordinal);
            var cas = new List<(string Fingerprint, byte[] Der)>();
            await using (var reader = await batch.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    entries[reader.GetString(0)] = new Entry(reader.GetGuid(1), DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
                }

                await reader.NextResultAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                {
                    cas.Add((reader.GetString(0), reader.GetFieldValue<byte[]>(1)));
                }
            }

            var caSetKey = string.Join(",", cas.Select(c => c.Fingerprint));
            var caCertificates = previous is not null && previous.CaSetKey == caSetKey
                ? previous.CaCertificates
                : LoadCas(cas);

            _snapshot = new Snapshot(entries, caCertificates, caSetKey, now);

            // Additions made before this reload read the database are now covered (or revoked) by the snapshot.
            foreach (var (fingerprint, recent) in _recent)
            {
                if (recent.AddedAt < startedAt)
                {
                    _recent.TryRemove(KeyValuePair.Create(fingerprint, recent));
                }
            }

            if (previous is null)
            {
                _logger.LogInformation("Agent certificate allow list loaded: {Count} certificates, {CaCount} CA certificates",
                    entries.Count, caCertificates.Count);
            }

            RaiseReloaded(endpointHint);
            return true;
        }
        catch (Exception ex) when (ex is NpgsqlException or TimeoutException or InvalidOperationException)
        {
            _logger.LogWarning(ex, "Could not reload the agent certificate allow list; keeping the previous list");
            return false;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var subscription = _bus.Subscribe(NotificationChannels.Revocations, async (payload, token) =>
        {
            Guid? hint = Guid.TryParse(payload, out var endpointId) ? endpointId : null;
            _logger.LogInformation(hint is null
                ? "Reloading the allow list after a notification listener reconnect"
                : "Reloading the allow list after a revocation for endpoint {EndpointId}", hint);
            await ReloadAsync(token, hint);
        });

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

    private void RaiseReloaded(Guid? endpointHint)
    {
        try
        {
            Reloaded?.Invoke(endpointHint);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Allow list reload handler failed");
        }
    }

    private static X509Certificate2Collection LoadCas(List<(string Fingerprint, byte[] Der)> cas)
    {
        var collection = new X509Certificate2Collection();
        foreach (var (_, der) in cas)
        {
            collection.Add(X509CertificateLoader.LoadCertificate(der));
        }

        return collection;
    }

    private readonly record struct Entry(Guid EndpointId, DateTime ExpiresAt);

    private sealed record Snapshot(Dictionary<string, Entry> Entries, X509Certificate2Collection CaCertificates, string CaSetKey, DateTime LoadedAt);
}
