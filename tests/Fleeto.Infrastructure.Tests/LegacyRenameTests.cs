using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Migrations;
using Fleeto.Infrastructure.Security;
using Fleeto.Testing;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The rename from Fleetify to Fleeto (0.2.1): data written before it stays usable. Functions and triggers of an existing database are
/// renamed without losing a rule, data keys are rewrapped, and certificates, licenses, backups and key files from before the rename are
/// still accepted, while everything new is written with the Fleeto names.
/// </summary>
public class LegacyRenameTests
{
    [Fact]
    public void Data_key_wrapped_with_the_old_label_is_rewrapped_once()
    {
        var rootKey = new RootKey(RandomNumberGenerator.GetBytes(32));
        var key = RandomNumberGenerator.GetBytes(32);
        var dataKey = new DataKey { Id = Guid.NewGuid(), Purpose = SecretPurposes.Settings, RootKeyId = rootKey.Id };
        dataKey.WrappedKey = AesGcmBox.Seal(rootKey.Key, key, Encoding.UTF8.GetBytes($"fleetify-dek|{dataKey.Id:N}|{dataKey.Purpose}"));

        Assert.True(EnvelopeSecretProtector.UpgradeLegacyWrap(dataKey, rootKey));
        Assert.Equal(key, AesGcmBox.Open(rootKey.Key, dataKey.WrappedKey, Encoding.UTF8.GetBytes($"fleeto-dek|{dataKey.Id:N}|{dataKey.Purpose}")));
        Assert.False(EnvelopeSecretProtector.UpgradeLegacyWrap(dataKey, rootKey));

        var fresh = EnvelopeSecretProtector.CreateDataKey(rootKey, SecretPurposes.License, DateTime.UtcNow);
        var before = fresh.WrappedKey.ToArray();
        Assert.False(EnvelopeSecretProtector.UpgradeLegacyWrap(fresh, rootKey));
        Assert.Equal(before, fresh.WrappedKey);
    }

    [Fact]
    public void Data_key_of_another_root_key_is_not_rewrapped()
    {
        var rootKey = new RootKey(RandomNumberGenerator.GetBytes(32));
        var other = new RootKey(RandomNumberGenerator.GetBytes(32));
        var dataKey = EnvelopeSecretProtector.CreateDataKey(other, SecretPurposes.Settings, DateTime.UtcNow);

        Assert.ThrowsAny<CryptographicException>(() => EnvelopeSecretProtector.UpgradeLegacyWrap(dataKey, rootKey));
    }

    [Fact]
    public void Certificate_with_the_old_endpoint_uri_still_names_its_endpoint()
    {
        var endpointId = Guid.NewGuid();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=" + endpointId, key, HashAlgorithmName.SHA256);
        var san = new SubjectAlternativeNameBuilder();
        san.AddUri(new Uri("urn:fleetify:endpoint:" + endpointId));
        request.CertificateExtensions.Add(san.Build());
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1));

        Assert.Equal(endpointId, InternalCertificateAuthority.EndpointIdFromCertificate(certificate));
    }

    [Fact]
    public void License_signed_with_the_old_context_verifies_and_new_licenses_use_the_fleeto_context()
    {
        var (privateKey, publicKey) = Ed25519.GenerateKeyPair();
        var document = new LicenseDocument
        {
            Serial = "L-1", CustomerName = "Test", Fqdn = "rmm.test.example", ManagedEndpointCount = 5,
            IssuedAt = DateTime.UtcNow.AddDays(-1), ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        var current = LicenseCodec.Sign(document, privateKey);
        var lines = current.Split('\n');
        var payload = Convert.FromBase64String(lines[1]);
        Assert.True(Ed25519.Verify(publicKey, "fleeto-license-v1", payload, Convert.FromBase64String(lines[2])));

        lines[2] = Convert.ToBase64String(Ed25519.Sign(privateKey, "fleetify-license-v1", payload));
        var legacy = string.Join('\n', lines);

        Assert.True(LicenseCodec.Verify(legacy, [publicKey], "rmm.test.example").IsValid);
        lines[2] = Convert.ToBase64String(Ed25519.Sign(privateKey, "fleetify-job-v1", payload));
        Assert.False(LicenseCodec.Verify(string.Join('\n', lines), [publicKey], "rmm.test.example").IsValid);
    }

    [Fact]
    public async Task Backup_written_before_the_rename_decrypts_with_its_old_keys()
    {
        var (privateKey, publicKey) = BackupCipher.GenerateKeyPair();
        var original = RandomNumberGenerator.GetBytes(BackupCipher.ChunkSize + 100);
        using var encrypted = new MemoryStream();
        await BackupCipher.EncryptAsync(new MemoryStream(original), encrypted, publicKey, Encoding.ASCII.GetBytes("fleetify-backup-v1"), CancellationToken.None);
        using var decrypted = new MemoryStream();

        var legacyPrivate = "fleetify-backup-key:" + Convert.ToBase64String(privateKey);
        var legacyPublic = "fleetify-backup-pub:" + Convert.ToBase64String(publicKey);
        encrypted.Position = 0;
        await BackupCipher.DecryptAsync(encrypted, decrypted, BackupCipher.DecodePrivateKey(legacyPrivate));

        Assert.Equal(original, decrypted.ToArray());
        Assert.True(BackupCipher.TryDecodePublicKey(legacyPublic, out var decoded));
        Assert.Equal(publicKey, decoded);
        Assert.StartsWith("fleeto-backup-pub:", BackupCipher.EncodePublicKey(publicKey));
    }
}

