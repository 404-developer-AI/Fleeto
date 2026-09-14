using System.Globalization;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Security;
using Fleetify.Infrastructure.Settings;
using Fleetify.Workers.Common;
using Fleetify.Workers.Email;
using Fleetify.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleetify.Workers.Licensing;

/// <summary>
/// Watches the license (ARCHITECTURE.md §5, Licensing), at start and every hour:
/// <list type="bullet">
/// <item>Advances the protected clock once a day, so setting the system clock back does not extend a license.</item>
/// <item>Detects state transitions (stored in <see cref="SettingKeys.LicenseLastState"/>). When managed behaviour
/// starts or stops being allowed it raises an instance-wide configuration change, so every endpoint gets a signed
/// configuration for its new effective tier. Admins get an email: expiring soon once, grace period daily, expired once.</item>
/// <item>Once a day decrypts the active license document and verifies signature, FQDN and the stored values. A failure
/// is logged as critical; the workers role cannot modify the license row, and the tier decision stays with LicenseService.</item>
/// </list>
/// </summary>
public sealed class LicenseMonitorService : WorkerLoop
{
    /// <summary>UTC date (yyyy-MM-dd) the last grace period email went out.</summary>
    public const string GraceEmailSentOnKey = "license.grace-email-sent-on";

    private static readonly TimeSpan Daily = TimeSpan.FromDays(1);

    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly ISecretProtector _protector;
    private DateTimeOffset _lastClockAdvance = DateTimeOffset.MinValue;
    private DateTimeOffset _lastVerification = DateTimeOffset.MinValue;

    public LicenseMonitorService(IFleetifyDbContextFactory dbFactory, LicenseService licenses, ISecretProtector protector,
        WorkerHeartbeat heartbeat, TimeProvider time, ILogger<LicenseMonitorService> logger)
        : base("license-monitor", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _protector = protector;
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow();
        if (now - _lastClockAdvance >= Daily)
        {
            await _licenses.AdvanceClockAsync(cancellationToken);
            _lastClockAdvance = now;
        }

        if (now - _lastVerification >= Daily)
        {
            await VerifyActiveLicenseAsync(cancellationToken);
            _lastVerification = now;
        }

        await EvaluateAsync(cancellationToken);
        return false;
    }

    /// <summary>Evaluates the license state, handles a transition and sends the due emails. Returns the current state.</summary>
    public async Task<LicenseState> EvaluateAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var status = await _licenses.GetStatusAsync(db, cancellationToken);
        var protectedNow = await _licenses.ProtectedNowAsync(db, cancellationToken);
        var now = Time.GetUtcNow().UtcDateTime;

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var lastStateSetting = await db.Settings.SingleOrDefaultAsync(s => s.Key == SettingKeys.LicenseLastState, cancellationToken);
        LicenseState? lastState = lastStateSetting is not null && Enum.TryParse<LicenseState>(lastStateSetting.Value, out var parsed) ? parsed : null;

        var emails = new List<EmailContent>();
        InstanceInfo? instance = null;

        if (lastState != status.State)
        {
            // Unknown history counts as "managed allowed": the first run after an upgrade on an expired instance still
            // pushes agent-only configurations.
            var wasAllowed = lastState is null || new LicenseStatus(lastState.Value, 0, null, null).AllowsManaged;
            if (wasAllowed != status.AllowsManaged)
            {
                db.ConfigChangeEvents.Add(new ConfigChangeEvent { Scope = ConfigChangeScope.Instance, CreatedAt = now });
                Logger.LogWarning("License state changed from {From} to {To}; managed behaviour is now {Allowed}. Requested new configurations for every endpoint",
                    lastState?.ToString() ?? "unknown", status.State, status.AllowsManaged ? "allowed" : "paused");
            }
            else
            {
                Logger.LogInformation("License state changed from {From} to {To}", lastState?.ToString() ?? "unknown", status.State);
            }

            instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
            if (status.State == LicenseState.ExpiringSoon && status.ExpiresAt is { } expiresAt)
            {
                emails.Add(EmailTemplates.LicenseExpiringSoon(instance.Fqdn, expiresAt, instance.LicensingUrl));
            }
            else if (status.State == LicenseState.Expired && status.GraceEndsAt is { } graceEndedAt)
            {
                emails.Add(EmailTemplates.LicenseExpired(instance.Fqdn, graceEndedAt, instance.LicensingUrl));
            }

            if (lastStateSetting is null)
            {
                db.Settings.Add(new Setting { Key = SettingKeys.LicenseLastState, Value = status.State.ToString(), UpdatedAt = now });
            }
            else
            {
                lastStateSetting.Value = status.State.ToString();
                lastStateSetting.UpdatedAt = now;
            }
        }

