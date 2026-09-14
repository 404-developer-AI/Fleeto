using System.Globalization;
using System.Net;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Security;
using Fleetify.Infrastructure.Settings;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Services;

public sealed record SmtpView(string Host, int Port, SmtpSecurity Security, string? Username, bool HasPassword, string FromAddress, string FromName);

public sealed record SmtpInput(string? Host, int Port, SmtpSecurity Security, string? Username, string? NewPassword, bool ClearPassword,
    string? FromAddress, string? FromName);

public sealed record BackupView(BackupDestinationType DestinationType, string? S3Endpoint, string? S3Region, string? S3Bucket, string? S3Prefix,
    string? S3AccessKeyId, bool HasS3Secret, string? DirectoryPath, string PublicKey, int ScheduleHourUtc, DateTime? RequestedAt);

public sealed record BackupInput(BackupDestinationType DestinationType, string? S3Endpoint, string? S3Region, string? S3Bucket, string? S3Prefix,
    string? S3AccessKeyId, string? NewS3SecretAccessKey, string? DirectoryPath, string? PublicKey, int ScheduleHourUtc);

public sealed record BackupRunView(Guid Id, BackupKind Kind, BackupRunStatus Status, DateTime StartedAt, DateTime? CompletedAt, long SizeBytes,
    string ObjectKey, string? Error);

public sealed record NotificationChannelView(Guid Id, string Name, string Recipients, AlertSeverity MinimumSeverity, bool NotifyOnResolve, bool Enabled,
    DateTime UpdatedAt);

public sealed record NotificationChannelInput(string? Name, string? Recipients, AlertSeverity MinimumSeverity, bool NotifyOnResolve, bool Enabled);

public sealed record InstanceView(Guid InstanceId, string Fqdn, string WebBaseUrl, string AgentHostName, int AgentPort, string? CaFingerprint,
    DateTime? CaExpiresAt, string? SigningKeyId, string Version, DateTime CreatedAt);

/// <summary>
/// Instance settings for admins: email (SMTP), notification channels, backups and the instance identity. Secrets are
/// write-only: they are stored encrypted and never returned; a blank field keeps the current value.
/// </summary>
public sealed class SettingsService
{
    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;

    public SettingsService(IFleetifyDbContextFactory dbFactory, SettingsStore settings, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _time = time;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Email

    public async Task<SmtpView> GetSmtpAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        var smtp = await _settings.GetAsync<SmtpSettings>(SettingKeys.Smtp, cancellationToken) ?? new SmtpSettings();
        return new SmtpView(smtp.Host, smtp.Port, smtp.Security, smtp.Username, !string.IsNullOrEmpty(smtp.Password), smtp.FromAddress, smtp.FromName);
    }

