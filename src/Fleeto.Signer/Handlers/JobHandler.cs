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
    public const string AgentPlatformReason = "The Action1 agent can only be installed on a Windows endpoint from Fleeto. Install it on a Linux endpoint from the Action1 console.";
    public const string AgentInstallerReason =
        "There is no Action1 agent installer link for this client. Add it in Settings, Integrations, next to the organization of this client.";
    public const string AgentInstallerChangedReason =
        "The Action1 agent installer link of this client changed after the job was started. Start the installation again.";
    public const string AgentRemovalPlatformReason = "Fleeto removes the Action1 agent from Windows endpoints only. Remove it on a Linux endpoint from the Action1 console.";
    public const string AgentRemovalChangedReason = "The job does not match the removal Fleeto composes. Start the removal again.";
    public const string AutomaticTypeReason = "Fleeto starts only the installation of the Action1 agent by itself. This job has no initiator.";
    public const string AccessReason = "The user who started the job no longer has access to this endpoint. Ask an admin to add it to a group that has.";
    public const string AutomaticPolicyReason =
        "The patch policy of this endpoint no longer installs the Action1 agent automatically, so Fleeto did not start the installation.";
    public const string AutomaticRemovedReason =
        "The Action1 agent was removed from this endpoint through Fleeto, so Fleeto does not install it again by itself. Install it by hand on the Patches tab.";

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

        if (job.Type is not (JobType.Script or JobType.Action1Agent or JobType.Action1AgentRemoval) || job.ValidUntil <= now ||
            job.ValidUntil > job.CreatedAt + ScriptRules.MaxValidity + TimeSpan.FromMinutes(5) ||
            job.ValidUntil > now + ScriptRules.MaxValidity + TimeSpan.FromMinutes(5))
        {
            return SigningOutcome.Refused(ValidityReason);
        }

        // An installation of the Action1 agent that Fleeto starts by itself (0.6.0) has no user behind it: instead of the
        // initiator's role, the patch policy of the endpoint has to ask for it, checked further down. No other job runs
        // without a user.
        var automatic = job.InitiatedByUserId == Action1AgentInstall.AutomaticInitiator;
        if (automatic && job.Type != JobType.Action1Agent)
        {
            return SigningOutcome.Refused(AutomaticTypeReason);
        }

        if (!automatic && !await HasRoleAsync(db, job.InitiatedByUserId, now, cancellationToken, FleetoRoleNames.Admin, FleetoRoleNames.Technician))
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

        // The groups of the initiator must still give it the endpoint (0.6.0): web filters on them, the signer checks again.
        if (!automatic && !await UserAccessResolver.MayReachAsync(db, job.InitiatedByUserId, job.ClientId, endpoint.SiteId, endpoint.Id, cancellationToken))
        {
            return SigningOutcome.Refused(AccessReason);
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
            if (automatic && await AutomaticRefusalAsync(db, job, cancellationToken) is { } refusal)
            {
                return SigningOutcome.Refused(refusal);
            }

            return await SignAgentInstallAsync(db, job, endpoint.Id, endpoint.Hostname, endpoint.OsPlatform, now, cancellationToken);
        }

        // The job that removes the Action1 agent (0.6.0) has a fixed body the signer writes; nothing about it comes from web.
        if (job.Type == JobType.Action1AgentRemoval)
        {
            return await SignAgentRemovalAsync(db, job, endpoint.Id, endpoint.Hostname, endpoint.OsPlatform, now, cancellationToken);
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

        var policy = await EffectivePolicies.LoadAsync(db, endpoint.Id, cancellationToken);
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
    /// web can pick the endpoint but never what happens on it. Windows only, because Fleeto does not put the Action1 agent
    /// on a Linux endpoint yet, and script approval does not apply: there is no script to approve.
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

        // The link itself is not a secret, but it names the customer's organization, so only its host is recorded.
        return await SignComposedAsync(db, job, endpointId, hostname, Action1AgentInstall.JobName, body, sha256, Action1AgentInstall.TimeoutSeconds,
            Action1AgentInstall.MaxOutputBytes, new Uri(installerUrl!).Host, now, cancellationToken);
    }

    /// <summary>
    /// Why an installation Fleeto started by itself may not be signed (0.6.0), or null: the patch policy that applies to the
    /// endpoint now must still ask for it, and the agent must not have been removed through Fleeto since. The workers
    /// decided the same when they wrote the job; this is the signer deciding again, independently of them.
    /// </summary>
    private static async Task<string?> AutomaticRefusalAsync(Infrastructure.Data.FleetoDbContext db, Job job,
        CancellationToken cancellationToken)
    {
        var policyId = await EffectivePolicies.Query(db).IgnoreQueryFilters()
            .Where(p => p.EndpointId == job.EndpointId)
            .Select(p => p.PatchPolicyId)
            .FirstOrDefaultAsync(cancellationToken);
        var asks = policyId is { } id && await db.PatchPolicies.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(p => p.Id == id && p.AutoInstallAgent, cancellationToken);
        if (!asks)
        {
            return AutomaticPolicyReason;
        }

        var removed = await db.Action1AgentInstallStates.IgnoreQueryFilters().AsNoTracking()
            .AnyAsync(a => a.EndpointId == job.EndpointId && a.Status == Action1AgentStatus.Removed, cancellationToken);
        return removed ? AutomaticRemovedReason : null;
    }

    /// <summary>
    /// Signs the job that uninstalls the Action1 agent (0.6.0). The body is <see cref="Action1AgentRemoval"/> and has no
    /// input at all, so web can pick the endpoint but never what runs. Windows only, like the install; script approval
    /// does not apply, as there is no script to approve.
    /// </summary>
    private async Task<SigningOutcome> SignAgentRemovalAsync(Infrastructure.Data.FleetoDbContext db, Job job, Guid endpointId,
        string hostname, string osPlatform, DateTime now, CancellationToken cancellationToken)
    {
        if (!ScriptLanguages.RunsOn(ScriptLanguage.PowerShell, osPlatform))
        {
            return SigningOutcome.Refused(AgentRemovalPlatformReason);
        }

        var body = Action1AgentRemoval.Body();
        var sha256 = ScriptLanguages.Sha256(body);
        if (!SecureCompare.HexEquals(sha256, job.ScriptSha256))
        {
            return SigningOutcome.Refused(AgentRemovalChangedReason);
        }

        return await SignComposedAsync(db, job, endpointId, hostname, Action1AgentRemoval.JobName, body, sha256, Action1AgentRemoval.TimeoutSeconds,
            Action1AgentRemoval.MaxOutputBytes, null, now, cancellationToken);
    }

    /// <summary>Signs a job whose PowerShell body the signer composed itself, runs it as the service, and audits it.</summary>
    private async Task<SigningOutcome> SignComposedAsync(Infrastructure.Data.FleetoDbContext db, Job job, Guid endpointId, string hostname,
        string name, string body, string sha256, int timeoutSeconds, long maxOutputBytes, string? installerHost, DateTime now,
        CancellationToken cancellationToken)
    {
        var payload = new JobPayload
        {
            JobId = job.Id.ToString("D"),
            InstanceId = _keyRing.InstanceId.ToString("D"),
            EndpointId = endpointId.ToString("D"),
            Type = Protocol.Agent.V1.JobType.Script,
            ValidUntil = Timestamp.FromDateTime(DateTime.SpecifyKind(job.ValidUntil, DateTimeKind.Utc)),
            InitiatedBy = job.InitiatedByName,
            TimeoutSeconds = (uint)timeoutSeconds,
            MaxOutputBytes = (ulong)maxOutputBytes,
            RunAs = Protocol.Agent.V1.JobRunAs.Service,
            RunAsUserId = string.Empty,
            Script = new ScriptJob
            {
                Language = AgentConfigBuilder.ToProto(ScriptLanguage.PowerShell),
                Name = name,
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
        job.ScriptName = name;
        job.TimeoutSeconds = timeoutSeconds;
        job.MaxOutputBytes = maxOutputBytes;
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
                InstallerHost = installerHost
            }), now, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        _logger.LogInformation("Signed the job {Name} {JobId} for endpoint {EndpointId}", name, job.Id, endpointId);
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
