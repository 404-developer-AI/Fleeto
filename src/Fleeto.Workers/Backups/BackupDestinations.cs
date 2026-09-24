using System.Globalization;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Common;
using Fleeto.Workers.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Fleeto.Workers.Backups;

/// <summary>
/// Encrypts files with the backup public key and uploads them to the configured destination. S3 uploads need
/// <c>s3:PutObject</c> only, so write-only credentials (no read, no delete) are enough: a compromised VPS cannot read or remove
/// backups. A file above <see cref="MultipartThreshold"/> goes up in parts (0.6.0), since one PutObject stops at 5 GB; the
/// parts of the multipart API fall under the same permission.
/// </summary>
public sealed class BackupDestinations
{
    /// <summary>S3 accepts at most this many parts per upload.</summary>
    public const int MaxParts = 10_000;

    /// <summary>S3 needs every part but the last to be at least this large.</summary>
    public const long MinPartSize = 5L * 1024 * 1024;

    private readonly BackupOptions _options;
    private readonly ILogger<BackupDestinations> _logger;
    private readonly Func<BackupSettings, AmazonS3Config, IAmazonS3> _createS3;

    public BackupDestinations(IOptions<BackupOptions> options, ILogger<BackupDestinations> logger)
        : this(options, logger, (settings, config) => new AmazonS3Client(new BasicAWSCredentials(settings.S3AccessKeyId, settings.S3SecretAccessKey), config))
    {
    }

    /// <param name="createS3">Only for tests: the S3 client for the settings and configuration.</param>
    internal BackupDestinations(IOptions<BackupOptions> options, ILogger<BackupDestinations> logger, Func<BackupSettings, AmazonS3Config, IAmazonS3> createS3)
    {
        _options = options.Value;
        _logger = logger;
        _createS3 = createS3;
    }

    /// <summary>A file larger than this is uploaded to S3 in parts.</summary>
    public long MultipartThreshold { get; internal set; } = 128L * 1024 * 1024;

    /// <summary>The smallest part of a multipart upload; larger when the file would need more than <see cref="MaxParts"/>.</summary>
    public long PartSize { get; internal set; } = 64L * 1024 * 1024;

    /// <summary>
    /// The size of every part but the last, and how many parts a file of <paramref name="fileSize"/> bytes needs: at least
    /// <paramref name="partSize"/>, and large enough to stay within <see cref="MaxParts"/>.
    /// </summary>
    public static (long PartSize, int Parts) PlanParts(long fileSize, long partSize)
    {
        var size = Math.Max(Math.Max(partSize, MinPartSize), (fileSize + MaxParts - 1) / MaxParts);
        return (size, (int)Math.Max(1, (fileSize + size - 1) / size));
    }

    /// <summary>Object key of a database dump: <c>&lt;prefix&gt;/&lt;instance id&gt;/&lt;yyyy&gt;/&lt;MM&gt;/fleeto-&lt;UTC timestamp&gt;.dump.fbk</c>.</summary>
    public static string DumpObjectKey(BackupSettings settings, Guid instanceId, DateTime startedAtUtc) =>
        JoinKey(Prefix(settings), instanceId.ToString("D"), startedAtUtc.ToString("yyyy", CultureInfo.InvariantCulture),
            startedAtUtc.ToString("MM", CultureInfo.InvariantCulture),
            $"fleeto-{startedAtUtc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)}.dump.fbk");

    /// <summary>Validates the settings needed to upload. Returns a problem (cause and next step) or null.</summary>
    public static string? Validate(BackupSettings settings, out byte[] publicKey)
    {
        if (!BackupCipher.TryDecodePublicKey(settings.PublicKey, out publicKey))
        {
            return "The backup public key is missing or not valid. Paste the key from 'fleeto-tool backup keygen' in Settings, Backups.";
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
            case BackupDestinationType.S3 when new FileInfo(localFile).Length > MultipartThreshold:
                // Each part has its own attempts and time limit: a large dump must not start over, or run out of time, as a whole.
                await UploadToS3InPartsAsync(settings, localFile, objectKey, cancellationToken);
                break;
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

    private AmazonS3Config S3Config(BackupSettings settings)
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

        return config;
    }

    private async Task UploadToS3Async(BackupSettings settings, string localFile, string objectKey, CancellationToken cancellationToken)
    {
        using var client = _createS3(settings, S3Config(settings));
        await client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = settings.S3Bucket,
            Key = objectKey,
            FilePath = localFile,
            ContentType = "application/octet-stream",
            UseChunkEncoding = false
        }, cancellationToken);
    }

    private async Task UploadToS3InPartsAsync(BackupSettings settings, string localFile, string objectKey, CancellationToken cancellationToken)
    {
        var fileSize = new FileInfo(localFile).Length;
        var (partSize, parts) = PlanParts(fileSize, PartSize);
        using var client = _createS3(settings, S3Config(settings));
        var upload = await client.InitiateMultipartUploadAsync(new InitiateMultipartUploadRequest
        {
            BucketName = settings.S3Bucket,
            Key = objectKey,
            ContentType = "application/octet-stream"
        }, cancellationToken);

        try
        {
            var etags = new List<PartETag>(parts);
            for (var number = 1; number <= parts; number++)
            {
                var position = (number - 1) * partSize;
                var size = Math.Min(partSize, fileSize - position);
                var part = number;
                var response = await Retry.WithBackoffAsync(async ct =>
                    {
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Max(1, _options.UploadTimeoutMinutes)));
                        return await client.UploadPartAsync(new UploadPartRequest
                        {
                            BucketName = settings.S3Bucket,
                            Key = objectKey,
                            UploadId = upload.UploadId,
                            PartNumber = part,
                            FilePath = localFile,
                            FilePosition = position,
                            PartSize = size,
                            UseChunkEncoding = false
                        }, timeout.Token);
                    },
                    Math.Max(1, _options.UploadAttempts), TimeSpan.FromSeconds(10),
                    (ex, attempt) => _logger.LogWarning("Part {Part} of {Parts} of {ObjectKey} failed (attempt {Attempt}): {Error}", part, parts, objectKey, attempt, Describe(ex)),
                    cancellationToken);
                etags.Add(new PartETag(part, response.ETag));
            }

            await client.CompleteMultipartUploadAsync(new CompleteMultipartUploadRequest
            {
                BucketName = settings.S3Bucket,
                Key = objectKey,
                UploadId = upload.UploadId,
                PartETags = etags
            }, cancellationToken);
            _logger.LogInformation("Uploaded {ObjectKey} in {Parts} parts ({Bytes} bytes)", objectKey, parts, fileSize);
        }
        catch
        {
            await AbortQuietlyAsync(client, settings, objectKey, upload.UploadId);
            throw;
        }
    }

    /// <summary>
    /// Removes the parts of a failed upload when the credentials allow it. Write-only credentials usually do not, so a failure
    /// here is expected: a lifecycle rule on the bucket removes incomplete uploads (Settings, Backups says so).
    /// </summary>
    private async Task AbortQuietlyAsync(IAmazonS3 client, BackupSettings settings, string objectKey, string uploadId)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await client.AbortMultipartUploadAsync(new AbortMultipartUploadRequest
            {
                BucketName = settings.S3Bucket,
                Key = objectKey,
                UploadId = uploadId
            }, timeout.Token);
        }
        catch (Exception ex)
        {
            _logger.LogInformation("The parts of the failed upload of {ObjectKey} stay until the lifecycle rule of the bucket removes them: {Error}",
                objectKey, Describe(ex));
        }
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
}
