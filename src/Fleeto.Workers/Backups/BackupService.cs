using System.Globalization;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Common;
using Fleeto.Workers.Email;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleeto.Workers.Backups;

/// <summary>
/// Database backups (ARCHITECTURE.md §5, Backups). Runs nightly at <see cref="BackupSettings.ScheduleHourUtc"/> and when
/// a technician requests one (<see cref="SettingKeys.BackupRequestedAt"/> newer than the last run); checks every minute.
/// Steps: BackupRun Running → pg_dump as the read-only backup role into a temp file → encrypt with the backup public key
/// → upload (S3 PutObject with write-only credentials, or a directory) → BackupRun Succeeded or Failed plus an email to
/// admins. Temp files are always deleted. The backup private key never exists on the VPS.
/// </summary>
public sealed class BackupService : WorkerLoop
{
    public const string InterruptedError = "The backup was interrupted because the workers stopped. The next backup runs on schedule.";

    private static readonly TimeSpan NotConfiguredLogInterval = TimeSpan.FromDays(1);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private readonly PgDumpRunner _pgDump;
    private readonly BackupDestinations _destinations;
    private readonly BackupOptions _options;
    private DateTimeOffset _lastNotConfiguredLog = DateTimeOffset.MinValue;
    private bool _recovered;

    public BackupService(IFleetoDbContextFactory dbFactory, SettingsStore settings, PgDumpRunner pgDump, BackupDestinations destinations,
        IOptions<BackupOptions> options, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<BackupService> logger)
        : base("backup", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _pgDump = pgDump;
        _destinations = destinations;
        _options = options.Value;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override TimeSpan MaxRunDuration =>
        TimeSpan.FromMinutes(_options.PgDumpTimeoutMinutes + _options.UploadTimeoutMinutes * Math.Max(1, _options.UploadAttempts) + 30);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!_recovered)
        {
            await MarkInterruptedRunsAsync(cancellationToken);
            _recovered = true;
        }