    public async Task<ServiceResult> SaveSmtpAsync(Caller caller, SmtpInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        var host = ServiceSupport.Clean(input.Host);
        if (host is null || host.Length > 255 || host.Any(char.IsWhiteSpace))
        {
            return ServiceResult.Fail("Enter the host name of the mail server, for example smtp.example.com.");
        }

        if (input.Port is < 1 or > 65535)
        {
            return ServiceResult.Fail("Enter a port between 1 and 65535, usually 587 for STARTTLS or 465 for TLS.");
        }

        var from = ServiceSupport.Clean(input.FromAddress);
        if (!ServiceSupport.IsValidEmail(from))
        {
            return ServiceResult.Fail("Enter a valid sender address.");
        }

        var fromName = ServiceSupport.Clean(input.FromName) ?? "Fleeto";
        if (fromName.Length > 100 || input.Username is { Length: > 320 } || input.NewPassword is { Length: > 1000 })
        {
            return ServiceResult.Fail("One of the fields is too long. Shorten it and try again.");
        }

        var current = await _settings.GetAsync<SmtpSettings>(SettingKeys.Smtp, cancellationToken) ?? new SmtpSettings();
        var passwordChanged = input.ClearPassword || !string.IsNullOrEmpty(input.NewPassword);
        var updated = new SmtpSettings
        {
            Host = host,
            Port = input.Port,
            Security = input.Security,
            Username = ServiceSupport.Clean(input.Username),
            Password = input.ClearPassword ? null : string.IsNullOrEmpty(input.NewPassword) ? current.Password : input.NewPassword,
            FromAddress = from!,
            FromName = fromName
        };

        await _settings.SetAsync(SettingKeys.Smtp, updated, encrypted: true, caller.UserId, cancellationToken);
        await WriteAuditAsync(caller, passwordChanged ? AuditActions.CredentialChanged : AuditActions.SettingsChanged, "Setting", SettingKeys.Smtp,
            new { updated.Host, updated.Port, Security = updated.Security.ToString(), updated.Username, updated.FromAddress, PasswordChanged = passwordChanged },
            cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Queues a short test message to the signed-in admin; the workers deliver it with the saved settings.</summary>
    public async Task<ServiceResult> SendTestEmailAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        if (!ServiceSupport.IsValidEmail(caller.Email))
        {
            return ServiceResult.Fail("Your account has no valid email address to send the test message to.");
        }

        var smtp = await _settings.GetAsync<SmtpSettings>(SettingKeys.Smtp, cancellationToken);
        if (smtp is null || string.IsNullOrEmpty(smtp.Host))
        {
            return ServiceResult.Fail("Save the mail server settings first, then send a test email.");
        }

        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        var name = WebUtility.HtmlEncode(caller.Name);
        db.OutboxEmails.Add(new OutboxEmail
        {
            Id = Guid.NewGuid(),
            ToAddress = caller.Email,
            Subject = "Test email from Fleeto",
            TextBody = $"Hello {caller.Name},\n\nThis is a test message from Fleeto. Email delivery works with the current settings.\n\nFleeto",
            HtmlBody = $"<p>Hello {name},</p><p>This is a test message from Fleeto. Email delivery works with the current settings.</p><p>Fleeto</p>",
            Category = "test",
            NextAttemptAt = now,
            CreatedAt = now
        });
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.SettingsChanged, "Setting", SettingKeys.Smtp, null,
            new { Action = "test email queued", To = caller.Email }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Notification channels

    public async Task<IReadOnlyList<NotificationChannelView>> ListChannelsAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        return await db.NotificationChannels.AsNoTracking().OrderBy(c => c.Name)
            .Select(c => new NotificationChannelView(c.Id, c.Name, c.Recipients, c.MinimumSeverity, c.NotifyOnResolve, c.Enabled, c.UpdatedAt))
            .ToListAsync(cancellationToken);
    }

    public async Task<ServiceResult<Guid>> SaveChannelAsync(Caller caller, Guid? channelId, NotificationChannelInput input,
        CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<Guid>.Forbidden();
        }

        var name = ServiceSupport.Clean(input.Name);
        if (name is null || name.Length > 100)
        {
            return ServiceResult<Guid>.Fail("Enter a channel name of at most 100 characters.");
        }

        var recipients = ParseRecipients(input.Recipients, out var recipientProblem);
        if (recipientProblem is not null)
        {
            return ServiceResult<Guid>.Fail(recipientProblem);
        }

        await using var db = _dbFactory.CreateSystem();
        var now = _time.GetUtcNow().UtcDateTime;
        NotificationChannel channel;
        if (channelId is { } id)
        {
            var existing = await db.NotificationChannels.SingleOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (existing is null)
            {
                return ServiceResult<Guid>.NotFound("notification channel");
            }

            channel = existing;
        }
        else
        {
            channel = new NotificationChannel { Id = Guid.NewGuid(), Type = NotificationChannelType.Email, CreatedAt = now };
            db.NotificationChannels.Add(channel);
        }

        channel.Name = name;
        channel.Recipients = string.Join(", ", recipients);
        channel.MinimumSeverity = input.MinimumSeverity;
        channel.NotifyOnResolve = input.NotifyOnResolve;
        channel.Enabled = input.Enabled;
        channel.UpdatedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.NotificationChannelChanged, "NotificationChannel", channel.Id.ToString(), null,
            new { Change = channelId is null ? "created" : "updated", channel.Name, Recipients = recipients.Count, MinimumSeverity = channel.MinimumSeverity.ToString(), channel.NotifyOnResolve, channel.Enabled }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult<Guid>.Ok(channel.Id);
    }

