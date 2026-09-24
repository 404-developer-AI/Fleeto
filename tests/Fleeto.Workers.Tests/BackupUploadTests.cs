using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Backups;
using Fleeto.Workers.Options;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Uploads to S3 (0.6.0): a small file goes up in one PutObject, a large one as a multipart upload whose parts put the file
/// back together byte for byte, within the 10,000 parts S3 allows; a failed multipart upload is aborted and reported.
/// </summary>
public sealed class BackupUploadTests : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("fleeto-upload-").FullName;

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private static readonly BackupSettings S3 = new()
    {
        DestinationType = BackupDestinationType.S3, S3Bucket = "backups", S3Region = "eu-central-1", S3AccessKeyId = "write-only",
        S3SecretAccessKey = "secret"
    };

    /// <summary>An S3 that keeps what it receives in memory. Only the calls a backup makes are answered.</summary>
    private sealed class FakeS3 : AmazonS3Client
    {
        public FakeS3()
            : base(new AnonymousAWSCredentials(), new AmazonS3Config { ServiceURL = "http://s3.test.invalid" })
        {
        }

        public Dictionary<string, byte[]> Objects { get; } = [];
        public Dictionary<int, byte[]> Parts { get; } = [];
        public int PutObjects { get; private set; }
        public List<string> Aborted { get; } = [];
        public int FailPart { get; set; }

        public override Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            PutObjects++;
            Objects[request.Key] = File.ReadAllBytes(request.FilePath);
            return Task.FromResult(new PutObjectResponse { HttpStatusCode = HttpStatusCode.OK });
        }

        public override Task<InitiateMultipartUploadResponse> InitiateMultipartUploadAsync(InitiateMultipartUploadRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(new InitiateMultipartUploadResponse { UploadId = "upload-1", Key = request.Key, BucketName = request.BucketName });

        public override async Task<UploadPartResponse> UploadPartAsync(UploadPartRequest request, CancellationToken cancellationToken = default)
        {
            if (request.PartNumber == FailPart)
            {
                throw new AmazonS3Exception("The part was refused.") { StatusCode = HttpStatusCode.InternalServerError };
            }

            await using var file = File.OpenRead(request.FilePath);
            file.Position = request.FilePosition ?? 0;
            var bytes = new byte[request.PartSize ?? 0];
            await file.ReadExactlyAsync(bytes, cancellationToken);
            Parts[request.PartNumber ?? 0] = bytes;
            return new UploadPartResponse { ETag = $"\"etag-{request.PartNumber}\"", PartNumber = request.PartNumber };
        }

        public override Task<CompleteMultipartUploadResponse> CompleteMultipartUploadAsync(CompleteMultipartUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(Enumerable.Range(1, Parts.Count), request.PartETags.Select(p => p.PartNumber ?? 0));
            Assert.All(request.PartETags, p => Assert.Equal($"\"etag-{p.PartNumber}\"", p.ETag));
            Objects[request.Key] = Parts.OrderBy(p => p.Key).SelectMany(p => p.Value).ToArray();
            return Task.FromResult(new CompleteMultipartUploadResponse { Key = request.Key });
        }

        public override Task<AbortMultipartUploadResponse> AbortMultipartUploadAsync(AbortMultipartUploadRequest request,
            CancellationToken cancellationToken = default)
        {
            Aborted.Add(request.UploadId);
            return Task.FromResult(new AbortMultipartUploadResponse());
        }
    }

    private (BackupDestinations Destinations, FakeS3 S3) Create()
    {
        var s3 = new FakeS3();
        var destinations = new BackupDestinations(MsOptions.Create(new BackupOptions { UploadAttempts = 1 }), NullLogger<BackupDestinations>.Instance,
            (_, _) => s3)
        {
            MultipartThreshold = 1024 * 1024,
            PartSize = BackupDestinations.MinPartSize
        };
        return (destinations, s3);
    }

    private string CreateFile(int bytes)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        var content = new byte[bytes];
        new Random(bytes).NextBytes(content);
        File.WriteAllBytes(path, content);
        return path;
    }

    [Fact]
    public void Parts_are_at_least_the_part_size_and_never_more_than_S3_allows()
    {
        const long mb = 1024 * 1024;
        Assert.Equal((64 * mb, 160), BackupDestinations.PlanParts(10_240 * mb, 64 * mb));
        Assert.Equal((64 * mb, 1), BackupDestinations.PlanParts(1, 64 * mb));
        Assert.Equal((BackupDestinations.MinPartSize, 3), BackupDestinations.PlanParts(11 * mb, mb));

        // A file too large for 10,000 parts of 64 MB gets larger parts rather than failing.
        var terabyte = 1024L * 1024 * mb;
        var (size, parts) = BackupDestinations.PlanParts(terabyte, 64 * mb);
        Assert.True(size > 64 * mb);
        Assert.True(parts <= BackupDestinations.MaxParts);
        Assert.True(size * parts >= terabyte);
    }

    [Fact]
    public async Task A_small_file_goes_up_in_one_request()
    {
        var (destinations, s3) = Create();
        var file = CreateFile(100_000);

        await destinations.UploadAsync(S3, file, "fleeto/small.dump.fbk", CancellationToken.None);

        Assert.Equal(1, s3.PutObjects);
        Assert.Empty(s3.Parts);
        Assert.Equal(File.ReadAllBytes(file), s3.Objects["fleeto/small.dump.fbk"]);
    }

    [Fact]
    public async Task A_large_file_goes_up_in_parts_that_add_up_to_the_file()
    {
        var (destinations, s3) = Create();
        var file = CreateFile(12 * 1024 * 1024 + 123);

        await destinations.UploadAsync(S3, file, "fleeto/large.dump.fbk", CancellationToken.None);

        Assert.Equal(0, s3.PutObjects);
        Assert.Equal(3, s3.Parts.Count);
        Assert.Equal(BackupDestinations.MinPartSize, s3.Parts[1].Length);
        Assert.Equal(File.ReadAllBytes(file), s3.Objects["fleeto/large.dump.fbk"]);
        Assert.Empty(s3.Aborted);
    }

    [Fact]
    public async Task A_failed_multipart_upload_is_aborted_and_reported()
    {
        var (destinations, s3) = Create();
        s3.FailPart = 2;

        var failure = await Assert.ThrowsAsync<BackupFailureException>(() =>
            destinations.UploadAsync(S3, CreateFile(12 * 1024 * 1024), "fleeto/failed.dump.fbk", CancellationToken.None));

        Assert.Contains("500", failure.Message);
        Assert.Equal(["upload-1"], s3.Aborted);
        Assert.False(s3.Objects.ContainsKey("fleeto/failed.dump.fbk"));
    }
}
