using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Services;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Security;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Web.Services;

public sealed record RunnableScript(Guid Id, string Name, ScriptLanguage Language, Guid? ClientId, string? ClientCode, int VersionNumber, bool Approved);

public sealed record JobRunTarget(Guid EndpointId, string Hostname, string Problem);

/// <param name="Skipped">Endpoints no job was created for, with the reason.</param>
/// <param name="AdminsNotified">Admins emailed because the run was above the notice threshold (0.2.1).</param>
public sealed record JobRunResult(Guid BatchId, int Created, IReadOnlyList<JobRunTarget> Skipped, int AdminsNotified = 0);

public sealed record JobListItem(Guid Id, Guid BatchId, Guid EndpointId, string Hostname, string? ClientCode, string ScriptName, int ScriptVersionNumber,
    ScriptLanguage Language, JobState State, JobResult? Result, int? ExitCode, string? Problem, JobOutputState OutputState, bool OutputTruncated,
    long OutputBytes, DateTime CreatedAt, DateTime ValidUntil, DateTime? StartedAt, DateTime? CompletedAt, string InitiatedByName, bool CanCancel,
    JobRunAs RunAs = JobRunAs.Service, string? RunAsAccount = null, string? RunAsChosenAccount = null);

/// <summary>The users signed in on one endpoint as its agent last reported them (0.2.2), for choosing the user a script runs as.</summary>
public sealed record SignedInUsersView(IReadOnlyList<SignedInUserInfo> Users, DateTime? ReportedAt, bool EndpointOnline);

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

    /// <summary>Host names named in the admin notice; the rest is counted.</summary>
    private const int NamedHostsInNotice = 10;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly LicenseService _licenses;
    private readonly INotificationBus _bus;
    private readonly SettingsStore _settings;
    private readonly TimeProvider _time;

    public JobService(IFleetoDbContextFactory dbFactory, LicenseService licenses, INotificationBus bus, SettingsStore settings, TimeProvider time)
    {
        _dbFactory = dbFactory;
        _licenses = licenses;
        _bus = bus;
        _settings = settings;
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
    /// reason; when none remains, nothing is created. With <paramref name="allSignedInUsers"/> (0.2.2) an endpoint gets one job per
    /// user its agent last reported, each signed for that user, so every user has their own result and output.
    /// </summary>
    public async Task<ServiceResult<JobRunResult>> RunAsync(Caller caller, Guid scriptId, IReadOnlyCollection<Guid> endpointIds, TimeSpan validity,
        JobRunAs runAs = JobRunAs.Service, string? runAsUserId = null, bool allSignedInUsers = false, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return ServiceResult<JobRunResult>.Forbidden();
        }

        if (!Enum.IsDefined(runAs))
        {
            return ServiceResult<JobRunResult>.Fail("Choose where the script runs.");
        }

        if (runAsUserId is not null && (runAs != JobRunAs.LoggedOnUser || !SignedInUserRules.IsValidUserId(runAsUserId)))
        {
            return ServiceResult<JobRunResult>.Fail("A user can only be chosen when the script runs as the signed-in user.");
        }

        if (runAsUserId is not null && endpointIds.Distinct().Count() != 1)
        {
            return ServiceResult<JobRunResult>.Fail("A user can only be chosen when the script runs on one endpoint. Run it on each endpoint to choose a user there.");
        }

        if (allSignedInUsers && (runAs != JobRunAs.LoggedOnUser || runAsUserId is not null))
        {
            return ServiceResult<JobRunResult>.Fail("All signed-in users can only be chosen when the script runs as the signed-in user, without a chosen user.");
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

        var endpoints = await (
                from e in db.Endpoints.AsNoTracking().Where(e => ids.Contains(e.Id))
                join ep in EffectivePolicies.Query(db) on e.Id equals ep.EndpointId
                select new
                {
                    e.Id, e.ClientId, e.Hostname, e.Tier, e.OsPlatform, e.Source, e.AgentVersion, e.SignedInUsersJson,
                    ApprovalRequired = db.Policies.IgnoreQueryFilters().Where(p => p.Id == ep.PolicyId).Select(p => (bool?)p.ScriptApprovalRequired).FirstOrDefault() == true,
                    // The signer reads the cap again when it signs; this is the value the job starts with.
                    MaxOutputBytes = db.Policies.IgnoreQueryFilters().Where(p => p.Id == ep.PolicyId).Select(p => (long?)p.MaxOutputBytes).FirstOrDefault()
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
            var chooseUser = runAsUserId is not null || allSignedInUsers;
            var reported = chooseUser ? SignedInUserRules.Parse(endpoint.SignedInUsersJson) : [];

            // The chosen user must be one the agent reported: the signer and the agent check the rest.
            var chosen = runAsUserId is null ? null : reported.FirstOrDefault(u => string.Equals(u.Id, runAsUserId, StringComparison.OrdinalIgnoreCase));
            var problem = TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed ? "Not managed. Switch it to managed to run scripts."
                : !ScriptLanguages.RunsOn(script.Language, endpoint.OsPlatform) ? $"{ScriptLanguages.Label(script.Language)} does not run on this operating system."
                : script.ClientId is { } scriptClient && scriptClient != endpoint.ClientId ? "The script belongs to another client."
                : endpoint.ApprovalRequired && !version.IsApproved ? "The site's policy requires an approved script. Ask a second admin to approve this version."
                : chooseUser && !SignedInUserRules.AgentSupportsChosenUser(endpoint.AgentVersion)
                    ? "The agent is too old to run a script as a chosen user. Run it as the signed-in user, or wait until the agent runs Fleeto 0.2.2."
                : runAsUserId is not null && chosen is null
                    ? "The chosen user is no longer signed in on this endpoint, as far as the agent reported. Open Run script again to see who is signed in."
                : allSignedInUsers && reported.Count == 0
                    ? "Nobody was signed in when the agent last reported."
                : null;
            if (problem is not null)
            {
                skipped.Add(new JobRunTarget(endpoint.Id, endpoint.Hostname, problem));
                continue;
            }

            // For all signed-in users one job per user, sorted by account so a run reads the same every time; otherwise one job.
            IEnumerable<SignedInUserInfo?> users = allSignedInUsers
                ? reported.DistinctBy(u => u.Id, StringComparer.OrdinalIgnoreCase).OrderBy(u => u.Account, StringComparer.OrdinalIgnoreCase)
                : [chosen];
            foreach (var user in users)
            {
                if (created.Count == ScriptRules.MaxJobsPerRun)
                {
                    return ServiceResult<JobRunResult>.Fail(
                        $"This run would start more than {ScriptRules.MaxJobsPerRun} jobs, one per signed-in user. Choose fewer endpoints.");
                }

                var job = new Job
                {
                    Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = batchId, Type = JobType.Script,
                    ScriptId = script.Id, ScriptVersionId = version.Id, ScriptName = script.Name, ScriptVersionNumber = version.Number, Language = script.Language,
                    ScriptSha256 = version.Sha256, TimeoutSeconds = version.TimeoutSeconds,
                    MaxOutputBytes = ScriptRules.OutputCap(endpoint.MaxOutputBytes ?? ScriptRules.DefaultMaxOutputBytes), RunAs = runAs,
                    RunAsUserId = user?.Id, RunAsChosenAccount = user?.Account,
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
                    endpoint.Hostname, EndpointId = endpoint.Id, BatchId = batchId, job.ScriptName, job.ScriptVersionNumber, job.ScriptSha256, job.ValidUntil,
                    RunAs = job.RunAs.ToString(), RunAsUser = job.RunAsChosenAccount, AllSignedInUsers = allSignedInUsers
                }), now));
            }
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

        // Above the threshold every admin is told, in the same transaction as the jobs: the notice cannot be lost once the run exists.
        var notified = await AddAdminNoticeAsync(db, caller, created, skipped.Count, now, cancellationToken);
        if (created.Count > 1)
        {
            db.AuditEntries.Add(AuditLog.ToEntry(caller.Audit(AuditActions.JobBatchStarted, "Job", batchId.ToString(), null, new
            {
                BatchId = batchId, script.Name, VersionNumber = version.Number, Endpoints = created.Select(j => j.EndpointId).Distinct().Count(),
                Jobs = created.Count, Skipped = skipped.Count, AdminsNotified = notified, RunAs = runAs.ToString(), AllSignedInUsers = allSignedInUsers
            }), now));
        }

        await db.SaveChangesAsync(cancellationToken);
        await PublishAsync(created.Select(j => j.EndpointId), cancellationToken);
        return ServiceResult<JobRunResult>.Ok(new JobRunResult(batchId, created.Count, skipped, notified));
    }

    /// <summary>
    /// The users signed in on one endpoint as its agent last reported them (0.2.2), for the run window. Null when the endpoint does not exist
    /// for the caller or its agent cannot run a script as a chosen user.
    /// </summary>
    public async Task<SignedInUsersView?> GetSignedInUsersAsync(Caller caller, Guid endpointId, CancellationToken cancellationToken = default)
    {
        if (!caller.CanManage)
        {
            return null;
        }

        await using var db = _dbFactory.Create(caller.Scope);
        var endpoint = await db.Endpoints.AsNoTracking().Where(e => e.Id == endpointId)
            .Select(e => new { e.AgentVersion, e.SignedInUsersJson, e.SignedInUsersAt, e.IsOnline })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null || !SignedInUserRules.AgentSupportsChosenUser(endpoint.AgentVersion) || endpoint.SignedInUsersAt is null)
        {
            return null;
        }

        return new SignedInUsersView(SignedInUserRules.Parse(endpoint.SignedInUsersJson), endpoint.SignedInUsersAt, endpoint.IsOnline);
    }

    /// <summary>
    /// Queues an email to every admin when a run is above the threshold of Settings, Scripts. The rows are added to the caller's
    /// context, not saved: they are committed with the jobs themselves.
    /// </summary>
    private async Task<int> AddAdminNoticeAsync(FleetoDbContext db, Caller caller, IReadOnlyList<Job> created, int skippedCount, DateTime now,
        CancellationToken cancellationToken)
    {
        // The threshold counts endpoints: a run for all signed-in users has several jobs per endpoint.
        var endpointIds = created.Select(j => j.EndpointId).Distinct().ToList();
        var threshold = await GetAdminNoticeAboveAsync(cancellationToken);
        if (threshold <= 0 || endpointIds.Count <= threshold)
        {
            return 0;
        }

        var admins = await InstanceQueries.GetAdminEmailsAsync(db, cancellationToken);
        if (admins.Count == 0)
        {
            return 0;
        }

        var instance = await InstanceQueries.GetInstanceAsync(db, cancellationToken);
        var hostnames = await db.Endpoints.AsNoTracking()
            .Where(e => endpointIds.Contains(e.Id))
            .OrderBy(e => e.Hostname).Select(e => e.Hostname).Take(NamedHostsInNotice)
            .ToListAsync(cancellationToken);
        var first = created[0];
        var content = EmailTemplates.ScriptRunOnManyEndpoints(instance.Fqdn, new ScriptRunEmailModel(
            caller.Name, first.ScriptName, first.ScriptVersionNumber, endpointIds.Count, skippedCount, hostnames, now, threshold), instance.AuditLogUrl);
        foreach (var admin in admins)
        {
            db.OutboxEmails.Add(OutboxEmails.Create(admin, content, OutboxEmails.CategoryJob, now));
        }

        return admins.Count;
    }

    /// <summary>The endpoint count above which a run emails every admin; 0 never notifies.</summary>
    public async Task<int> GetAdminNoticeAboveAsync(CancellationToken cancellationToken = default)
    {
        return ScriptRules.AdminNoticeAbove(await _settings.GetStringAsync(SettingKeys.JobAdminNoticeAbove, cancellationToken));
    }

    /// <summary>The jobs of one run, newest endpoint state first seen by host name, for the run window (0.2.1).</summary>
    public async Task<IReadOnlyList<JobListItem>> ListForBatchAsync(Caller caller, Guid batchId, CancellationToken cancellationToken = default)
    {
        caller.EnsureView();
        await using var db = _dbFactory.Create(caller.Scope);
        var jobs = await Project(db, db.Jobs.AsNoTracking().Where(j => j.BatchId == batchId).OrderBy(j => j.CreatedAt).Take(ScriptRules.MaxJobsPerRun))
            .ToListAsync(cancellationToken);
        // The host name comes from the projection, so a run is sorted by endpoint here rather than in SQL; a batch holds at most 500 jobs.
        return jobs.OrderBy(j => j.Hostname, StringComparer.OrdinalIgnoreCase).ThenBy(j => j.RunAsChosenAccount, StringComparer.OrdinalIgnoreCase).ToList();
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
            (j.State == JobState.PendingSignature || j.State == JobState.Queued) && j.DeliveredAt == null, j.RunAs, j.RunAsAccount, j.RunAsChosenAccount));

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
