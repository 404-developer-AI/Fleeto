using System.Globalization;
using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Settings;
using Fleetify.Workers.Common;
using Fleetify.Workers.Email;
using Fleetify.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleetify.Workers.Licensing;

/// <summary>
/// Warns admins about stored credentials that reach their end date (<see cref="ExpiringCredentials"/>), at start and every
/// hour: one email when 30, 14, 7 and 1 days remain and one after expiry (<see cref="CredentialExpiryRules"/>). The stage
/// already warned about is remembered per credential together with its end date, in the same transaction as the emails, so
/// a renewed credential starts over and no warning is sent twice. The email itself goes through SMTP while an expired Graph
/// credential cannot send (<see cref="EmailTransportFactory"/>).
/// </summary>
public sealed class CredentialExpiryService : WorkerLoop
{
    private readonly IFleetifyDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;

    public CredentialExpiryService(IFleetifyDbContextFactory dbFactory, SettingsStore settings, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<CredentialExpiryService> logger)
        : base("credential-expiry", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
    }

    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await CheckAsync(cancellationToken);
        return false;
    }

    /// <summary>Queues the due warnings. Returns the number of emails queued.</summary>
    public async Task<int> CheckAsync(CancellationToken cancellationToken)
    {
        var credentials = await ExpiringCredentials.ListAsync(_settings, cancellationToken);
        var now = Time.GetUtcNow().UtcDateTime;
        var queued = 0;

        foreach (var credential in credentials)
        {
            var stage = credential.StageAt(now);
            if (stage == CredentialExpiryStage.None)
            {
                continue;
            }

            await using var db = _dbFactory.CreateSystem();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var key = SettingKeys.CredentialWarningPrefix + credential.Key;
            var record = await db.Settings.FromSqlInterpolated($"""SELECT * FROM "Settings" WHERE "Key" = {key} FOR UPDATE""")
                .SingleOrDefaultAsync(cancellationToken);
            var expiry = credential.ExpiresAt.ToString("O", CultureInfo.InvariantCulture);
            var warned = CredentialExpiryStage.None;
            if (record?.Value.Split('|') is [var recordedExpiry, var recordedStage] && recordedExpiry == expiry &&
                Enum.TryParse<CredentialExpiryStage>(recordedStage, out var parsed))
            {
                warned = parsed;
            }

            if (!CredentialExpiryRules.NeedsWarning(stage, warned))
            {
                continue;
            }

            var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
            var url = instance.WebBaseUrl.TrimEnd('/') + credential.SettingsPath;
            var content = stage == CredentialExpiryStage.Expired
                ? EmailTemplates.CredentialExpired(instance.Fqdn, credential.Name, credential.ExpiresAt, credential.StopsWorking, credential.NextStep, url,
                    credential.FallbackInUse)
                : EmailTemplates.CredentialExpiring(instance.Fqdn, credential.Name, credential.ExpiresAt, credential.StopsWorking, credential.NextStep, url);

            var admins = await InstanceQueries.GetAdminEmailsAsync(db, cancellationToken);
            foreach (var admin in admins)
            {
                db.OutboxEmails.Add(OutboxEmails.Create(admin, content, OutboxEmails.CategoryCredential, now));
                queued++;
            }

            if (admins.Count == 0)
            {
                Logger.LogWarning("No admin has an email address; the warning '{Subject}' was not queued", content.Subject);
            }

            var value = $"{expiry}|{stage}";
            if (record is null)
            {
                db.Settings.Add(new Setting { Key = key, Value = value, UpdatedAt = now });
            }
            else
            {
                record.Value = value;
                record.UpdatedAt = now;
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            Logger.LogWarning("{Credential} expires on {ExpiresAt:yyyy-MM-dd} ({Stage}); admins were emailed", credential.Name, credential.ExpiresAt, stage);
        }

        return queued;
    }
}
