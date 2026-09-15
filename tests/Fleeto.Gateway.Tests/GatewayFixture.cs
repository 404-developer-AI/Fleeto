using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Core.Entities;
using Fleeto.Gateway.Data;
using Fleeto.Gateway.Diagnostics;
using Fleeto.Gateway.Releases;
using Fleeto.Gateway.Sessions;
using Fleeto.Gateway.Signing;
using Fleeto.Gateway.Tls;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Fleeto.Gateway.Tests;

[CollectionDefinition(Name)]
public sealed class GatewayCollection : ICollectionFixture<GatewayFixture>
{
    public const string Name = "gateway";
}

/// <summary>One migrated test database with an instance CA for every gateway test.</summary>
public sealed class GatewayFixture : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;

    public InternalCertificateAuthority.CaMaterial Ca { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        Database = await TestDatabase.CreateAsync("gateway");
        var now = Database.Time.GetUtcNow().UtcDateTime;
        Ca = InternalCertificateAuthority.CreateCa(TestDatabase.Fqdn, now);
        await using var db = Database.DbFactory.CreateSystem();
        db.CertificateAuthorities.Add(new CertificateAuthority
        {
            Id = Guid.NewGuid(),
            CertificateDer = Ca.CertificateDer,
            Fingerprint = Ca.Fingerprint,
            // The gateway never reads the CA key; the fake signer in these tests uses Ca.PrivateKeyPkcs8 directly.
            EncryptedPrivateKey = [0],
            CreatedAt = now,
            ExpiresAt = Ca.ExpiresAt
        });
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await Database.DisposeAsync();

    /// <summary>A managed or agent-only endpoint in a fresh client.</summary>
    public async Task<Endpoint> CreateEndpointAsync(EndpointTier tier = EndpointTier.AgentOnly)
    {
        var client = await Database.CreateClientAsync();
        var site = await Database.CreateSiteAsync(client.Id);
        return await Database.CreateEndpointAsync(site, tier);
    }

    /// <summary>Issues an agent certificate for the endpoint and records it like the signer does.</summary>
    public async Task<AgentCredential> IssueAsync(Endpoint endpoint, Guid? sanEndpointId = null, DateTime? revokedAt = null,
        DateTime? expiresAt = null, AgentComponent role = AgentComponent.Agent)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        var now = Database.Time.GetUtcNow().UtcDateTime;
        var issued = InternalCertificateAuthority.IssueAgentCertificate(Ca.CertificateDer, Ca.PrivateKeyPkcs8, csr,
            sanEndpointId ?? endpoint.Id, Database.InstanceId, now);

        await using var db = Database.DbFactory.CreateSystem();
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            Fingerprint = issued.Fingerprint,
            PublicKeyFingerprint = issued.PublicKeyFingerprint,
            SerialNumber = issued.SerialNumber,
            IssuedAt = issued.NotBefore,
            ExpiresAt = expiresAt ?? issued.NotAfter,
            RevokedAt = revokedAt,
            Role = role
        });
        await db.SaveChangesAsync();
        return new AgentCredential(issued, key);
    }

    /// <summary>All gateway components wired to the test database, without a host.</summary>
    public GatewayHarness CreateHarness(Action<GatewayOptions>? configure = null)
    {
        var options = new GatewayOptions { DuplicateProbeSeconds = 1, SigningTimeoutSeconds = 3 };
        configure?.Invoke(options);
        return new GatewayHarness(Database, Options.Create(options));
    }
}

/// <summary>An issued agent certificate with its private key.</summary>
public sealed record AgentCredential(InternalCertificateAuthority.IssuedCertificate Issued, ECDsa Key)
{
    public X509Certificate2 PublicCertificate() => X509CertificateLoader.LoadCertificate(Issued.CertificateDer);

    /// <summary>Certificate with private key, usable as a TLS client certificate on every platform.</summary>
    public X509Certificate2 ClientCertificate() => TestCertificates.WithKey(Issued.CertificateDer, Key);

    public AgentIdentity Identity(Guid endpointId, AgentComponent role = AgentComponent.Agent) =>
        new(endpointId, Issued.Fingerprint, Issued.PublicKeyFingerprint, Issued.NotAfter, role);

    public byte[] Csr() => new CertificateRequest("CN=agent", Key, HashAlgorithmName.SHA256).CreateSigningRequest();
}

