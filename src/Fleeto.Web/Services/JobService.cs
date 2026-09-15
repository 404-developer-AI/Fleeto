using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record RunnableScript(Guid Id, string Name, ScriptLanguage Language, Guid? ClientId, string? ClientCode, int VersionNumber, bool Approved);

public sealed record JobRunTarget(Guid EndpointId, string Hostname, string Problem);

/// <param name="Skipped">Endpoints no job was created for, with the reason.</param>
public sealed record JobRunResult(Guid BatchId, int Created, IReadOnlyList<JobRunTarget> Skipped);

public sealed record JobListItem(Guid Id, Guid BatchId, Guid EndpointId, string Hostname, string? ClientCode, string ScriptName, int ScriptVersionNumber,
    ScriptLanguage Language, JobState State, JobResult? Result, int? ExitCode, string? Problem, JobOutputState OutputState, bool OutputTruncated,
    long OutputBytes, DateTime CreatedAt, DateTime ValidUntil, DateTime? StartedAt, DateTime? CompletedAt, string InitiatedByName, bool CanCancel);

/// <param name="Stdout">Decoded text of the first <see cref="JobService.MaxViewBytes"/> of the stream.</param>
public sealed record JobOutputView(JobListItem Job, string Stdout, long StdoutBytes, string Stderr, long StderrBytes, bool ShortenedInView);

/// <summary>
/// Jobs in web (0.2.0): starting a library script on managed endpoints, the job history and output. Web checks what it can for a
/// clear answer; fleeto-signer decides again before anything is signed (ARCHITECTURE.md §4, Job).
/// </summary>
public sealed class JobService
{
    /// <summary>Output shown per stream in the UI; the rest stays stored.</summary>
    public const int MaxViewBytes = 1024 * 1024;

    public static readonly IReadOnlyList<(TimeSpan Validity, string Label)> Validities =
    [
        (TimeSpan.FromHours(1), "1 hour"),
        (TimeSpan.FromHours(24), "24 hours"),
        (TimeSpan.FromDays(7), "7 days")
    ];

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly TimeProvider _time;

    public JobService(IFleetoDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _time = time;
    }

    /// <summary>Scripts with a current version that can run on every given endpoint: the right platform and the client or global.</summary>
    public async Task<IReadOnlyList<RunnableScript>> ListRunnableScriptsAsync(Caller caller, IReadOnlyCollection<Guid> endpointIds,
        CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var ids = endpointIds.Distinct().ToList();
        var endpoints = await db.Endpoints.AsNoTracking().Where(e => ids.Contains(e.Id)).Select(e => new { e.ClientId, e.OsPlatform }).ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            return [];
        }

