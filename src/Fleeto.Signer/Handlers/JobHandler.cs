using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Services;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Keys;
using Fleeto.Signer.Processing;
using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using JobType = Fleeto.Core.Entities.JobType;
using ScriptLanguage = Fleeto.Core.Entities.ScriptLanguage;

namespace Fleeto.Signer.Handlers;

/// <summary>
/// Signs one job (0.2.0, ARCHITECTURE.md §4, Job and §5, The signer). Everything is decided from the database, independently of
/// web: the initiator still exists, is not locked out and is an admin or technician; the endpoint is managed (license included)
/// and runs the script's platform; the script version belongs to the job's client or is global and its body still has the hash
/// the job names; when the site's policy requires approval the version is the script's current one and was approved by another
/// admin for exactly this body; the validity window is within 7 days. The signed payload carries the body the signer read.
/// </summary>
public sealed class JobHandler : ISigningRequestHandler
{
    public const string MissingJobReason = "The job no longer exists.";
    public const string InitiatorReason =
        "The technician who started the job no longer exists, is locked out or has no admin or technician role. Ask an admin to start it again.";
    public const string NotManagedReason = "The endpoint is not managed. Switch it to managed before running scripts.";
    public const string PlatformReason = "The script's language does not run on this endpoint's operating system.";
    public const string ScriptChangedReason = "The script version no longer matches the job. Start the job again.";
    public const string ScriptClientReason = "The script belongs to another client.";
    public const string ApprovalReason =
        "The site's policy requires approval of scripts, and this version of the script is not the current version approved by a second admin. Ask an admin to approve it.";
    public const string ChosenUserReason = "The chosen user is invalid: a user can only be chosen for a script that runs as the signed-in user.";
    public const string ChosenUserAgentReason =
        "The agent of this endpoint is too old to run a script as a chosen user. Wait until it runs Fleeto 0.2.2 or later, or run it as the signed-in user.";
    public const string ValidityReason = "The job's validity window is invalid or has passed. Start the job again with a validity of at most 7 days.";
    public const string AgentPlatformReason = "The Action1 agent can only be installed on a Windows endpoint from Fleeto. Linux follows in 0.4.1.";
    public const string AgentInstallerReason =
        "There is no Action1 agent installer link for this client. Add it in Settings, Integrations, next to the organization of this client.";
    public const string AgentInstallerChangedReason =
        "The Action1 agent installer link of this client changed after the job was started. Start the installation again.";

    private readonly SignerKeyRing _keyRing;
    private readonly LicenseService _licenses;
    private readonly ILogger<JobHandler> _logger;

    public JobHandler(SignerKeyRing keyRing, LicenseService licenses, ILogger<JobHandler> logger)
    {
        _keyRing = keyRing;
        _licenses = licenses;
        _logger = logger;
    }

    public SigningRequestKind Kind => SigningRequestKind.Job;

