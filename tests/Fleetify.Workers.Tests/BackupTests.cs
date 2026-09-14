using System.Globalization;
using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Security;
using Fleetify.Infrastructure.Settings;
using Fleetify.Workers.Backups;
using Fleetify.Workers.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Fleetify.Workers.Tests;

/// <summary>Skips a test with a clear reason when pg_dump is not installed on this machine.</summary>
public sealed class PgDumpFactAttribute : FactAttribute
{
    public PgDumpFactAttribute()
    {
        if (PgDumpLocator.FindExecutable("auto") is null)
        {
            Skip = "pg_dump was not found (PATH or C:\\Program Files\\PostgreSQL\\*\\bin). Install the PostgreSQL client tools to run backup tests.";
        }
    }
}

/// <summary>
/// Guarantees that a backup to a directory destination produces an encrypted file that the offline backup private key
/// restores to the exact pg_dump output, that the run is recorded, and that a broken configuration fails the run with a
/// stored reason, an admin email and no temporary files left behind.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class BackupTests
{
    private readonly WorkersFixture _fixture;

    public BackupTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class TestTarget(string connectionString) : IPgDumpTargetProvider
    {
        public PgDumpTarget GetTarget()
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return new PgDumpTarget(builder.Host!, builder.Port, builder.Database!, builder.Username!, builder.Password ?? string.Empty, SslMode.Disable);
        }
    }

    private static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "fleetify-workers-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private (BackupService Service, PgDumpRunner Runner, BackupDestinations Destinations) CreateService(string tempDirectory)
    {
        var options = MsOptions.Create(new BackupOptions { PgDumpPath = "auto", TempDirectory = tempDirectory, UploadAttempts = 1 });
        var runner = new PgDumpRunner(options, new TestTarget(_fixture.Db.ConnectionString), NullLogger<PgDumpRunner>.Instance);
        var destinations = new BackupDestinations(options, NullLogger<BackupDestinations>.Instance);
        var service = new BackupService(_fixture.Db.DbFactory, _fixture.Settings(), runner, destinations, options, _fixture.Heartbeat(),
            _fixture.Db.Time, NullLogger<BackupService>.Instance);
        return (service, runner, destinations);
    }

    [PgDumpFact]
    public async Task An_encrypted_backup_decrypts_to_the_original_pg_dump_bytes()
    {
        var temp = TempDirectory();
        var destination = TempDirectory();
        var (_, runner, destinations) = CreateService(temp);
        var (privateKey, publicKey) = BackupCipher.GenerateKeyPair();
        var settings = new BackupSettings { DestinationType = BackupDestinationType.Directory, DirectoryPath = destination, PublicKey = BackupCipher.EncodePublicKey(publicKey) };

        var dump = Path.Combine(temp, "original.dump");
        await runner.DumpAsync(dump, CancellationToken.None);
        var encrypted = dump + ".fbk";
        await BackupDestinations.EncryptFileAsync(dump, encrypted, publicKey, CancellationToken.None);
        var key = BackupDestinations.DumpObjectKey(settings, Guid.NewGuid(), _fixture.Now);
        await destinations.UploadAsync(settings, encrypted, key, CancellationToken.None);

        var stored = Path.Combine(destination, key.Replace('/', Path.DirectorySeparatorChar));
        await using var input = File.OpenRead(stored);
        using var restored = new MemoryStream();
        await BackupCipher.DecryptAsync(input, restored, privateKey, CancellationToken.None);

        Assert.Equal(await File.ReadAllBytesAsync(dump), restored.ToArray());
        Assert.NotEqual(await File.ReadAllBytesAsync(dump), await File.ReadAllBytesAsync(stored));
    }

    [PgDumpFact]
    public async Task A_requested_backup_to_a_directory_is_recorded_and_restorable()
    {
        var temp = TempDirectory();
        var destination = TempDirectory();
        var (service, _, _) = CreateService(temp);
        var (privateKey, publicKey) = BackupCipher.GenerateKeyPair();
        var store = _fixture.Settings();
        await store.SetAsync(SettingKeys.Backup, new BackupSettings
        {
            DestinationType = BackupDestinationType.Directory, DirectoryPath = destination, PublicKey = BackupCipher.EncodePublicKey(publicKey)
        }, encrypted: true, userId: null);
        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));
        await store.SetStringAsync(SettingKeys.BackupRequestedAt, _fixture.Now.ToString("O", CultureInfo.InvariantCulture), encrypted: false, userId: null);
        _fixture.Db.Time.Advance(TimeSpan.FromSeconds(1));

        var run = await service.RunIfDueAsync(CancellationToken.None);

        Assert.NotNull(run);
        Assert.Equal(BackupKind.Manual, run.Kind);
        Assert.Equal(BackupRunStatus.Succeeded, run.Status);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var stored = await db.BackupRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id);
            Assert.Equal(BackupRunStatus.Succeeded, stored.Status);
            Assert.Matches(@"^[0-9a-f-]{36}/\d{4}/\d{2}/fleetify-\d{8}T\d{6}Z\.dump\.fbk$", stored.ObjectKey);
            Assert.True(stored.SizeBytes > 0);
        }

        var file = Path.Combine(destination, run.ObjectKey.Replace('/', Path.DirectorySeparatorChar));
        await using (var input = File.OpenRead(file))
        {
            using var restored = new MemoryStream();
            await BackupCipher.DecryptAsync(input, restored, privateKey, CancellationToken.None);
            Assert.Equal("PGDMP"u8.ToArray(), restored.ToArray()[..5]);
        }

        Assert.Empty(Directory.GetFiles(temp, "*", SearchOption.AllDirectories));
        Assert.NotEqual(BackupKind.Manual, await service.DueKindAsync(CancellationToken.None));
    }

    [Fact]
    public async Task A_backup_with_an_invalid_public_key_fails_with_a_reason_and_an_admin_email()
    {
        var admin = await _fixture.CreateAdminAsync();
        var temp = TempDirectory();
        var (service, _, _) = CreateService(temp);

        var run = await service.RunBackupAsync(BackupKind.Manual,
            new BackupSettings { DestinationType = BackupDestinationType.Directory, DirectoryPath = TempDirectory(), PublicKey = "not a key" },
            CancellationToken.None);

        Assert.Equal(BackupRunStatus.Failed, run.Status);
        Assert.Contains("backup public key", run.Error);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Equal(BackupRunStatus.Failed, (await db.BackupRuns.AsNoTracking().SingleAsync(r => r.Id == run.Id)).Status);
        Assert.True(await db.OutboxEmails.AnyAsync(e => e.ToAddress == admin.Email && e.Category == "backup"));
        Assert.Empty(Directory.GetFiles(temp, "*", SearchOption.AllDirectories));
    }
}
