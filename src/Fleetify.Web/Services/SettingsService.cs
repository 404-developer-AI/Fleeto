using System.Globalization;
using System.Net;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Audit;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Email;
using Fleetify.Infrastructure.Security;
using Fleetify.Infrastructure.Settings;
using Fleetify.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleetify.Web.Services;

public sealed record SmtpView(string Host, int Port, SmtpSecurity Security, string? Username, bool HasPassword, string FromAddress, string FromName);

public sealed record SmtpInput(string? Host, int Port, SmtpSecurity Security, string? Username, string? NewPassword, bool ClearPassword,
    string? FromAddress, string? FromName);

/// <summary>Microsoft Graph email settings without secrets or private keys.</summary>
public sealed record GraphView(string TenantId, string ClientId, string SenderAddress, GraphCredentialType CredentialType, bool HasClientSecret,
    DateTime? ClientSecretExpiresAt, string? CertificateThumbprint, DateTime? CertificateExpiresAt, string? PendingCertificateThumbprint,
    DateTime? PendingCertificateExpiresAt, bool IsComplete);

/// <param name="NewClientSecret">A new secret value; blank keeps the current secret.</param>
/// <param name="ClientSecretExpiresOn">The end date of the secret as shown in the app registration (a date, UTC).</param>
public sealed record GraphInput(string? TenantId, string? ClientId, string? SenderAddress, GraphCredentialType CredentialType, string? NewClientSecret,
    DateTime? ClientSecretExpiresOn);

public sealed record EmailView(EmailProvider Provider, SmtpView Smtp, GraphView Graph);

public sealed record GraphPublicCertificate(byte[] Der, string Thumbprint);

public sealed record BackupView(BackupDestinationType DestinationType, string? S3Endpoint, string? S3Region, string? S3Bucket, string? S3Prefix,
    string? S3AccessKeyId, bool HasS3Secret, string? DirectoryPath, string PublicKey, int ScheduleHourUtc, DateTime? RequestedAt);

public sealed record BackupInput(BackupDestinationType DestinationType, string? S3Endpoint, string? S3Region, string? S3Bucket, string? S3Prefix,
    string? S3AccessKeyId, string? NewS3SecretAccessKey, string? DirectoryPath, string? PublicKey, int ScheduleHourUtc);

public sealed record BackupRunView(Guid Id, BackupKind Kind, BackupRunStatus Status, DateTime StartedAt, DateTime? CompletedAt, long SizeBytes,
    string ObjectKey, string? Error);

public sealed record InstanceView(Guid InstanceId, string Fqdn, string WebBaseUrl, string AgentHostName, int AgentPort, string? CaFingerprint,
    DateTime? CaExpiresAt, string? SigningKeyId, string Version, DateTime CreatedAt);