    public async Task<SigningOutcome> HandleAsync(SigningContext context, CancellationToken cancellationToken)
    {
        var db = context.Db;
        var now = context.Now;
        if (context.Request.SubjectId is not { } jobId)
        {
            return SigningOutcome.Refused(MissingJobReason);
        }

        var jobs = await db.Jobs.FromSql($"""SELECT * FROM "Jobs" WHERE "Id" = {jobId} FOR UPDATE""").IgnoreQueryFilters().ToListAsync(cancellationToken);
        if (jobs.Count == 0 || jobs[0].ClientId != context.Request.ClientId)
        {
            return SigningOutcome.Refused(MissingJobReason);
        }

        var job = jobs[0];
        if (job.State != JobState.PendingSignature)
        {
            // Cancelled or already handled: nothing to sign and nothing wrong.
            return SigningOutcome.Completed(null);
        }

        if (job.Type is not (JobType.Script or JobType.Action1Agent) || job.ValidUntil <= now ||
            job.ValidUntil > job.CreatedAt + ScriptRules.MaxValidity + TimeSpan.FromMinutes(5) ||
            job.ValidUntil > now + ScriptRules.MaxValidity + TimeSpan.FromMinutes(5))
        {
            return SigningOutcome.Refused(ValidityReason);
        }

        if (!await HasRoleAsync(db, job.InitiatedByUserId, now, cancellationToken, FleetoRoleNames.Admin, FleetoRoleNames.Technician))
        {
            return SigningOutcome.Refused(InitiatorReason);
        }

        var endpoint = await db.Endpoints.IgnoreQueryFilters().AsNoTracking()
            .Where(e => e.Id == job.EndpointId && e.ClientId == job.ClientId)
            .Select(e => new { e.Id, e.SiteId, e.Tier, e.OsPlatform, e.Hostname, e.AgentVersion })
            .SingleOrDefaultAsync(cancellationToken);
        if (endpoint is null)
        {
            return SigningOutcome.Refused(MissingJobReason);
        }

        // Tier enforcement, layer 2 (the signer): the stored tier and the license must both allow managed behaviour.
        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        if (TierRules.EffectiveTier(endpoint.Tier, license) != EndpointTier.Managed)
        {
            return SigningOutcome.Refused(NotManagedReason);
        }

        // The job that installs the Action1 agent carries no script: the signer writes the body itself from the installer
        // link of the client's organization, so nothing a user can edit decides what runs (0.4.0 step 3).
        if (job.Type == JobType.Action1Agent)
        {
            return await SignAgentInstallAsync(db, job, endpoint.Id, endpoint.Hostname, endpoint.OsPlatform, now, cancellationToken);
        }

        var version = job.ScriptVersionId is { } versionId
            ? await db.ScriptVersions.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(v => v.Id == versionId, cancellationToken)
            : null;
        var script = version is null
            ? null
            : await db.Scripts.IgnoreQueryFilters().AsNoTracking().SingleOrDefaultAsync(s => s.Id == version.ScriptId, cancellationToken);
        if (version is null || script is null || script.Id != job.ScriptId || version.ClientId != script.ClientId ||
            ScriptLanguages.Sha256(version.Body) != version.Sha256 || !SecureCompare.HexEquals(version.Sha256, job.ScriptSha256) ||
            script.Language != job.Language)
        {
            return SigningOutcome.Refused(ScriptChangedReason);
        }

        if (script.ClientId is { } scriptClient && scriptClient != job.ClientId)
        {
            return SigningOutcome.Refused(ScriptClientReason);
        }

        if (!ScriptLanguages.RunsOn(script.Language, endpoint.OsPlatform))
        {
            return SigningOutcome.Refused(PlatformReason);
        }

        var policy = await db.SitePolicies.IgnoreQueryFilters().AsNoTracking()
                         .Where(l => l.SiteId == endpoint.SiteId)
                         .Select(l => l.Policy)
                         .FirstOrDefaultAsync(cancellationToken)
                     ?? await db.Policies.IgnoreQueryFilters().AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault, cancellationToken);
        if (policy?.ScriptApprovalRequired == true &&
            (script.CurrentVersionId != version.Id || !version.IsApproved ||
             !await HasRoleAsync(db, version.ApprovedByUserId!.Value, null, cancellationToken, FleetoRoleNames.Admin)))
        {
            return SigningOutcome.Refused(ApprovalReason);
        }

        // A chosen user (0.2.2) only with run as the signed-in user, as a SID or uid, and only for an agent that honours it: an older agent
        // would ignore the choice and run the script as whichever user is signed in.
        if (job.RunAsUserId is not null)
        {
            if (job.RunAs != Core.Entities.JobRunAs.LoggedOnUser || !SignedInUserRules.IsValidUserId(job.RunAsUserId))
            {
                return SigningOutcome.Refused(ChosenUserReason);
            }

            if (!SignedInUserRules.AgentSupportsChosenUser(endpoint.AgentVersion))
            {
                return SigningOutcome.Refused(ChosenUserAgentReason);
            }
        }

