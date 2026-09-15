using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Core.Entities;
using Fleeto.Gateway.Signing;
using Fleeto.Infrastructure.Security;
using Npgsql;

namespace Fleeto.Gateway.Tls;

/// <summary>The gateway server certificate in use, with its CA chain for the TLS handshake.</summary>
public sealed class ServerCertificate : IDisposable
{
    internal ServerCertificate(X509Certificate2 certificate, ECDsa key, SslStreamCertificateContext context, DateTime notAfter, DateTime obtainedAt)
    {
        Certificate = certificate;
        Key = key;
        Context = context;
        NotAfter = notAfter;
        ObtainedAt = obtainedAt;
    }

    public X509Certificate2 Certificate { get; }
    public SslStreamCertificateContext Context { get; }
    public DateTime NotAfter { get; }
    public DateTime ObtainedAt { get; }
    private ECDsa Key { get; }

    public void Dispose()
    {
        Certificate.Dispose();
        Key.Dispose();
    }
}

/// <summary>
/// Obtains the gateway server certificate from fleeto-signer. The private key is generated in memory at start and
/// never stored (ARCHITECTURE.md §5, Agent identity and revocation). The certificate lives 24 hours and is renewed
/// every 12 hours; when a renewal fails the gateway retries with backoff and keeps serving the current certificate
/// until it expires. Without a certificate the agent port refuses every TLS handshake.
/// </summary>
public sealed class GatewayCertificateProvider : BackgroundService
{
    private static readonly TimeSpan RenewAfter = TimeSpan.FromHours(12);
    private static readonly TimeSpan MinRetry = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetry = TimeSpan.FromMinutes(5);

    /// <summary>How long a replaced certificate stays alive for handshakes that already picked it up.</summary>
    private static readonly TimeSpan RetireDelay = TimeSpan.FromMinutes(2);

    private readonly SigningRequestClient _signing;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly ILogger<GatewayCertificateProvider> _logger;
    private volatile ServerCertificate? _current;
    private volatile string _status = "Requesting a gateway certificate from fleeto-signer.";

    public GatewayCertificateProvider(SigningRequestClient signing, NpgsqlDataSource dataSource, TimeProvider time,
        ILogger<GatewayCertificateProvider> logger)
    {
        _signing = signing;
        _dataSource = dataSource;
        _time = time;
        _logger = logger;
    }

    /// <summary>The certificate to present, or null when none is valid.</summary>
    public ServerCertificate? Current
    {
        get
        {
            var current = _current;
            return current is not null && current.NotAfter > _time.GetUtcNow().UtcDateTime ? current : null;
        }
    }

