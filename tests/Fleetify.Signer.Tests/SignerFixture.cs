using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Hosting;
using Fleetify.Infrastructure.Security;
using Fleetify.Infrastructure.Services;
using Fleetify.Protocol.Agent.V1;
using Fleetify.Signer.Handlers;
using Fleetify.Signer.Keys;
using Fleetify.Signer.Processing;
using Fleetify.Testing;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Fleetify.Signer.Tests;

[CollectionDefinition(Name)]
public sealed class SignerCollection : ICollectionFixture<SignerFixture>
{
    public const string Name = "signer";
}

/// <summary>
/// One migrated test database for the whole project, with the signer keys bootstrapped once. Tests create their own
/// clients and process only the requests they insert, so they do not interfere with each other.
/// </summary>
public sealed class SignerFixture : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;

    public SignerKeyRing KeyRing { get; } = new();

    /// <summary>
    /// Contexts that connect as the <c>fleetify_signer</c> role with the production grants, so every test also proves
    /// the signer needs nothing beyond what DatabaseGrants gives it. Test setup and assertions use the superuser
    /// <see cref="TestDatabase.DbFactory"/>.
    /// </summary>
    public IFleetifyDbContextFactory SignerDbFactory { get; private set; } = null!;

    private NpgsqlDataSource? _signerDataSource;

    public async Task InitializeAsync()
    {
        Database = await TestDatabase.CreateAsync("signer");

        // Roles are cluster-wide; setup-dev and install.sh create them with a login. CI may not have them yet.
        await using (var command = Database.DataSource.CreateCommand("""
            DO $$ BEGIN
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fleetify_web') THEN CREATE ROLE fleetify_web NOLOGIN; END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fleetify_gateway') THEN CREATE ROLE fleetify_gateway NOLOGIN; END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fleetify_signer') THEN CREATE ROLE fleetify_signer NOLOGIN; END IF;
              IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'fleetify_workers') THEN CREATE ROLE fleetify_workers NOLOGIN; END IF;
            END $$;
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = Database.DbFactory.CreateSystem())
        {
            await db.Database.ExecuteSqlRawAsync(DatabaseGrants.BuildSql());
        }

        // The superuser connects and every session runs as the signer role: the same privileges as production.
        var signerConnection = new NpgsqlConnectionStringBuilder(Database.ConnectionString) { Options = "-c role=" + DatabaseRoles.Signer };
        _signerDataSource = new NpgsqlDataSourceBuilder(signerConnection.ConnectionString).Build();
        SignerDbFactory = new FleetifyDbContextFactory(FleetifyDbContextFactory.BuildOptions(_signerDataSource));

        await CreateBootstrapper(Database.SignerKey, KeyRing).RunAsync();
    }

    public async Task DisposeAsync()
    {
        KeyRing.Dispose();
        if (_signerDataSource is not null)
        {
            await _signerDataSource.DisposeAsync();
        }

        await Database.DisposeAsync();
    }

    public DateTime Now => Database.Time.GetUtcNow().UtcDateTime;

    public SigningKeyBootstrapper CreateBootstrapper(SignerKey signerKey, SignerKeyRing keyRing) =>
        new(SignerDbFactory, signerKey, keyRing, Database.Time, NullLogger<SigningKeyBootstrapper>.Instance);

    public SigningRequestProcessor CreateProcessor(SigningRateLimiter? rateLimiter = null, SignerKeyRing? keyRing = null)
    {
        var ring = keyRing ?? KeyRing;
        var configSigner = new AgentConfigSigner(new AgentConfigBuilder(Database.Licenses), ring, NullLogger<AgentConfigSigner>.Instance);
        ISigningRequestHandler[] handlers =
        [
            new AgentEnrollmentHandler(ring, configSigner, NullLogger<AgentEnrollmentHandler>.Instance),
            new AgentRenewalHandler(ring, NullLogger<AgentRenewalHandler>.Instance),
            new AgentRecoveryHandler(ring, NullLogger<AgentRecoveryHandler>.Instance),
            new GatewayCertificateHandler(ring, NullLogger<GatewayCertificateHandler>.Instance),
            new AgentConfigHandler(configSigner),
            new JobHandler(ring, Database.Licenses, NullLogger<JobHandler>.Instance)
        ];
        return new SigningRequestProcessor(SignerDbFactory, ring, handlers, rateLimiter ?? new SigningRateLimiter(), Database.Bus,
            Database.Time, NullLogger<SigningRequestProcessor>.Instance);
    }

    public async Task<SigningRequest> InsertRequestAsync(SigningRequestKind kind, Guid? clientId, Guid? subjectId, byte[] payload,
        string requestedBy, DateTime? createdAt = null)
    {
        await using var db = Database.DbFactory.CreateSystem();
        var request = new SigningRequest
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            ClientId = clientId,
            SubjectId = subjectId,
            Payload = payload,
            RequestedBy = requestedBy,
            CreatedAt = createdAt ?? Now
        };
        db.SigningRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    /// <summary>Inserts a request, lets a fresh processor handle it and returns the stored row.</summary>
    public async Task<SigningRequest> ProcessAsync(SigningRequestKind kind, Guid? clientId, Guid? subjectId, byte[] payload,
        string requestedBy = "gateway", DateTime? createdAt = null)
    {
        var request = await InsertRequestAsync(kind, clientId, subjectId, payload, requestedBy, createdAt);
        await CreateProcessor().ProcessRequestAsync(request.Id);
        return await LoadRequestAsync(request.Id);
    }

    public async Task<SigningRequest> LoadRequestAsync(Guid id)
    {
        await using var db = Database.DbFactory.CreateSystem();
        return await db.SigningRequests.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    public async Task<(Site Site, string Token, EnrollmentToken Row)> CreateSiteWithTokenAsync(int? maxUses = 1, TimeSpan? lifetime = null)
    {
        var client = await Database.CreateClientAsync();
        var site = await Database.CreateSiteAsync(client.Id);
        var (token, row) = await Database.CreateEnrollmentTokenAsync(site, maxUses, lifetime);
        return (site, token, row);
    }

    public static byte[] EnrollPayload(string token, byte[] csrDer, string hostname = "WS-ENROLL", bool isServer = false) =>
        new EnrollRequest
        {
            Token = token,
            CsrDer = ByteString.CopyFrom(csrDer),
            Hostname = hostname,
            AgentVersion = "0.1.0",
            Os = new OsInfo { Platform = "windows", Name = isServer ? "Windows Server 2022 Standard" : "Windows 11 Pro", Version = "10.0.26200", IsServer = isServer, Architecture = "amd64" }
        }.ToByteArray();

    /// <summary>Enrolls a new endpoint through the processor and returns everything a renewal test needs.</summary>
    public async Task<EnrolledAgent> EnrollAsync(string hostname = "WS-RENEW")
    {
        var (site, token, _) = await CreateSiteWithTokenAsync();
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = await ProcessAsync(SigningRequestKind.AgentEnrollment, site.ClientId, null,
            EnrollPayload(token, Csr(key), hostname), "gateway:198.51.100.10");
        Assert.Equal(SigningRequestState.Completed, request.State);
        var response = EnrollResponse.Parser.ParseFrom(request.Result);
        return new EnrolledAgent(Guid.Parse(response.EndpointId), site.ClientId, key, response);
    }

    public static byte[] Csr(AsymmetricAlgorithm key, HashAlgorithmName? hash = null)
    {
        var request = key switch
        {
            ECDsa ecdsa => new CertificateRequest("CN=agent", ecdsa, hash ?? HashAlgorithmName.SHA256),
            RSA rsa => new CertificateRequest("CN=agent", rsa, hash ?? HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1),
            _ => throw new ArgumentException("Unsupported key type.", nameof(key))
        };
        return request.CreateSigningRequest();
    }

    /// <summary>True when the certificate chains to the CA at the fake clock's current time.</summary>
    public bool ChainsToCa(byte[] certificateDer, byte[] caDer)
    {
        using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
        using var ca = X509CertificateLoader.LoadCertificate(caDer);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.Add(ca);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        chain.ChainPolicy.VerificationTime = Now.AddMinutes(1);
        return chain.Build(certificate);
    }
}

public sealed record EnrolledAgent(Guid EndpointId, Guid ClientId, ECDsa Key, EnrollResponse Response);