    public async Task<ServiceResult> DeleteChannelAsync(Caller caller, Guid channelId, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var channel = await db.NotificationChannels.SingleOrDefaultAsync(c => c.Id == channelId, cancellationToken);
        if (channel is null)
        {
            return ServiceResult.NotFound("notification channel");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        db.NotificationChannels.Remove(channel);
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.NotificationChannelChanged, "NotificationChannel", channel.Id.ToString(), null,
            new { Change = "deleted", channel.Name }), now));
        await db.SaveChangesAsync(cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Splits recipients on commas, semicolons and line breaks and validates each address.</summary>
    internal static List<string> ParseRecipients(string? text, out string? problem)
    {
        problem = null;
        var recipients = (text ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (recipients.Count == 0)
        {
            problem = "Enter at least one email address.";
            return recipients;
        }

        if (recipients.Count > 50)
        {
            problem = "A channel can have at most 50 recipients.";
            return recipients;
        }

        var invalid = recipients.FirstOrDefault(r => !ServiceSupport.IsValidEmail(r));
        if (invalid is not null)
        {
            problem = $"{invalid} is not a valid email address. Correct it and try again.";
            return recipients;
        }

        if (string.Join(", ", recipients).Length > 2000)
        {
            problem = "The recipient list is too long. Use fewer addresses, or a distribution list.";
        }

        return recipients;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Backups

    public async Task<BackupView> GetBackupAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        return await GetBackupViewAsync(cancellationToken);
    }

    internal async Task<BackupView> GetBackupViewAsync(CancellationToken cancellationToken)
    {
        var backup = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken) ?? new BackupSettings();
        var requested = await _settings.GetStringAsync(SettingKeys.BackupRequestedAt, cancellationToken);
        DateTime? requestedAt = DateTime.TryParse(requested, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
        return new BackupView(backup.DestinationType, backup.S3Endpoint, backup.S3Region, backup.S3Bucket, backup.S3Prefix, backup.S3AccessKeyId,
            !string.IsNullOrEmpty(backup.S3SecretAccessKey), backup.DirectoryPath, backup.PublicKey, backup.ScheduleHourUtc, requestedAt);
    }

    /// <summary>Saves the backup destination and public key. The S3 secret is write-only: blank keeps the current one.</summary>
    public async Task<ServiceResult> SaveBackupAsync(Caller caller, BackupInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        var current = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken) ?? new BackupSettings();
        var secret = string.IsNullOrEmpty(input.NewS3SecretAccessKey) ? current.S3SecretAccessKey : input.NewS3SecretAccessKey;
        if (ValidateBackup(input, secret) is { } problem)
        {
            return ServiceResult.Fail(problem);
        }

        var updated = input.DestinationType switch
        {
            BackupDestinationType.S3 => new BackupSettings
            {
                DestinationType = BackupDestinationType.S3,
                S3Endpoint = ServiceSupport.Clean(input.S3Endpoint),
                S3Region = ServiceSupport.Clean(input.S3Region),
                S3Bucket = ServiceSupport.Clean(input.S3Bucket),
                S3Prefix = ServiceSupport.Clean(input.S3Prefix),
                S3AccessKeyId = ServiceSupport.Clean(input.S3AccessKeyId),
                S3SecretAccessKey = secret,
                PublicKey = input.PublicKey!.Trim(),
                ScheduleHourUtc = input.ScheduleHourUtc
            },
            BackupDestinationType.Directory => new BackupSettings
            {
                DestinationType = BackupDestinationType.Directory,
                DirectoryPath = ServiceSupport.Clean(input.DirectoryPath),
                PublicKey = input.PublicKey!.Trim(),
                ScheduleHourUtc = input.ScheduleHourUtc
            },
            _ => new BackupSettings { DestinationType = BackupDestinationType.None, PublicKey = input.PublicKey?.Trim() ?? string.Empty, ScheduleHourUtc = input.ScheduleHourUtc }
        };

        await _settings.SetAsync(SettingKeys.Backup, updated, encrypted: true, caller.UserId, cancellationToken);
        var secretChanged = !string.IsNullOrEmpty(input.NewS3SecretAccessKey);
        await WriteAuditAsync(caller, secretChanged ? AuditActions.CredentialChanged : AuditActions.SettingsChanged, "Setting", SettingKeys.Backup,
            new
            {
                DestinationType = updated.DestinationType.ToString(),
                updated.S3Endpoint,
                updated.S3Bucket,
                updated.S3Prefix,
                updated.DirectoryPath,
                updated.ScheduleHourUtc,
                SecretChanged = secretChanged
            }, cancellationToken);
        return ServiceResult.Ok();
    }

    internal static string? ValidateBackup(BackupInput input, string? effectiveSecret)
    {
        if (input.ScheduleHourUtc is < 0 or > 23)
        {
            return "Choose an hour between 0 and 23 (UTC) for the nightly backup.";
        }

        if (input.DestinationType == BackupDestinationType.None)
        {
            return null;
        }

        if (!BackupCipher.TryDecodePublicKey(input.PublicKey?.Trim(), out _))
        {
            return "The backup public key is not valid. Paste the public key printed by fleetify-tool backup keygen; it starts with fleetify-backup-pub:.";
        }

        if (input.DestinationType == BackupDestinationType.Directory)
        {
            var path = ServiceSupport.Clean(input.DirectoryPath);
            return path is null || path.Length > 500 ? "Enter the directory path backups are written to." : null;
        }

        if (!Uri.TryCreate(ServiceSupport.Clean(input.S3Endpoint), UriKind.Absolute, out var endpoint) || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return "Enter the S3 endpoint as an https URL, for example https://s3.eu-central-1.amazonaws.com.";
        }

        if (ServiceSupport.Clean(input.S3Bucket) is null || ServiceSupport.Clean(input.S3AccessKeyId) is null || string.IsNullOrEmpty(effectiveSecret))
        {
            return "Enter the bucket, the access key and the secret key of a write-only S3 credential.";
        }

        if (new[] { input.S3Region, input.S3Bucket, input.S3Prefix, input.S3AccessKeyId }.Any(v => v is { Length: > 255 }) ||
            input.NewS3SecretAccessKey is { Length: > 1000 })
        {
            return "One of the fields is too long. Shorten it and try again.";
        }

        return null;
    }

    public async Task<IReadOnlyList<BackupRunView>> ListBackupRunsAsync(Caller caller, int limit = 20, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        return await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Take(Math.Clamp(limit, 1, 100))
            .Select(r => new BackupRunView(r.Id, r.Kind, r.Status, r.StartedAt, r.CompletedAt, r.SizeBytes, r.ObjectKey, r.Error))
            .ToListAsync(cancellationToken);
    }

    /// <summary>Asks the workers for a backup now. Refused while no destination is configured.</summary>
    public async Task<ServiceResult> RequestBackupNowAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        var backup = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken);
        if (backup is null || backup.DestinationType == BackupDestinationType.None)
        {
            return ServiceResult.Fail("Configure a backup destination and save it first, then start a backup.");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await _settings.SetStringAsync(SettingKeys.BackupRequestedAt, now.ToString("O", CultureInfo.InvariantCulture), encrypted: false, caller.UserId, cancellationToken);
        await WriteAuditAsync(caller, AuditActions.BackupStarted, "Backup", "manual", new { RequestedAt = now }, cancellationToken);
        return ServiceResult.Ok();
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Instance

    public async Task<InstanceView?> GetInstanceAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        caller.EnsureAdmin();
        await using var db = _dbFactory.CreateSystem();
        var instance = await db.InstanceSettings.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (instance is null)
        {
            return null;
        }

        // Public columns only (column-level grants).
        var ca = await db.CertificateAuthorities.AsNoTracking().Where(c => c.RetiredAt == null).OrderByDescending(c => c.CreatedAt)
            .Select(c => new { c.Fingerprint, c.ExpiresAt }).FirstOrDefaultAsync(cancellationToken);
        var signingKey = await db.InstanceSigningKeys.AsNoTracking().Where(k => k.RetiredAt == null).OrderByDescending(k => k.CreatedAt)
            .Select(k => k.Id).FirstOrDefaultAsync(cancellationToken);
        return new InstanceView(instance.InstanceId, instance.Fqdn, instance.WebBaseUrl, instance.AgentHostName, instance.AgentPort, ca?.Fingerprint,
            ca?.ExpiresAt, signingKey, FleetifyVersion.Current, instance.CreatedAt);
    }

    private async Task WriteAuditAsync(Caller caller, string action, string targetType, string targetId, object details, CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(action, targetType, targetId, null, details), _time.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(cancellationToken);
    }
}
