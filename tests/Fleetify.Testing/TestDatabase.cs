using System.Security.Cryptography;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Fleetify.Testing;

/// <summary>
/// A fresh PostgreSQL database for one test project, migrated and seeded with an instance, data keys, the default
/// policy and the starter monitoring template.
/// <para>
/// Connection: the <c>FLEETIFY_TEST_ADMIN_CONNECTION</c> environment variable (a superuser connection string,
/// set in CI), else <c>Host=localhost;Username=postgres;Password=postgres</c> for local development. The database
/// <c>fleetify_test_&lt;name&gt;</c> is dropped and recreated on every run.
/// </para>
/// Use one instance per test project through an xUnit collection fixture; tests create their own clients so they
/// do not interfere with each other.
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    public const string DefaultAdminConnection = "Host=localhost;Port=5432;Username=postgres;Password=postgres";

    private TestDatabase(string connectionString, NpgsqlDataSource dataSource)
    {
        ConnectionString = connectionString;
        DataSource = dataSource;
        DbFactory = new FleetifyDbContextFactory(FleetifyDbContextFactory.BuildOptions(dataSource));
        RootKey = new RootKey(RandomNumberGenerator.GetBytes(32));
        SignerKey = new SignerKey(RandomNumberGenerator.GetBytes(32));
        SecretProtector = new EnvelopeSecretProtector(RootKey, DbFactory);
        AuditLog = new AuditLog(DbFactory, Time);
        Licenses = new LicenseService(DbFactory, Time, SecretProtector, AuditLog);
    }

    public string ConnectionString { get; }
    public NpgsqlDataSource DataSource { get; }
    public IFleetifyDbContextFactory DbFactory { get; }
    public RootKey RootKey { get; }
    public SignerKey SignerKey { get; }
    public ISecretProtector SecretProtector { get; }
    public IAuditLog AuditLog { get; }
    public LicenseService Licenses { get; }
    public InMemoryNotificationBus Bus { get; } = new();

    /// <summary>Controllable clock, starting at the real current time.</summary>
    public FakeTimeProvider Time { get; } = new(DateTimeOffset.UtcNow);

    public Guid InstanceId { get; private set; }

    public const string Fqdn = "rmm.test.example";

    /// <param name="name">Short project name, e.g. "gateway". Letters, digits and underscores.</param>
    public static async Task<TestDatabase> CreateAsync(string name)
    {
        var admin = new NpgsqlConnectionStringBuilder(
            Environment.GetEnvironmentVariable("FLEETIFY_TEST_ADMIN_CONNECTION") ?? DefaultAdminConnection);
        if (!System.Text.RegularExpressions.Regex.IsMatch(name, "^[a-z0-9_]{1,40}$"))
        {
            throw new ArgumentException("Use lowercase letters, digits and underscores for the test database name.", nameof(name));
        }

        var databaseName = "fleetify_test_" + name;

        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            // The database name is validated above; identifiers cannot be parameters.
#pragma warning disable CA2100
            await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{databaseName}\" WITH (FORCE)", connection);
            await drop.ExecuteNonQueryAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection);
            await create.ExecuteNonQueryAsync();
#pragma warning restore CA2100
        }

        var builder = new NpgsqlConnectionStringBuilder(admin.ConnectionString) { Database = databaseName, IncludeErrorDetail = true };
        var dataSource = new NpgsqlDataSourceBuilder(builder.ConnectionString).Build();
        var database = new TestDatabase(builder.ConnectionString, dataSource);

        await using (var db = database.DbFactory.CreateSystem())
        {
            await db.Database.MigrateAsync();
        }

        var initializer = new InstanceInitializer(database.DbFactory, database.RootKey, database.Time, NullLogger<InstanceInitializer>.Instance);
        var result = await initializer.RunAsync(new InstanceInitOptions(Fqdn, "agents." + Fqdn, 443, "https://" + Fqdn, ApplyGrants: false));
        database.InstanceId = result.InstanceId;
        return database;
    }

    public async Task<Client> CreateClientAsync(string? code = null)
    {
        await using var db = DbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Code = code ?? "T" + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)),
            Name = "Test client",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Clients.Add(client);
        await db.SaveChangesAsync();
        return client;
    }

    public async Task<Site> CreateSiteAsync(Guid clientId, string name = "Monitoring")
    {
        await using var db = DbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var site = new Site { Id = Guid.NewGuid(), ClientId = clientId, Name = name, CreatedAt = now, UpdatedAt = now };
        db.Sites.Add(site);
        await db.SaveChangesAsync();
        return site;
    }

    public async Task<Endpoint> CreateEndpointAsync(Site site, EndpointTier tier = EndpointTier.AgentOnly, string hostname = "WS-TEST",
        EndpointClass endpointClass = EndpointClass.Workstation)
    {
        await using var db = DbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var endpoint = new Endpoint
        {
            Id = Guid.NewGuid(),
            ClientId = site.ClientId,
            SiteId = site.Id,
            Hostname = hostname,
            DetectedClass = endpointClass,
            Tier = tier,
            OsPlatform = "windows",
            OsName = "Windows 11 Pro",
            OsVersion = "10.0.26200",
            Architecture = "amd64",
            AgentVersion = "0.1.0",
            EnrolledAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Endpoints.Add(endpoint);
        await db.SaveChangesAsync();
        return endpoint;
    }

    /// <summary>Creates an enrollment token and returns the plaintext token with the stored row.</summary>
    public async Task<(string Token, EnrollmentToken Row)> CreateEnrollmentTokenAsync(Site site, int? maxUses = 1, TimeSpan? lifetime = null)
    {
        await using var db = DbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var (token, id, hash) = OpaqueTokens.Create(OpaqueTokens.EnrollmentPrefix);
        var row = new EnrollmentToken
        {
            Id = id,
            ClientId = site.ClientId,
            SiteId = site.Id,
            Name = "Test token",
            TokenHash = hash,
            ExpiresAt = now + (lifetime ?? TimeSpan.FromDays(1)),
            MaxUses = maxUses,
            CreatedAt = now
        };
        db.EnrollmentTokens.Add(row);
        await db.SaveChangesAsync();
        return (token, row);
    }

    /// <summary>Loads a license signed with a fresh test key, bypassing the compiled trusted keys.</summary>
    public async Task<License> LoadTestLicenseAsync(int managedEndpointCount, DateTime? expiresAt = null)
    {
        await using var db = DbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        await db.Licenses.Where(l => l.IsActive).ExecuteUpdateAsync(s => s.SetProperty(l => l.IsActive, false));
        var license = new License
        {
            Id = Guid.NewGuid(),
            Serial = "TEST-" + Guid.NewGuid().ToString("N")[..8],
            CustomerName = "Test",
            Fqdn = Fqdn,
            ManagedEndpointCount = managedEndpointCount,
            IssuedAt = now,
            ExpiresAt = expiresAt ?? now.AddYears(1),
            KeyId = "test",
            EncryptedDocument = "test",
            IsActive = true,
            LoadedAt = now
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license;
    }

    public async ValueTask DisposeAsync()
    {
        await DataSource.DisposeAsync();
    }
}