public static class TestCertificates
{
    public static X509Certificate2 WithKey(byte[] certificateDer, ECDsa key)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
        var withKey = certificate.CopyWithPrivateKey(key);
        if (!OperatingSystem.IsWindows())
        {
            return withKey;
        }

        // SChannel cannot use an ephemeral key for TLS; a PKCS#12 round trip gives it a temporary key container.
        using (withKey)
        {
            return X509CertificateLoader.LoadPkcs12(withKey.Export(X509ContentType.Pkcs12), null);
        }
    }
}

/// <summary>Gateway components over the test database, with helpers to drive a session without a WebSocket.</summary>
public sealed class GatewayHarness : IDisposable
{
    public GatewayHarness(TestDatabase database, IOptions<GatewayOptions> options)
    {
        Database = database;
        Options = options.Value;
        Store = new GatewayStore(database.DataSource);
        AllowList = new CertificateAllowList(database.DataSource, database.Bus, database.Time, NullLogger<CertificateAllowList>.Instance);
        Signing = new SigningRequestClient(database.DataSource, database.Bus, database.Time, options);
        ReleaseDirectory = Directory.CreateTempSubdirectory("fleeto-release-").FullName;
        BinariesDirectory = Directory.CreateTempSubdirectory("fleeto-binaries-").FullName;
        options.Value.ReleaseDirectory = ReleaseDirectory;
        options.Value.AgentBinariesDirectory = BinariesDirectory;
        Releases = new ReleaseCatalog(Store, database.Bus, database.Time, options, NullLogger<ReleaseCatalog>.Instance, [ReleaseKey.Public]);
        Manager = new AgentSessionManager(Store, database.Bus, AllowList, Signing, Metrics, database.Time, options,
            NullLogger<AgentSessionManager>.Instance, releases: Releases);
    }

    /// <summary>The release key the catalog of this harness trusts.</summary>
    public static (byte[] Private, byte[] Public) ReleaseKey { get; } = Ed25519.GenerateKeyPair();

    public string ReleaseDirectory { get; }
    public string BinariesDirectory { get; }
    public ReleaseCatalog Releases { get; }

    public TestDatabase Database { get; }
    public GatewayOptions Options { get; }
    public GatewayMetrics Metrics { get; } = new();
    public GatewayStore Store { get; }
    public CertificateAllowList AllowList { get; }
    public SigningRequestClient Signing { get; }
    public AgentSessionManager Manager { get; }

    public AgentSession NewSession(AgentIdentity identity, string remoteAddress = "192.0.2.10") =>
        new(identity, remoteAddress, Options.SendQueueCapacity, Database.Time.GetUtcNow().UtcDateTime);

    public static Hello Hello(ulong configVersion = 0, string inventoryHash = "") => new()
    {
        AgentVersion = "0.1.0",
        Hostname = "WS-TEST",
        Os = new OsInfo { Platform = "windows", Name = "Windows 11 Pro", Version = "10.0.26200", Architecture = "amd64" },
        ConfigVersion = configVersion,
        InventoryHash = inventoryHash
    };

    /// <summary>Opens a session for an endpoint with an arbitrary identity (no certificate needed).</summary>
    public async Task<AgentSession> OpenAsync(Endpoint endpoint, ulong configVersion = 0)
    {
        var session = NewSession(new AgentIdentity(endpoint.Id, Guid.NewGuid().ToString("N"), "key", DateTime.UtcNow.AddDays(30)));
        Assert.True(await Manager.OpenAsync(session, Hello(configVersion), CancellationToken.None));
        return session;
    }

    /// <summary>Reads messages until one matches, or fails after the timeout.</summary>
    public static async Task<ServerMessage> ReadUntilAsync(AgentSession session, ServerMessage.BodyOneofCase body, TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await session.Outbox.ReadAsync(cts.Token);
            if (message.BodyCase == body)
            {
                return message;
            }
        }
    }

    /// <summary>Everything queued right now, without waiting.</summary>
    public static List<ServerMessage> Drain(AgentSession session)
    {
        var messages = new List<ServerMessage>();
        while (session.Outbox.TryRead(out var message))
        {
            messages.Add(message);
        }

        return messages;
    }

    public void Dispose()
    {
        Manager.Dispose();
        Releases.Dispose();
        try
        {
            Directory.Delete(ReleaseDirectory, true);
            Directory.Delete(BinariesDirectory, true);
        }
        catch (IOException)
        {
        }

        Signing.Dispose();
        AllowList.Dispose();
    }
}