/// <summary>
/// Instance settings for admins: email (SMTP), backups and the instance identity. Secrets are
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

    public async Task<EmailView> GetEmailAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        var smtp = await GetSmtpAsync(caller, cancellationToken);
        var graph = await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken) ?? new GraphMailSettings();
        return new EmailView(await GetProviderAsync(cancellationToken), smtp, new GraphView(graph.TenantId, graph.ClientId, graph.SenderAddress,
            graph.CredentialType, !string.IsNullOrEmpty(graph.ClientSecret), graph.ClientSecretExpiresAt, graph.CertificateThumbprint,
            graph.CertificateExpiresAt, graph.PendingCertificateThumbprint, graph.PendingCertificateExpiresAt, graph.IsComplete));
    }

    private async Task<EmailProvider> GetProviderAsync(CancellationToken cancellationToken) =>
        await _settings.GetStringAsync(SettingKeys.EmailProvider, cancellationToken) == nameof(EmailProvider.MicrosoftGraph)
            ? EmailProvider.MicrosoftGraph
            : EmailProvider.Smtp;

    /// <summary>Chooses how email is sent. Microsoft Graph can be chosen once its settings are complete.</summary>
    public async Task<ServiceResult> SaveProviderAsync(Caller caller, EmailProvider provider, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        if (!Enum.IsDefined(provider))
        {
            return ServiceResult.Fail("Choose SMTP or Microsoft Graph.");
        }

        if (provider == EmailProvider.MicrosoftGraph &&
            await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken) is not { IsComplete: true })
        {
            return ServiceResult.Fail("Save the Microsoft Graph settings with a client secret or a certificate first, then choose Microsoft Graph.");
        }

        await _settings.SetStringAsync(SettingKeys.EmailProvider, provider.ToString(), encrypted: false, caller.UserId, cancellationToken);
        await WriteAuditAsync(caller, AuditActions.SettingsChanged, "Setting", SettingKeys.EmailProvider, new { Provider = provider.ToString() }, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Saves the Graph app registration. The client secret is write-only: blank keeps the current secret.</summary>
    public async Task<ServiceResult> SaveGraphAsync(Caller caller, GraphInput input, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        var tenantId = ServiceSupport.Clean(input.TenantId);
        if (!GraphMail.IsValidTenantId(tenantId))
        {
            return ServiceResult.Fail("Enter the directory (tenant) ID of the app registration, or the tenant domain such as contoso.onmicrosoft.com.");
        }

        var clientId = ServiceSupport.Clean(input.ClientId);
        if (!GraphMail.IsValidClientId(clientId))
        {
            return ServiceResult.Fail("Enter the application (client) ID of the app registration. It looks like 00000000-0000-0000-0000-000000000000.");
        }

        var sender = ServiceSupport.Clean(input.SenderAddress);
        if (!ServiceSupport.IsValidEmail(sender))
        {
            return ServiceResult.Fail("Enter the address of the mailbox Fleeto sends as.");
        }

        if (!Enum.IsDefined(input.CredentialType) || input.NewClientSecret is { Length: > 1000 })
        {
            return ServiceResult.Fail("One of the fields is not valid. Check the credential and try again.");
        }

        var current = await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken) ?? new GraphMailSettings();
        var secretChanged = !string.IsNullOrWhiteSpace(input.NewClientSecret);
        var secret = secretChanged ? input.NewClientSecret!.Trim() : current.ClientSecret;
        var secretExpiresAt = current.ClientSecretExpiresAt;
        if (input.CredentialType == GraphCredentialType.ClientSecret)
        {
            if (string.IsNullOrEmpty(secret))
            {
                return ServiceResult.Fail("Enter the client secret value of the app registration, or use a certificate.");
            }

            if (input.ClientSecretExpiresOn is not { } expiresOn)
            {
                return ServiceResult.Fail("Enter the end date of the client secret, as shown under Certificates & secrets in the app registration.");
            }

            // The end of the chosen day, UTC: Entra ID shows the date only.
            var now = _time.GetUtcNow().UtcDateTime;
            secretExpiresAt = DateTime.SpecifyKind(expiresOn.Date, DateTimeKind.Utc).AddDays(1).AddSeconds(-1);
            if (secretChanged && secretExpiresAt <= now)
            {
                return ServiceResult.Fail("The end date of the new client secret has passed. Create a new secret and enter its end date.");
            }

            if (secretExpiresAt > now.AddYears(5))
            {
                return ServiceResult.Fail("Enter the real end date of the client secret; it is at most a few years away.");
            }
        }

        var updated = current with
        {
            TenantId = tenantId!,
            ClientId = clientId!,
            SenderAddress = sender!,
            CredentialType = input.CredentialType,
            ClientSecret = secret,
            ClientSecretExpiresAt = string.IsNullOrEmpty(secret) ? null : secretExpiresAt
        };
        if (!updated.IsComplete && await GetProviderAsync(cancellationToken) == EmailProvider.MicrosoftGraph)
        {
            return ServiceResult.Fail("Microsoft Graph sends the email of this instance, so these settings must stay complete. Create a certificate and switch to it first, or choose SMTP.");
        }
        await _settings.SetAsync(SettingKeys.Graph, updated, encrypted: true, caller.UserId, cancellationToken);
        await WriteAuditAsync(caller, secretChanged ? AuditActions.CredentialChanged : AuditActions.SettingsChanged, "Setting", SettingKeys.Graph,
            new
            {
                updated.TenantId,
                updated.ClientId,
                updated.SenderAddress,
                CredentialType = updated.CredentialType.ToString(),
                SecretChanged = secretChanged,
                updated.ClientSecretExpiresAt
            }, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>
    /// Creates a new certificate for the app registration. It waits as the pending certificate until the admin has uploaded it
    /// and switches to it, so sending keeps working with the current credential meanwhile.
    /// </summary>
    public async Task<ServiceResult> CreateGraphCertificateAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.CreateSystem();
        var fqdn = await db.InstanceSettings.AsNoTracking().Select(i => i.Fqdn).SingleAsync(cancellationToken);
        var certificate = GraphMail.CreateCertificate(fqdn, _time.GetUtcNow().UtcDateTime);
        var current = await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken) ?? new GraphMailSettings();
        var updated = current with
        {
            PendingCertificatePfx = certificate.PfxBase64,
            PendingCertificateThumbprint = certificate.Thumbprint,
            PendingCertificateExpiresAt = certificate.ExpiresAt
        };
        await _settings.SetAsync(SettingKeys.Graph, updated, encrypted: true, caller.UserId, cancellationToken);
        await WriteAuditAsync(caller, AuditActions.CredentialChanged, "Setting", SettingKeys.Graph,
            new { Change = "certificate created", certificate.Thumbprint, certificate.ExpiresAt }, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>Switches to the pending certificate and signs in with certificates from now on.</summary>
    public async Task<ServiceResult> ActivateGraphCertificateAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult.Forbidden();
        }

        var current = await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken);
        if (current?.PendingCertificatePfx is null)
        {
            return ServiceResult.Fail("There is no new certificate to switch to. Create one first.");
        }

        var updated = current with
        {
            CredentialType = GraphCredentialType.Certificate,
            CertificatePfx = current.PendingCertificatePfx,
            CertificateThumbprint = current.PendingCertificateThumbprint,
            CertificateExpiresAt = current.PendingCertificateExpiresAt,
            PendingCertificatePfx = null,
            PendingCertificateThumbprint = null,
            PendingCertificateExpiresAt = null
        };
        await _settings.SetAsync(SettingKeys.Graph, updated, encrypted: true, caller.UserId, cancellationToken);
        await WriteAuditAsync(caller, AuditActions.CredentialChanged, "Setting", SettingKeys.Graph,
            new { Change = "certificate in use", updated.CertificateThumbprint, updated.CertificateExpiresAt }, cancellationToken);
        return ServiceResult.Ok();
    }

    /// <summary>The public part (DER) of the pending certificate, or else of the certificate in use; null when there is none.</summary>
    public async Task<ServiceResult<GraphPublicCertificate>> GetGraphPublicCertificateAsync(Caller caller, CancellationToken cancellationToken = default)
    {
        if (!caller.IsAdmin)
        {
            return ServiceResult<GraphPublicCertificate>.Forbidden();
        }

        var graph = await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken);
        if (graph?.PendingCertificatePfx is { } pending)
        {
            return ServiceResult<GraphPublicCertificate>.Ok(new GraphPublicCertificate(GraphMail.PublicCertificate(pending), graph.PendingCertificateThumbprint ?? string.Empty));
        }

        return graph?.CertificatePfx is { } active
            ? ServiceResult<GraphPublicCertificate>.Ok(new GraphPublicCertificate(GraphMail.PublicCertificate(active), graph.CertificateThumbprint ?? string.Empty))
            : ServiceResult<GraphPublicCertificate>.Fail("There is no certificate to download. Create one first.");
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
        var graphReady = await GetProviderAsync(cancellationToken) == EmailProvider.MicrosoftGraph &&
                         await _settings.GetAsync<GraphMailSettings>(SettingKeys.Graph, cancellationToken) is { IsComplete: true };
        if (!graphReady && (smtp is null || string.IsNullOrEmpty(smtp.Host)))
        {
            return ServiceResult.Fail("Save the email settings first, then send a test email.");
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