        var clients = endpoints.Select(e => e.ClientId).Distinct().ToList();
        var platforms = endpoints.Select(e => e.OsPlatform).Distinct().ToList();
        var scripts = await db.Scripts.AsNoTracking()
            .Where(s => s.CurrentVersionId != null && (s.ClientId == null || (clients.Count == 1 && s.ClientId == clients[0])))
            .OrderBy(s => s.Name)
            .Select(s => new
            {
                s.Id, s.Name, s.Language, s.ClientId,
                ClientCode = db.Clients.Where(c => c.Id == s.ClientId).Select(c => c.Code).FirstOrDefault(),
                Version = db.ScriptVersions.Where(v => v.Id == s.CurrentVersionId).FirstOrDefault()
            })
            .ToListAsync(cancellationToken);
        return scripts.Where(s => s.Version is not null && platforms.All(p => ScriptLanguages.RunsOn(s.Language, p)))
            .Select(s => new RunnableScript(s.Id, s.Name, s.Language, s.ClientId, s.ClientCode, s.Version!.Number, s.Version.IsApproved))
            .ToList();
    }

    /// <summary>
    /// Creates one job per endpoint for the script's current version and asks fleeto-signer to sign them. Endpoints that cannot
    /// run it (not managed, another platform or client, or an approval policy with an unapproved version) are skipped with the
    /// reason; when none remains, nothing is created.
    /// </summary>
    public async Task<ServiceResult<JobRunResult>> RunAsync(Caller caller, Guid scriptId, IReadOnlyCollection<Guid> endpointIds, TimeSpan validity,
        CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<JobRunResult>.Forbidden();
        }

        if (Validities.All(v => v.Validity != validity))
        {
            return ServiceResult<JobRunResult>.Fail("Choose a validity of 1 hour, 24 hours or 7 days.");
        }

        var ids = endpointIds.Distinct().ToList();
        if (ids.Count == 0 || ids.Count > ScriptRules.MaxEndpointsPerRun)
        {
            return ServiceResult<JobRunResult>.Fail($"Choose between 1 and {ScriptRules.MaxEndpointsPerRun} endpoints.");
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var script = await db.Scripts.AsNoTracking().SingleOrDefaultAsync(s => s.Id == scriptId, cancellationToken);
        var version = script?.CurrentVersionId is { } versionId
            ? await db.ScriptVersions.AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            : null;
        if (script is null || version is null)
        {
            return ServiceResult<JobRunResult>.NotFound("script");
        }

        var endpoints = await db.Endpoints.AsNoTracking().Where(e => ids.Contains(e.Id))
            .Select(e => new
            {
                e.Id, e.ClientId, e.Hostname, e.Tier, e.OsPlatform, e.Source,
                ApprovalRequired = (db.SitePolicies.Where(l => l.SiteId == e.SiteId).Select(l => (bool?)l.Policy!.ScriptApprovalRequired).FirstOrDefault() ??
                                    db.Policies.Where(p => p.IsDefault).Select(p => (bool?)p.ScriptApprovalRequired).FirstOrDefault()) == true
            })
            .ToListAsync(cancellationToken);
        if (endpoints.Count == 0)
        {
            return ServiceResult<JobRunResult>.NotFound("endpoint");
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;
        var batchId = Guid.NewGuid();
        var skipped = new List<JobRunTarget>();
        var created = new List<Job>();
        foreach (var endpoint in endpoints.OrderBy(e => e.Hostname))
        {
            var problem = TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed ? "Not managed. Switch it to managed to run scripts."
                : !ScriptLanguages.RunsOn(script.Language, endpoint.OsPlatform) ? $"{ScriptLanguages.Label(script.Language)} does not run on this operating system."
                : script.ClientId is { } scriptClient && scriptClient != endpoint.ClientId ? "The script belongs to another client."
                : endpoint.ApprovalRequired && !version.IsApproved ? "The site's policy requires an approved script. Ask a second admin to approve this version."
                : null;
            if (problem is not null)
            {
                skipped.Add(new JobRunTarget(endpoint.Id, endpoint.Hostname, problem));
                continue;
            }

            var job = new Job
            {
                Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = batchId, Type = JobType.Script,
                ScriptId = script.Id, ScriptVersionId = version.Id, ScriptName = script.Name, ScriptVersionNumber = version.Number, Language = script.Language,
                ScriptSha256 = version.Sha256, TimeoutSeconds = version.TimeoutSeconds, MaxOutputBytes = ScriptRules.MaxOutputBytes,
                CreatedAt = now, ValidUntil = now + validity, InitiatedByUserId = caller.UserId,
                InitiatedByName = caller.Name.Length > 200 ? caller.Name[..200] : caller.Name
            };
            created.Add(job);
            db.Jobs.Add(job);
            db.SigningRequests.Add(new SigningRequest
            {
                Id = Guid.NewGuid(), ClientId = endpoint.ClientId, Kind = SigningRequestKind.Job, SubjectId = job.Id, Payload = [],
                RequestedBy = "web:" + (caller.IpAddress ?? "unknown"), CreatedAt = now
            });
            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.JobCreated, "Job", job.Id.ToString(), endpoint.ClientId, new
            {
                endpoint.Hostname, EndpointId = endpoint.Id, BatchId = batchId, job.ScriptName, job.ScriptVersionNumber, job.ScriptSha256, job.ValidUntil
            }), now));
        }

        foreach (var missing in ids.Except(endpoints.Select(e => e.Id)))
        {
            skipped.Add(new JobRunTarget(missing, "Unknown endpoint", "The endpoint no longer exists."));
        }

        if (created.Count == 0)
        {
            return ServiceResult<JobRunResult>.Fail(skipped.Count == 1
                ? $"No job was started: {skipped[0].Problem}"
                : $"No job was started. None of the endpoints can run this script, for example {skipped[0].Hostname}: {skipped[0].Problem}");
        }

        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(created.Select(j => j.EndpointId), cancellationToken);
        return ServiceResult<JobRunResult>.Ok(new JobRunResult(batchId, created.Count, skipped));
    }

    public async Task<IReadOnlyList<JobListItem>> ListForEndpointAsync(Caller caller, Guid endpointId, int limit = 50, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await Project(db, db.Jobs.AsNoTracking().Where(j => j.EndpointId == endpointId).OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit, 1, 200)))
            .ToListAsync(cancellationToken);
    }

    /// <summary>The most recent jobs the caller can see, for the dashboard.</summary>
    public async Task<IReadOnlyList<JobListItem>> ListRecentAsync(Caller caller, int limit = 10, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        return await Project(db, db.Jobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).Take(Math.Clamp(limit, 1, 50))).ToListAsync(cancellationToken);
    }

    public async Task<JobOutputView?> GetOutputAsync(Caller caller, Guid jobId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var job = await Project(db, db.Jobs.AsNoTracking().Where(j => j.Id == jobId)).SingleOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return null;
        }

        var (stdout, stdoutBytes, stdoutShort) = await ReadStreamAsync(db, jobId, JobStream.Stdout, cancellationToken);
        var (stderr, stderrBytes, stderrShort) = await ReadStreamAsync(db, jobId, JobStream.Stderr, cancellationToken);
        return new JobOutputView(job, stdout, stdoutBytes, stderr, stderrBytes, stdoutShort || stderrShort);
    }

    /// <summary>Cancels a job that has not been delivered to its agent yet.</summary>
    public async Task<ServiceResult> CancelAsync(Caller caller, Guid jobId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult.Forbidden();
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var job = await db.Jobs.AsNoTracking().Where(j => j.Id == jobId).Select(j => new { j.Id, j.ClientId, j.EndpointId, j.ScriptName }).SingleOrDefaultAsync(cancellationToken);
        if (job is null)
        {
            return ServiceResult.NotFound("job");
        }

        var now = _time.GetUtcNow().UtcDateTime;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var cancelled = await db.Jobs.Where(j => j.Id == jobId && (j.State == JobState.PendingSignature || j.State == JobState.Queued) && j.DeliveredAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.State, JobState.Cancelled).SetProperty(j => j.CompletedAt, now), cancellationToken);
        if (cancelled == 0)
        {
            return ServiceResult.Fail("The job was already delivered to the endpoint or has ended, so it can no longer be cancelled.");
        }

        db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.JobCancelled, "Job", job.Id.ToString(), job.ClientId, new { job.ScriptName, job.EndpointId }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        await PublishAsync([job.EndpointId], cancellationToken);
        return ServiceResult.Ok();
    }

    private static IQueryable<JobListItem> Project(FleetoDbContext db, IQueryable<Job> jobs) =>
        jobs.Select(j => new JobListItem(j.Id, j.BatchId, j.EndpointId,
            db.Endpoints.Where(e => e.Id == j.EndpointId).Select(e => e.Hostname).FirstOrDefault() ?? string.Empty,
            db.Clients.Where(c => c.Id == j.ClientId).Select(c => c.Code).FirstOrDefault(),
            j.ScriptName, j.ScriptVersionNumber, j.Language, j.State, j.Result, j.ExitCode, j.RefusalReason ?? j.Error, j.OutputState, j.OutputTruncated,
            j.ReceivedOutputBytes, j.CreatedAt, j.ValidUntil, j.StartedAt, j.CompletedAt, j.InitiatedByName,
            (j.State == JobState.PendingSignature || j.State == JobState.Queued) && j.DeliveredAt == null));

    private static async Task<(string Text, long Bytes, bool Shortened)> ReadStreamAsync(FleetoDbContext db, Guid jobId, JobStream stream,
        CancellationToken cancellationToken)
    {
        var total = await db.JobOutputChunks.Where(c => c.JobId == jobId && c.Stream == stream).SumAsync(c => (long)c.Data.Length, cancellationToken);
        var buffer = new MemoryStream();
        long sequence = 0;
        while (buffer.Length < MaxViewBytes)
        {
            var chunks = await db.JobOutputChunks.AsNoTracking()
                .Where(c => c.JobId == jobId && c.Stream == stream && c.Sequence >= sequence)
                .OrderBy(c => c.Sequence).Take(16)
                .Select(c => new { c.Sequence, c.Data })
                .ToListAsync(cancellationToken);
            if (chunks.Count == 0)
            {
                break;
            }

            foreach (var chunk in chunks)
            {
                if (chunk.Sequence != sequence)
                {
                    // A gap: show what is contiguous.
                    return (Decode(buffer), total, true);
                }

                buffer.Write(chunk.Data, 0, (int)Math.Min(chunk.Data.Length, MaxViewBytes - buffer.Length));
                sequence++;
                if (buffer.Length >= MaxViewBytes)
                {
                    break;
                }
            }
        }

        return (Decode(buffer), total, buffer.Length < total);
    }

    /// <summary>UTF-8 with replacement characters; a Windows console code page shows its non-ASCII characters as replacements.</summary>
    private static string Decode(MemoryStream buffer) => new UTF8Encoding(false, false).GetString(buffer.GetBuffer(), 0, (int)buffer.Length);

    private async Task PublishAsync(IEnumerable<Guid> endpointIds, CancellationToken cancellationToken)
    {
        foreach (var endpointId in endpointIds.Distinct())
        {
            try
            {
                await _bus.PublishAsync(NotificationChannels.Jobs, endpointId.ToString(), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Pages refresh on their next load; the signer is woken by the signing request trigger.
            }
        }
    }
}