        await RunIfDueAsync(cancellationToken);
        return false;
    }

    /// <summary>Runs a backup when one is due. Returns the finished run, or null when nothing was due.</summary>
    public async Task<BackupRun?> RunIfDueAsync(CancellationToken cancellationToken)
    {
        var kind = await DueKindAsync(cancellationToken);
        if (kind is null)
        {
            return null;
        }

        var settings = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken);
        if (settings is null || settings.DestinationType == BackupDestinationType.None)
        {
            if (Time.GetUtcNow() - _lastNotConfiguredLog >= NotConfiguredLogInterval)
            {
                Logger.LogWarning("A backup is due but no backup destination is configured; backups exist on the VPS only. Configure one in Settings, Backups");
                _lastNotConfiguredLog = Time.GetUtcNow();
            }

            return null;
        }

        return await RunBackupAsync(kind.Value, settings, cancellationToken);
    }

    /// <summary>Nightly when the schedule hour has passed today without a nightly run; manual when requested after the last run.</summary>
    internal async Task<BackupKind?> DueKindAsync(CancellationToken cancellationToken)
    {
        var now = Time.GetUtcNow().UtcDateTime;
        var requestedText = await _settings.GetStringAsync(SettingKeys.BackupRequestedAt, cancellationToken);
        var settings = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken);

        await using var db = _dbFactory.CreateSystem();
        var lastStartedAt = await db.BackupRuns.AsNoTracking().OrderByDescending(r => r.StartedAt).Select(r => (DateTime?)r.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (requestedText is not null &&
            DateTime.TryParse(requestedText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var requestedAt) &&
            (lastStartedAt is null || requestedAt > lastStartedAt))
        {
            return BackupKind.Manual;
        }

        var scheduleHour = Math.Clamp(settings?.ScheduleHourUtc ?? 2, 0, 23);
        var scheduledToday = now.Date.AddHours(scheduleHour);
        if (now < scheduledToday)
        {
            return null;
        }

        var ranTonight = await db.BackupRuns.AsNoTracking()
            .AnyAsync(r => r.Kind == BackupKind.Nightly && r.StartedAt >= scheduledToday, cancellationToken);
        return ranTonight ? null : BackupKind.Nightly;
    }

    /// <summary>Runs one backup to the configured destination and records the outcome.</summary>
    public async Task<BackupRun> RunBackupAsync(BackupKind kind, BackupSettings settings, CancellationToken cancellationToken)
    {
        var run = new BackupRun
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            Status = BackupRunStatus.Running,
            StartedAt = Time.GetUtcNow().UtcDateTime
        };
        await using (var db = _dbFactory.CreateSystem())
        {
            db.BackupRuns.Add(run);
            await db.SaveChangesAsync(cancellationToken);
        }

        Logger.LogInformation("{Kind} backup {RunId} started", kind, run.Id);
        var tempDirectory = Path.Combine(string.IsNullOrWhiteSpace(_options.TempDirectory) ? Path.GetTempPath() : _options.TempDirectory, "fleeto-backup");
        var dumpFile = Path.Combine(tempDirectory, $"{run.Id:N}.dump");
        var encryptedFile = dumpFile + ".fbk";

        try
        {
            var problem = BackupDestinations.Validate(settings, out var publicKey);
            if (problem is not null)
            {
                throw new BackupFailureException(problem);
            }

            InstanceInfo instance;
            await using (var db = _dbFactory.CreateSystem())
            {
                instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
            }

            Directory.CreateDirectory(tempDirectory);
            await _pgDump.DumpAsync(dumpFile, cancellationToken);
            await BackupDestinations.EncryptFileAsync(dumpFile, encryptedFile, publicKey, cancellationToken);
            TryDelete(dumpFile);

            var objectKey = BackupDestinations.DumpObjectKey(settings, instance.InstanceId, run.StartedAt);
            var size = new FileInfo(encryptedFile).Length;
            await _destinations.UploadAsync(settings, encryptedFile, objectKey, cancellationToken);

            await using (var db = _dbFactory.CreateSystem())
            {
                var completedAt = Time.GetUtcNow().UtcDateTime;
                await db.BackupRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, BackupRunStatus.Succeeded)
                    .SetProperty(r => r.CompletedAt, completedAt)
                    .SetProperty(r => r.SizeBytes, size)
                    .SetProperty(r => r.ObjectKey, objectKey), CancellationToken.None);
                run.Status = BackupRunStatus.Succeeded;
                run.CompletedAt = completedAt;
                run.SizeBytes = size;
                run.ObjectKey = objectKey;
            }

            Logger.LogInformation("Backup {RunId} succeeded: {Size} bytes to {ObjectKey}", run.Id, size, objectKey);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
        {
            var error = ex is BackupFailureException ? ex.Message : $"The backup failed unexpectedly ({ex.GetType().Name}): {ex.Message}";
            error = error.Length <= 2000 ? error : error[..2000];
            Logger.LogError(ex, "Backup {RunId} failed: {Error}", run.Id, error);
            await RecordFailureAsync(run, error);
        }
        finally
        {
            TryDelete(dumpFile);
            TryDelete(encryptedFile);
        }

        return run;
    }

    /// <summary>Runs left in Running by a stopped process are marked failed, so the page never shows a backup that is not running.</summary>
    internal async Task<int> MarkInterruptedRunsAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        return await db.BackupRuns.Where(r => r.Status == BackupRunStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, BackupRunStatus.Failed)
                .SetProperty(r => r.CompletedAt, now)
                .SetProperty(r => r.Error, InterruptedError), cancellationToken);
    }

    private async Task RecordFailureAsync(BackupRun run, string error)
    {
        try
        {
            await using var db = _dbFactory.CreateSystem();
            await using var transaction = await db.Database.BeginTransactionAsync(CancellationToken.None);
            var completedAt = Time.GetUtcNow().UtcDateTime;
            await db.BackupRuns.Where(r => r.Id == run.Id).ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, BackupRunStatus.Failed)
                .SetProperty(r => r.CompletedAt, completedAt)
                .SetProperty(r => r.Error, error), CancellationToken.None);

            var instance = await InstanceQueries.GetInstanceAsync(db, CancellationToken.None);
            var content = EmailTemplates.BackupFailed(instance.Fqdn, run.StartedAt, error, instance.BackupsUrl);
            foreach (var admin in await InstanceQueries.GetAdminEmailsAsync(db, CancellationToken.None))
            {
                db.OutboxEmails.Add(OutboxEmails.Create(admin, content, OutboxEmails.CategoryBackup, completedAt));
            }

            await db.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);

            run.Status = BackupRunStatus.Failed;
            run.CompletedAt = completedAt;
            run.Error = error;
        }
        catch (Exception ex)
        {
            // The run stays Running and is marked interrupted on the next start.
            Logger.LogError(ex, "Recording the failure of backup {RunId} failed", run.Id);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Logger.LogWarning("Could not delete temporary backup file {Path}: {Error}", path, ex.Message);
        }
    }
}