/// <summary>The rename migration against the shared test database; it leaves every name as it found it (the Fleeto names).</summary>
[Collection(DatabaseCollection.Name)]
public class RenameMigrationTests
{
    private readonly TestDatabase _db;

    public RenameMigrationTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task Rename_migration_moves_every_function_and_trigger_to_the_fleeto_names_and_keeps_the_rules()
    {
        var database = _db;
        await using var connection = await database.DataSource.OpenConnectionAsync();
        var triggersBefore = await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND NOT t.tgisinternal");

        // A database created before the rename: the same objects under the old names.
        await ExecuteAsync(connection, RenameToFleeto.RenameSql("fleeto_", "fleetify_"));
        Assert.True(await ScalarAsync<long>(connection, LegacyObjectCountSql) > 0);
        Assert.Equal(0, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_proc WHERE proname LIKE 'fleeto\\_%'"));

        await ExecuteAsync(connection, RenameToFleeto.RenameSql("fleetify_", "fleeto_"));

        Assert.Equal(0, await ScalarAsync<long>(connection, LegacyObjectCountSql));
        Assert.Equal(triggersBefore, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = 'public' AND NOT t.tgisinternal"));
        Assert.Equal(1, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_proc WHERE proname = 'fleeto_audit_append_only'"));
        Assert.Contains("fleeto_gateway", await ScalarAsync<string>(connection, "SELECT prosrc FROM pg_proc WHERE proname = 'fleeto_signing_request_origin'"));

        // The audit log still refuses to be emptied after its triggers were recreated.
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connection, """TRUNCATE "AuditEntries" """));

        // Running it again changes nothing.
        await ExecuteAsync(connection, RenameToFleeto.RenameSql("fleetify_", "fleeto_"));
        Assert.Equal(0, await ScalarAsync<long>(connection, LegacyObjectCountSql));
    }

    [Fact]
    public async Task Rename_migration_has_every_configuration_signed_again()
    {
        var database = _db;
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        var endpoint = await database.CreateEndpointAsync(site);
        await using var connection = await database.DataSource.OpenConnectionAsync();
        await using (var insert = new NpgsqlCommand("""
            INSERT INTO "EndpointConfigs" ("EndpointId", "ClientId", "Version", "Payload", "Signature", "KeyId", "ContentHash", "CreatedAt")
            VALUES (@endpoint, @client, 1, ''::bytea, ''::bytea, 'key', repeat('a', 64), now())
            """, connection))
        {
            insert.Parameters.AddWithValue("endpoint", endpoint.Id);
            insert.Parameters.AddWithValue("client", client.Id);
            await insert.ExecuteNonQueryAsync();
        }

        await ExecuteAsync(connection, RenameToFleeto.ResignConfigurationsSql);

        await using var hash = new NpgsqlCommand("""SELECT "ContentHash" FROM "EndpointConfigs" WHERE "EndpointId" = @endpoint""", connection);
        hash.Parameters.AddWithValue("endpoint", endpoint.Id);
        Assert.Equal("", (string)(await hash.ExecuteScalarAsync())!);
        Assert.True(await ScalarAsync<long>(connection, """SELECT count(*) FROM "ConfigChangeEvents" WHERE "Scope" = 'Instance' AND "ProcessedAt" IS NULL""") > 0);
    }

    private const string LegacyObjectCountSql = """
        SELECT (SELECT count(*) FROM pg_proc p JOIN pg_namespace n ON n.oid = p.pronamespace
                WHERE n.nspname = 'public' AND (p.proname LIKE 'fleetify%' OR p.prosrc LIKE '%fleetify%'))
             + (SELECT count(*) FROM pg_trigger t WHERE NOT t.tgisinternal AND pg_get_triggerdef(t.oid) LIKE '%fleetify%')
        """;

    internal static async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // fixed SQL from the migration and this test
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }

    internal static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // fixed SQL from this test
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
