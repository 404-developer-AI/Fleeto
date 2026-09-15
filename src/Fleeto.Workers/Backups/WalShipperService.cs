using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Common;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleeto.Workers.Backups;

/// <summary>
/// Continuous WAL archiving: PostgreSQL's <c>archive_command</c> on the VPS writes completed WAL files into
/// <see cref="BackupOptions.WalSpoolDirectory"/> (atomically: copy to a temporary name, then rename). This loop encrypts
/// each file, uploads it to the backup destination and deletes it. A file is deleted only after a successful upload, so
/// a failed upload is retried on the next pass. A circuit breaker pauses shipping after repeated failures.
/// </summary>
public sealed class WalShipperService : WorkerLoop
{
    internal const int FilesPerPass = 100;
    private static readonly TimeSpan MinimumFileAge = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ProblemLogInterval = TimeSpan.FromHours(1);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly SettingsStore _settings;
    private readonly BackupDestinations _destinations;
    private readonly BackupOptions _options;
    private readonly CircuitBreaker _breaker;
    private DateTimeOffset _lastProblemLog = DateTimeOffset.MinValue;

    public WalShipperService(IFleetoDbContextFactory dbFactory, SettingsStore settings, BackupDestinations destinations,
        IOptions<BackupOptions> options, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<WalShipperService> logger)
        : base("wal-shipper", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _settings = settings;
        _destinations = destinations;
        _options = options.Value;
        _breaker = new CircuitBreaker(3, TimeSpan.FromMinutes(5), time);
    }

    protected override TimeSpan Interval => TimeSpan.FromSeconds(30);

    protected override TimeSpan MaxRunDuration =>
        TimeSpan.FromMinutes(_options.UploadTimeoutMinutes * Math.Max(1, _options.UploadAttempts) + 10);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        await ShipPendingAsync(cancellationToken) >= FilesPerPass;

    /// <summary>Ships up to <see cref="FilesPerPass"/> WAL files. Returns the number shipped.</summary>
    public async Task<int> ShipPendingAsync(CancellationToken cancellationToken)
    {
        var spool = _options.WalSpoolDirectory;
        if (string.IsNullOrWhiteSpace(spool) || !Directory.Exists(spool) || _breaker.IsOpen)
        {
            return 0;
        }

        var cutoff = Time.GetUtcNow().UtcDateTime - MinimumFileAge;
        var files = new DirectoryInfo(spool).EnumerateFiles()
            .Where(f => BackupDestinations.IsValidWalFileName(f.Name) &&
                        !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) &&
                        !f.Name.EndsWith(".partial", StringComparison.OrdinalIgnoreCase) &&
                        f.LastWriteTimeUtc < cutoff)
            .OrderBy(f => f.Name, StringComparer.Ordinal)
            .Take(FilesPerPass)
            .ToList();
        if (files.Count == 0)
        {
            return 0;
        }

        var settings = await _settings.GetAsync<BackupSettings>(SettingKeys.Backup, cancellationToken) ?? new BackupSettings();
        var problem = BackupDestinations.Validate(settings, out var publicKey);
        if (problem is not null)
        {
            if (Time.GetUtcNow() - _lastProblemLog >= ProblemLogInterval)
            {
                Logger.LogWarning("{Count} WAL file(s) are waiting and cannot be shipped: {Problem}", files.Count, problem);
                _lastProblemLog = Time.GetUtcNow();
            }

            return 0;
        }

        InstanceInfo instance;
        await using (var db = _dbFactory.CreateSystem())
        {
            instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        }

        var tempDirectory = Path.Combine(string.IsNullOrWhiteSpace(_options.TempDirectory) ? Path.GetTempPath() : _options.TempDirectory, "fleeto-wal");
        Directory.CreateDirectory(tempDirectory);

        var shipped = 0;
        foreach (var file in files)
        {
            var encrypted = Path.Combine(tempDirectory, $"{Guid.NewGuid():N}.fbk");
            try
            {
                await BackupDestinations.EncryptFileAsync(file.FullName, encrypted, publicKey, cancellationToken);
                await _destinations.UploadAsync(settings, encrypted, BackupDestinations.WalObjectKey(settings, instance.InstanceId, file.Name), cancellationToken);
                file.Delete();
                _breaker.RecordSuccess();
                shipped++;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && cancellationToken.IsCancellationRequested))
            {
                Logger.LogError("Shipping WAL file {File} failed: {Error}", file.Name, ex.Message);
                if (_breaker.RecordFailure())
                {
                    Logger.LogWarning("WAL shipping failed repeatedly; pausing for 5 minutes");
                }

                break;
            }
            finally
            {
                try
                {
                    File.Delete(encrypted);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Logger.LogWarning("Could not delete temporary file {Path}: {Error}", encrypted, ex.Message);
                }
            }
        }

        return shipped;
    }
}