        if (status is { State: LicenseState.GracePeriod, ExpiresAt: { } expiredAt, GraceEndsAt: { } graceEndsAt })
        {
            var today = protectedNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var sentOn = await db.Settings.SingleOrDefaultAsync(s => s.Key == GraceEmailSentOnKey, cancellationToken);
            if (sentOn?.Value != today)
            {
                instance ??= await InstanceQueries.GetInstanceAsync(db, cancellationToken);
                emails.Add(EmailTemplates.LicenseGracePeriod(instance.Fqdn, expiredAt, graceEndsAt, instance.LicensingUrl));
                if (sentOn is null)
                {
                    db.Settings.Add(new Setting { Key = GraceEmailSentOnKey, Value = today, UpdatedAt = now });
                }
                else
                {
                    sentOn.Value = today;
                    sentOn.UpdatedAt = now;
                }
            }
        }

        if (emails.Count > 0)
        {
            var admins = await InstanceQueries.GetAdminEmailsAsync(db, cancellationToken);
            if (admins.Count == 0)
            {
                Logger.LogWarning("No admin has an email address; the license email '{Subject}' was not queued", emails[0].Subject);
            }

            foreach (var content in emails)
            {
                foreach (var admin in admins)
                {
                    db.OutboxEmails.Add(OutboxEmails.Create(admin, content, OutboxEmails.CategoryLicense, now));
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return status.State;
    }

    /// <summary>
    /// Decrypts and verifies the active license document: signature against the compiled trusted keys, the instance
    /// FQDN, and the values stored in the row (a row edited in the database to extend a license is detected here).
    /// Returns true when the license is valid or no license is loaded.
    /// </summary>
    public async Task<bool> VerifyActiveLicenseAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var license = await db.Licenses.AsNoTracking().Where(l => l.IsActive).FirstOrDefaultAsync(cancellationToken);
        if (license is null)
        {
            return true;
        }

        string text;
        try
        {
            text = _protector.Unprotect(SecretPurposes.License, license.EncryptedDocument, "Licenses|" + license.Serial);
        }
        catch (Exception ex) when (ex is System.Security.Cryptography.CryptographicException or FormatException or InvalidOperationException)
        {
            Logger.LogCritical("The active license {Serial} could not be decrypted ({Error}). The license row or the root key has been changed. Load the license file again in Settings, Licensing",
                license.Serial, ex.GetType().Name);
            return false;
        }

        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        var verification = LicenseCodec.Verify(text, TrustedKeys.LicenseKeys, instance.Fqdn);
        if (!verification.IsValid || verification.Document is null)
        {
            Logger.LogCritical("The active license {Serial} failed verification: {Problem}", license.Serial, verification.Problem);
            return false;
        }

        var document = verification.Document;
        if (document.Serial != license.Serial ||
            document.ManagedEndpointCount != license.ManagedEndpointCount ||
            DateTime.SpecifyKind(document.ExpiresAt, DateTimeKind.Utc) != DateTime.SpecifyKind(license.ExpiresAt, DateTimeKind.Utc))
        {
            Logger.LogCritical("The stored values of license {Serial} do not match the signed document. The license row has been changed. Load the license file again in Settings, Licensing",
                license.Serial);
            return false;
        }

        return true;
    }
}