        var timeout = Math.Clamp(version.TimeoutSeconds, ScriptRules.MinTimeoutSeconds, ScriptRules.MaxTimeoutSeconds);
        // The output cap comes from the effective policy, not from the job row web wrote: the signer is the authority.
        var maxOutput = ScriptRules.OutputCap(policy?.MaxOutputBytes ?? ScriptRules.DefaultMaxOutputBytes);
        var payload = new JobPayload
        {
            JobId = job.Id.ToString("D"),
            InstanceId = _keyRing.InstanceId.ToString("D"),
            EndpointId = endpoint.Id.ToString("D"),
            Type = Protocol.Agent.V1.JobType.Script,
            ValidUntil = Timestamp.FromDateTime(DateTime.SpecifyKind(job.ValidUntil, DateTimeKind.Utc)),
            InitiatedBy = job.InitiatedByName,
            TimeoutSeconds = (uint)timeout,
            MaxOutputBytes = (ulong)maxOutput,
            RunAs = job.RunAs == Core.Entities.JobRunAs.LoggedOnUser ? Protocol.Agent.V1.JobRunAs.LoggedOnUser : Protocol.Agent.V1.JobRunAs.Service,
            RunAsUserId = job.RunAsUserId ?? string.Empty,
            Script = new ScriptJob
            {
                Language = AgentConfigBuilder.ToProto(script.Language),
                Name = script.Name,
                Version = (uint)version.Number,
                Body = version.Body,
                Sha256 = version.Sha256
            }
        }.ToByteArray();

