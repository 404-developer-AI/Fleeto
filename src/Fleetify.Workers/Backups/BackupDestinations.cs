using System.Globalization;
using System.Text.RegularExpressions;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Fleetify.Infrastructure.Security;
using Fleetify.Infrastructure.Settings;
using Fleetify.Workers.Common;
using Fleetify.Workers.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleetify.Workers.Backups;

/// <summary>
/// Encrypts files with the backup public key and uploads them to the configured destination. S3 uploads use PutObject
/// only, so write-only credentials (no read, no delete) are enough: a compromised VPS cannot read or remove backups.
/// </summary>
public sealed partial class BackupDestinations
{
    private readonly BackupOptions _options;
    private readonly ILogger<BackupDestinations> _logger;

    public BackupDestinations(IOptions<BackupOptions> options, ILogger<BackupDestinations> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Object key of a database dump: <c>&lt;prefix&gt;/&lt;instance id&gt;/&lt;yyyy&gt;/&lt;MM&gt;/fleetify-&lt;UTC timestamp&gt;.dump.fbk</c>.</summary>
    public static string DumpObjectKey(BackupSettings settings, Guid instanceId, DateTime startedAtUtc) =>
        JoinKey(Prefix(settings), instanceId.ToString("D"), startedAtUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            startedAtUtc.ToString("MM", CultureInfo.InvariantCulture),
            $"fleetify-{startedAtUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.dump.fbk");

    /// <summary>Object key of an archived WAL file: <c>&lt;prefix&gt;/&lt;instance id&gt;/wal/&lt;file name&gt;.fbk</c>.</summary>
    public static string WalObjectKey(BackupSettings settings, Guid instanceId, string walFileName) =>
        JoinKey(Prefix(settings), instanceId.ToString("D"), "wal", walFileName + ".fbk");

    /// <summary>WAL, history and backup label names only: no path separators can reach an object key or a path.</summary>
    public static bool IsValidWalFileName(string name) => WalFileName().IsMatch(name);

    /// <summary>Validates the settings needed to upload. Returns a problem (cause and next step) or null.</summary>
    public static string? Validate(BackupSettings settings, out byte[] publicKey)
    {
        if (!BackupCipher.TryDecodePublicKey(settings.PublicKey, out publicKey))
        {
            return "The backup public key is missing or not valid. Paste the key from 'fleetify-tool backup keygen' in Settings, Backups.";
        }

        return settings.DestinationType switch
        {
            BackupDestinationType.S3 when string.IsNullOrWhiteSpace(settings.S3Bucket) || string.IsNullOrWhiteSpace(settings.S3AccessKeyId) ||
                                          string.IsNullOrWhiteSpace(settings.S3SecretAccessKey) ||
                                          (string.IsNullOrWhiteSpace(settings.S3Endpoint) && string.IsNullOrWhiteSpace(settings.S3Region)) =>
                "The S3 destination is incomplete. Enter the bucket, region or endpoint, and the access key in Settings, Backups.",
            BackupDestinationType.Directory when string.IsNullOrWhiteSpace(settings.DirectoryPath) =>
                "The backup directory is not set. Enter it in Settings, Backups.",
            BackupDestinationType.None => "No backup destination is configured. Configure one in Settings, Backups.",
            _ => null
        };
    }

    /// <summary>Encrypts <paramref name="plainFile"/> into <paramref name="encryptedFile"/> with the backup public key.</summary>
    public static async Task EncryptFileAsync(string plainFile, string encryptedFile, byte[] publicKey, CancellationToken cancellationToken)
    {
        await using var input = new FileStream(plainFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var output = new FileStream(encryptedFile, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous);
        await BackupCipher.EncryptAsync(input, output, publicKey, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    /// <summary>Uploads with retries and backoff. Throws <see cref="BackupFailureException"/> when every attempt failed.</summary>
    public async Task UploadAsync(BackupSettings settings, string localFile, string objectKey, CancellationToken cancellationToken)
    {
        try
        {
            await Retry.WithBackoffAsync(async ct =>
                {
                    await UploadOnceAsync(settings, localFile, objectKey, ct);
                    return true;
                },
                Math.Max(1, _options.UploadAttempts), TimeSpan.FromSeconds(10),
                (ex, attempt) => _logger.LogWarning("Upload of {ObjectKey} failed (attempt {Attempt}): {Error}", objectKey, attempt, Describe(ex)),
                cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            throw new BackupFailureException($"Uploading the backup failed after {Math.Max(1, _options.UploadAttempts)} attempt(s): {Describe(ex)}", ex);
        }
    }

    private async Task UploadOnceAsync(BackupSettings settings, string localFile, string objectKey, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.UploadTimeoutMinutes)));

        switch (settings.DestinationType)
        {
            case BackupDestinationType.S3:
                await UploadToS3Async(settings, localFile, objectKey, timeout.Token);
                break;
            case BackupDestinationType.Directory:
                await CopyToDirectoryAsync(settings.DirectoryPath!, localFile, objectKey, timeout.Token);
                break;
            default:
                throw new BackupFailureException("No backup destination is configured. Configure one in Settings, Backups.");
        }
    }

    private async Task UploadToS3Async(BackupSettings settings, string localFile, string objectKey, CancellationToken cancellationToken)
    {
        var config = new AmazonS3Config
        {
            // Path-style works with every S3-compatible provider; virtual-host style needs DNS per bucket.
            ForcePathStyle = true,
            Timeout = TimeSpan.FromMinutes(Math.Max(1, _options.UploadTimeoutMinutes)),
            MaxErrorRetry = 0, // Retries are ours, with backoff and logging.
            // Several S3-compatible providers reject the SDK's default trailing checksums.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };

        if (!string.IsNullOrWhiteSpace(settings.S3Endpoint))
        {
            config.ServiceURL = settings.S3Endpoint;
            if (!string.IsNullOrWhiteSpace(settings.S3Region))
            {
                config.AuthenticationRegion = settings.S3Region;
            }
        }
        else
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.S3Region);
        }

        using var client = new AmazonS3Client(new BasicAWSCredentials(settings.S3AccessKeyId, settings.S3SecretAccessKey), config);
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.S3Bucket,
            Key = objectKey,
            FilePath = localFile,
            ContentType = "application/octet-stream",
            UseChunkEncoding = false
        }, cancellationToken);
    }

    private static async Task CopyToDirectoryAsync(string directory, string localFile, string objectKey, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(Environment.ExpandEnvironmentVariables(directory));
        var target = Path.GetFullPath(Path.Combine(root, objectKey.Replace('/', Path.DirectorySeparatorChar)));
        if (!target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new BackupFailureException("The backup object key leaves the backup directory. Check the prefix in Settings, Backups.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var partial = target + ".partial";
        await using (var input = new FileStream(localFile, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.Asynchronous))
        await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, FileOptions.Asynchronous))
        {
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }

        // The final name appears only when the copy is complete.
        File.Move(partial, target, overwrite: false);
    }

    private static string Prefix(BackupSettings settings) =>
        settings.DestinationType == BackupDestinationType.S3 ? (settings.S3Prefix ?? string.Empty).Trim().Trim('/') : string.Empty;

    private static string JoinKey(params string[] parts) => string.Join('/', parts.Where(p => !string.IsNullOrEmpty(p)));

    /// <summary>Error text without credentials: the SDK never includes the secret key in messages.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        BackupFailureException failure => failure.Message,
        AmazonS3Exception s3 => $"the storage returned {(int)s3.StatusCode} {s3.ErrorCode}: {s3.Message}",
        OperationCanceledException => "the upload did not finish in time",
        _ => ex.Message
    };

    [GeneratedRegex("^[0-9A-Za-z._-]{1,128}$")]
    private static partial Regex WalFileName();
}