    /// <summary>Human-readable state for the health endpoint. Never contains key material.</summary>
    public string Status => _status;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var retry = MinRetry;
        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;
            try
            {
                if (await ObtainAsync(stoppingToken))
                {
                    retry = MinRetry;
                    wait = RenewAfter;
                }
                else
                {
                    wait = retry;
                    retry = TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, retry.Ticks * 2));
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Requesting the gateway certificate failed; retrying in {Delay}", retry);
                SetStatus($"The gateway certificate request failed ({ex.GetType().Name}). Retrying in {retry.TotalSeconds:0} seconds.");
                wait = retry;
                retry = TimeSpan.FromTicks(Math.Min(MaxRetry.Ticks, retry.Ticks * 2));
            }

            // Never sleep past the expiry of the certificate in use.
            var current = _current;
            if (current is not null)
            {
                var untilRefresh = current.NotAfter - _time.GetUtcNow().UtcDateTime - TimeSpan.FromMinutes(30);
                if (untilRefresh < wait)
                {
                    wait = untilRefresh > MinRetry ? untilRefresh : wait;
                }
            }

            try
            {
                await Task.Delay(wait, _time, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Requests and installs a new certificate. Returns false when the signer did not deliver one.</summary>
    internal async Task<bool> ObtainAsync(CancellationToken cancellationToken)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            var request = new CertificateRequest("CN=fleeto-gateway, O=Fleeto", key, HashAlgorithmName.SHA256);
            var csr = request.CreateSigningRequest();

            var outcome = await _signing.RequestAsync(SigningRequestKind.GatewayCertificate, null, null, csr, "gateway", cancellationToken);
            switch (outcome.State)
            {
                case SigningOutcomeState.TimedOut:
                    return Fail("fleeto-signer did not answer the gateway certificate request in time. Check that fleeto-signer is running.");
                case SigningOutcomeState.Refused:
                    return Fail($"fleeto-signer refused the gateway certificate request: {outcome.RefusalReason}");
                case SigningOutcomeState.Failed or SigningOutcomeState.Completed when outcome.Result is null:
                    return Fail("fleeto-signer could not issue a gateway certificate. Check the fleeto-signer log.");
            }

            var installed = await InstallAsync(outcome.Result!, key, cancellationToken);
            if (installed)
            {
                key = null; // Owned by the installed certificate now.
            }

            return installed;
        }
        finally
        {
            key?.Dispose();
        }
    }

    private async Task<bool> InstallAsync(byte[] certificateDer, ECDsa key, CancellationToken cancellationToken)
    {
        using var issued = X509CertificateLoader.LoadCertificate(certificateDer);
        var now = _time.GetUtcNow().UtcDateTime;

        var ourKey = KeyIds.Sha256Hex(key.ExportSubjectPublicKeyInfo());
        if (!SecureCompare.HexEquals(InternalCertificateAuthority.PublicKeyFingerprint(issued), ourKey))
        {
            return Fail("The certificate from fleeto-signer does not match the gateway key. It was not installed.");
        }

        var cas = await LoadCasAsync(cancellationToken);
        var chain = CertificateChains.Build(issued, cas, CertificateChains.ServerAuthOid, now);
        foreach (var ca in cas)
        {
            ca.Dispose();
        }

        if (chain is null)
        {
            return Fail("The certificate from fleeto-signer does not chain to an instance CA. It was not installed.");
        }

        var withKey = issued.CopyWithPrivateKey(key);
        if (OperatingSystem.IsWindows())
        {
            // Windows SChannel cannot use an ephemeral (never persisted) key for a server certificate, so on Windows
            // (local development only) the key is round-tripped through PKCS#12 into a temporary key container that
            // is deleted when the certificate is disposed. Linux (the container) uses the in-memory key directly.
            var ephemeral = withKey;
            withKey = X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null, X509KeyStorageFlags.DefaultKeySet);
            ephemeral.Dispose();
        }

        var context = SslStreamCertificateContext.Create(withKey, chain, offline: true);
        var notAfter = withKey.NotAfter.ToUniversalTime();
        var replacement = new ServerCertificate(withKey, key, context, notAfter, now);
        var previous = _current;
        _current = replacement;
        SetStatus($"Gateway certificate valid until {notAfter:yyyy-MM-dd HH:mm} UTC.");
        _logger.LogInformation("Gateway certificate installed, valid until {NotAfter:O}", notAfter);

        if (previous is not null)
        {
            _ = Task.Delay(RetireDelay, _time).ContinueWith(_ => previous.Dispose(), TaskScheduler.Default);
        }

        return true;
    }

    private async Task<X509Certificate2Collection> LoadCasAsync(CancellationToken cancellationToken)
    {
        var collection = new X509Certificate2Collection();
        await using var command = _dataSource.CreateCommand("""
            SELECT "CertificateDer" FROM "CertificateAuthorities" WHERE "RetiredAt" IS NULL
            """);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            collection.Add(X509CertificateLoader.LoadCertificate(reader.GetFieldValue<byte[]>(0)));
        }

        return collection;
    }

    private bool Fail(string status)
    {
        var current = Current;
        SetStatus(current is null
            ? status
            : $"{status} The current certificate stays in use until {current.NotAfter:yyyy-MM-dd HH:mm} UTC.");
        _logger.LogWarning("Gateway certificate not renewed: {Status}", status);
        return false;
    }

    private void SetStatus(string status) => _status = status;

    public override void Dispose()
    {
        _current?.Dispose();
        base.Dispose();
    }
}