        job.Payload = payload;
        job.Signature = _keyRing.Sign(SignatureContexts.Job, payload);
        job.SigningKeyId = _keyRing.SigningKeyId;
        job.SignedAt = now;
        job.TimeoutSeconds = timeout;
        job.MaxOutputBytes = maxOutput;
        job.State = JobState.Queued;

        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.JobSigned, "Job", job.Id.ToString(), job.ClientId, AuditActorType.System,
            "fleeto-signer", "fleeto-signer",
            new
            {
                endpoint.Hostname,
                EndpointId = endpoint.Id,
                job.ScriptName,
                job.ScriptVersionNumber,
                job.ScriptSha256,
                job.ValidUntil,
                InitiatedBy = job.InitiatedByName,
                ApprovalRequired = policy?.ScriptApprovalRequired == true,
                RunAs = job.RunAs.ToString()
            }), now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Signed job {JobId} ({Script} v{Version}) for endpoint {EndpointId}", job.Id, job.ScriptName, job.ScriptVersionNumber, endpoint.Id);
        return SigningOutcome.Completed(null, new PendingNotification(NotificationChannels.Jobs, endpoint.Id.ToString()));
    }

    /// <summary>
    /// Signs the job that installs the Action1 agent (0.4.0 step 3). Everything it runs comes from the signer: the body
    /// from <see cref="Action1AgentInstall"/> and the download link from the integration mapping of the job's client, so
    /// web can pick the endpoint but never what happens on it. Windows only, because Action1 has no Linux agent in Fleeto
    /// until 0.4.1, and script approval does not apply: there is no script to approve.
    /// </summary>
    private async Task<SigningOutcome> SignAgentInstallAsync(Infrastructure.Data.FleetoDbContext db, Job job, Guid endpointId,
        string hostname, string osPlatform, DateTime now, CancellationToken cancellationToken)
    {
        if (!ScriptLanguages.RunsOn(ScriptLanguage.PowerShell, osPlatform))
        {
            return SigningOutcome.Refused(AgentPlatformReason);
        }

        var installerUrl = await db.IntegrationMappings.IgnoreQueryFilters().AsNoTracking()
            .Where(m => m.ClientId == job.ClientId)
            .Select(m => m.AgentInstallerUrl)
            .FirstOrDefaultAsync(cancellationToken);
        if (!Action1AgentInstall.IsValidInstallerUrl(installerUrl))
        {
            return SigningOutcome.Refused(AgentInstallerReason);
        }

        var body = Action1AgentInstall.Body(installerUrl!);
        var sha256 = ScriptLanguages.Sha256(body);
        // The technician asked to install from the link as it was then. An admin who changes it in between changes what
        // would run, so the job is refused instead of signed over something nobody chose.
        if (!SecureCompare.HexEquals(sha256, job.ScriptSha256))
        {
            return SigningOutcome.Refused(AgentInstallerChangedReason);
        }

        var payload = new JobPayload
        {
            JobId = job.Id.ToString("D"),
            InstanceId = _keyRing.InstanceId.ToString("D"),
            EndpointId = endpointId.ToString("D"),
            Type = Protocol.Agent.V1.JobType.Script,
            ValidUntil = Timestamp.FromDateTime(DateTime.SpecifyKind(job.ValidUntil, DateTimeKind.Utc)),
            InitiatedBy = job.InitiatedByName,
            TimeoutSeconds = Action1AgentInstall.TimeoutSeconds,
            MaxOutputBytes = Action1AgentInstall.MaxOutputBytes,
            RunAs = Protocol.Agent.V1.JobRunAs.Service,
            RunAsUserId = string.Empty,
            Script = new ScriptJob
            {
                Language = AgentConfigBuilder.ToProto(ScriptLanguage.PowerShell),
                Name = Action1AgentInstall.JobName,
                Version = 1,
                Body = body,
                Sha256 = sha256
            }
        }.ToByteArray();

        job.Payload = payload;
        job.Signature = _keyRing.Sign(SignatureContexts.Job, payload);
        job.SigningKeyId = _keyRing.SigningKeyId;
        job.SignedAt = now;
        // Language and the body hash are part of what was requested and cannot change any more (TR_Jobs_Binding).
        job.ScriptName = Action1AgentInstall.JobName;
        job.TimeoutSeconds = Action1AgentInstall.TimeoutSeconds;
        job.MaxOutputBytes = Action1AgentInstall.MaxOutputBytes;
        job.State = JobState.Queued;

        await SignerAudit.WriteAsync(db, new AuditRecord(AuditActions.JobSigned, "Job", job.Id.ToString(), job.ClientId, AuditActorType.System,
            "fleeto-signer", "fleeto-signer",
            new
            {
                Hostname = hostname,
                EndpointId = endpointId,
                job.ScriptName,
                job.ScriptSha256,
                job.ValidUntil,
                InitiatedBy = job.InitiatedByName,
                // The link itself is not a secret, but it names the customer's organization, so only its host is recorded.
                InstallerHost = new Uri(installerUrl!).Host
            }), now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Signed the Action1 agent install job {JobId} for endpoint {EndpointId}", job.Id, endpointId);
        return SigningOutcome.Completed(null, new PendingNotification(NotificationChannels.Jobs, endpointId.ToString()));
    }

    /// <summary>Marks the job refused with the signer's reason, after the handler's own writes were rolled back.</summary>
    public async Task<IReadOnlyList<PendingNotification>> OnRefusedAsync(SigningContext context, string reason, CancellationToken cancellationToken)
    {
        if (context.Request.SubjectId is not { } jobId)
        {
            return [];
        }

        var reasonText = reason.Length > 500 ? reason[..500] : reason;
        var endpointIds = await context.Db.Jobs.IgnoreQueryFilters()
            .Where(j => j.Id == jobId && j.ClientId == context.Request.ClientId && j.State == JobState.PendingSignature)
            .Select(j => j.EndpointId)
            .ToListAsync(cancellationToken);
        await context.Db.Jobs.IgnoreQueryFilters()
            .Where(j => j.Id == jobId && j.ClientId == context.Request.ClientId && j.State == JobState.PendingSignature)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Refused)
                .SetProperty(j => j.RefusalReason, reasonText)
                .SetProperty(j => j.CompletedAt, context.Now), cancellationToken);
        return endpointIds.Select(id => new PendingNotification(NotificationChannels.Jobs, id.ToString())).ToList();
    }

    private static Task<bool> HasRoleAsync(Infrastructure.Data.FleetoDbContext db, Guid userId, DateTime? lockedOutAt,
        CancellationToken cancellationToken, params string[] normalizedRoles) =>
        ScriptCheckResolver.UserHasRoleAsync(db, userId, lockedOutAt, cancellationToken, normalizedRoles);
}

/// <summary>Normalized role names as stored by ASP.NET Core Identity.</summary>
internal static class FleetoRoleNames
{
    public static readonly string Admin = ScriptCheckResolver.AdminRole;
    public static readonly string Technician = ScriptCheckResolver.TechnicianRole;
}
