using System.Text.Json;
using System.Text.Json.Serialization;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Infrastructure.Settings;

/// <summary>Setting keys shared between web (writes) and workers (reads).</summary>
public static class SettingKeys
{
    /// <summary><see cref="SmtpSettings"/>, encrypted.</summary>
    public const string Smtp = "email.smtp";

    /// <summary><see cref="BackupSettings"/>, encrypted (contains storage credentials).</summary>
    public const string Backup = "backup.settings";

    /// <summary>ISO 8601 UTC time a technician asked for a backup now; the workers pick it up.</summary>
    public const string BackupRequestedAt = "backup.requested-at";

    /// <summary>Last evaluated <see cref="Core.Domain.LicenseState"/>, to detect transitions.</summary>
    public const string LicenseLastState = "license.last-state";

    /// <summary>Days of raw check results to keep when TimescaleDB is not installed. Default 30.</summary>
    public const string RetentionCheckResultsDays = "retention.check-results-days";
}

public enum SmtpSecurity
{
    None,
    StartTls,
    Tls
}

/// <summary>Outgoing mail server. The password is write-only in the UI and never returned.</summary>
public sealed record SmtpSettings
{
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 587;
    public SmtpSecurity Security { get; init; } = SmtpSecurity.StartTls;
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string FromAddress { get; init; } = string.Empty;
    public string FromName { get; init; } = "Fleeto";
}

public enum BackupDestinationType
{
    /// <summary>No off-VPS destination: the dashboard shows a warning.</summary>
    None,
    /// <summary>S3-compatible object storage in the EU, write-only credentials.</summary>
    S3,
    /// <summary>A directory (local development, or a mounted remote file system).</summary>
    Directory
}

/// <summary>Backup destination and the backup public key. The backup private key never reaches the instance.</summary>
public sealed record BackupSettings
{
    public BackupDestinationType DestinationType { get; init; } = BackupDestinationType.None;
    public string? S3Endpoint { get; init; }
    public string? S3Region { get; init; }
    public string? S3Bucket { get; init; }
    public string? S3Prefix { get; init; }
    public string? S3AccessKeyId { get; init; }
    public string? S3SecretAccessKey { get; init; }
    public string? DirectoryPath { get; init; }

    /// <summary>"fleetify-backup-pub:&lt;base64&gt;", see <see cref="BackupCipher.EncodePublicKey"/>.</summary>
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>Hour of the nightly backup, UTC.</summary>
    public int ScheduleHourUtc { get; init; } = 2;
}

/// <summary>Typed access to <see cref="Setting"/> rows. Encrypted values are bound to their key.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly ISecretProtector _protector;
    private readonly TimeProvider _time;

    public SettingsStore(IFleetifyDbContextFactory dbFactory, ISecretProtector protector, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _protector = protector;
        _time = time;
    }

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var text = await GetStringAsync(key, cancellationToken);
        return text is null ? null : JsonSerializer.Deserialize<T>(text, JsonOptions);
    }

    public Task SetAsync<T>(string key, T value, bool encrypted, Guid? userId, CancellationToken cancellationToken = default) where T : class =>
        SetStringAsync(key, JsonSerializer.Serialize(value, JsonOptions), encrypted, userId, cancellationToken);

    public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var setting = await db.Settings.AsNoTracking().SingleOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            return null;
        }

        return setting.IsEncrypted ? _protector.Unprotect(SecretPurposes.Settings, setting.Value, "Settings|" + key) : setting.Value;
    }

    public async Task SetStringAsync(string key, string value, bool encrypted, Guid? userId, CancellationToken cancellationToken = default)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        var stored = encrypted ? _protector.Protect(SecretPurposes.Settings, value, "Settings|" + key) : value;
        var setting = await db.Settings.SingleOrDefaultAsync(s => s.Key == key, cancellationToken);
        if (setting is null)
        {
            db.Settings.Add(new Setting { Key = key, Value = stored, IsEncrypted = encrypted, UpdatedAt = now, UpdatedByUserId = userId });
        }
        else
        {
            setting.Value = stored;
            setting.IsEncrypted = encrypted;
            setting.UpdatedAt = now;
            setting.UpdatedByUserId = userId;
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
