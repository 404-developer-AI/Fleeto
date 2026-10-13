using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Keeps the Action1 agent of every managed Windows endpoint of a mapped client in view (0.6.0), and installs it by itself
/// where the endpoint's patch policy asks for it.
/// <para>
/// Per endpoint it decides one <see cref="Action1AgentStatus"/>: installed once Action1 reports the endpoint, which is
/// the only proof that patch management covers it; installing while a job of type <see cref="JobType.Action1Agent"/>
/// runs, while the Fleeto agent has to report the new agent, or while Action1 has to report the endpoint; failed with the
/// reason when one of those does not happen. An installation Fleeto starts is an ordinary signed job without a user
/// (<see cref="Action1AgentInstall.AutomaticInitiator"/>): fleeto-signer signs it only while the patch policy of the
/// endpoint still asks for it. A failed automatic installation is tried again after 1, 6 and 24 hours, then Fleeto stops
/// until a technician installs the agent by hand, which also starts the count again. An agent removed through Fleeto is
/// not installed again by itself.
/// </para>
/// <para>
/// The "Action1 agent" checks of an endpoint get their result here: 1 when installed, 0 with the reason otherwise, nothing
/// while an installation runs, and no result at all once the agent was removed through Fleeto.
/// </para>
/// </summary>
public sealed class Action1AgentInstallService : WorkerLoop
{
    /// <summary>Waits before the next automatic attempt, after the first, second and third failed one.</summary>
    public static readonly IReadOnlyList<TimeSpan> RetryDelays = [TimeSpan.FromHours(1), TimeSpan.FromHours(6), TimeSpan.FromHours(24)];

    /// <summary>
    /// How long after a successful job the Fleeto agent may take to report the new Action1 agent: it sends its inventory
    /// every hour.
    /// </summary>
    public static readonly TimeSpan AgentReportTimeout = TimeSpan.FromHours(3);

    /// <summary>How long after the Fleeto agent reported it Action1 may take to report the endpoint; Fleeto asks for it at once.</summary>
    public static readonly TimeSpan Action1ReportTimeout = TimeSpan.FromHours(2);

    /// <summary>How often an unchanged result is written again, so the check shows when it was last evaluated.</summary>
    public static readonly TimeSpan CheckRefreshInterval = TimeSpan.FromHours(4);

    /// <summary>Installations started in one pass, so a patch policy switched on for a large client does not flood the signer.</summary>
    public const int MaxStartsPerPass = 50;

    public const string RemovedReason = "The Action1 agent was removed from this endpoint through Fleeto.";

    public const string NoInstallerReason =
        "There is no Action1 agent installer link for this client yet. Fleeto reads it from Action1 every hour, or add it in Settings, Integrations.";

    public const string OfflineReason = "No Action1 agent is installed on this endpoint. Fleeto installs it as soon as the endpoint is online.";

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly AlertNotificationService _notifier;
    private readonly LicenseService _licenses;

    public Action1AgentInstallService(IFleetoDbContextFactory dbFactory, INotificationBus bus, AlertNotificationService notifier,
        LicenseService licenses, WorkerHeartbeat heartbeat, TimeProvider time, ILogger<Action1AgentInstallService> logger)
        : base("action1-agent", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _notifier = notifier;
        _licenses = licenses;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(5);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await RunAsync(cancellationToken);
        return false;
    }

    /// <summary>One pass over every endpoint in view.</summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var integration = await db.Integrations.AsNoTracking().Include(i => i.Mappings)
            .SingleOrDefaultAsync(i => i.Type == IntegrationType.Action1, cancellationToken);
        if (integration is not { Enabled: true })
        {
            // Switched off or not set up: nothing is judged or started, and what was known stays for when it is back.
            return;
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var installers = integration.Mappings.GroupBy(m => m.ClientId).ToDictionary(g => g.Key, g => g.First().AgentInstallerUrl);
        var clientIds = installers.Keys.ToList();

        var endpoints = (await db.Endpoints.AsNoTracking()
                .Where(e => clientIds.Contains(e.ClientId) && e.Tier == EndpointTier.Managed)
                .Select(e => new Candidate(e.Id, e.ClientId, e.Hostname, e.OsPlatform, e.IsOnline, e.Tier))
                .ToListAsync(cancellationToken))
            .Where(e => TierRules.EffectiveTier(e.Tier, license) == EndpointTier.Managed && ScriptLanguages.RunsOn(ScriptLanguage.PowerShell, e.OsPlatform))
            .ToDictionary(e => e.Id);
        var ids = endpoints.Keys.ToList();

        var rows = await db.Action1AgentInstallStates.ToDictionaryAsync(r => r.EndpointId, cancellationToken);
        var transitions = new List<AlertTransition>();
        var changed = new HashSet<Guid>();

        // Out of view (no longer managed, Windows or mapped): its checks lose their result once. The row stays, so an agent
        // removed through Fleeto is not installed again when the endpoint comes back into view; it goes with its endpoint.
        var gone = rows.Values.Where(r => !endpoints.ContainsKey(r.EndpointId) && r.CheckWrittenAt is not null).Select(r => r.EndpointId).ToList();
        foreach (var id in gone)
        {
            rows[id].CheckWrittenAt = null;
            rows[id].CheckDetail = null;
        }

        transitions.AddRange(await MissingUpdateChecks.ForgetChecksAsync(db, CheckType.Action1Agent, gone, now,
            "Patch management through Action1 no longer applies to this endpoint.", cancellationToken));

        if (ids.Count > 0)
        {
            var policies = await EffectivePolicies.Query(db)
                .Where(p => ids.Contains(p.EndpointId))
                .Select(p => new { p.EndpointId, p.PatchPolicyId })
                .ToListAsync(cancellationToken);
            var automatic = await db.PatchPolicies.AsNoTracking()
                .Where(p => p.AutoInstallAgent)
                .ToDictionaryAsync(p => p.Id, p => p.Name, cancellationToken);
            var policyOf = policies.Where(p => p.PatchPolicyId is { } id && automatic.ContainsKey(id))
                .ToDictionary(p => p.EndpointId, p => automatic[p.PatchPolicyId!.Value]);
            var covered = (await db.EndpointPatchStates.AsNoTracking()
                    .Where(s => ids.Contains(s.EndpointId))
                    .Select(s => s.EndpointId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            var reported = (await db.InventorySnapshots.AsNoTracking()
                    .Where(i => ids.Contains(i.EndpointId) && i.Action1AgentId != "")
                    .Select(i => i.EndpointId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();
            var jobs = (await db.Jobs.AsNoTracking()
                    .Where(j => ids.Contains(j.EndpointId) && j.Type == JobType.Action1Agent)
                    .Select(j => new JobView(j.Id, j.EndpointId, j.CreatedAt, j.State, j.Result, j.ExitCode, j.RefusalReason, j.Error,
                        j.InitiatedByUserId, j.CompletedAt))
                    .ToListAsync(cancellationToken))
                .GroupBy(j => j.EndpointId)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(j => j.CreatedAt).First());
            var removals = await RemovalsAsync(db, ids, cancellationToken);
            var pendingSyncs = (await db.PatchRequests.AsNoTracking()
                    .Where(r => ids.Contains(r.EndpointId) && r.Kind == PatchRequestKind.Sync && r.CompletedAt == null)
                    .Select(r => r.EndpointId)
                    .ToListAsync(cancellationToken))
                .ToHashSet();

            var starts = 0;
            foreach (var endpoint in endpoints.Values)
            {
                if (!rows.TryGetValue(endpoint.Id, out var row))
                {
                    row = new Action1AgentInstallState
                    {
                        EndpointId = endpoint.Id, ClientId = endpoint.ClientId, Status = Action1AgentStatus.Missing, UpdatedAt = now
                    };
                    db.Action1AgentInstallStates.Add(row);
                    rows[endpoint.Id] = row;
                }

                var before = (row.Status, row.Failure, row.NextAttemptAt, row.JobId);
                policyOf.TryGetValue(endpoint.Id, out var policyName);
                jobs.TryGetValue(endpoint.Id, out var job);
                removals.TryGetValue(endpoint.Id, out var removal);
                var start = Decide(row, policyName is not null, covered.Contains(endpoint.Id), reported.Contains(endpoint.Id), job, removal, now);

                if (start == Next.RequestSync && !pendingSyncs.Contains(endpoint.Id))
                {
                    db.PatchRequests.Add(new PatchRequest
                    {
                        Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = PatchRequestKind.Sync,
                        RequestedByUserId = Action1AgentInstall.AutomaticInitiator, RequestedByName = "Fleeto", RequestedAt = now
                    });
                }
                else if (start == Next.Install)
                {
                    var url = installers.GetValueOrDefault(endpoint.ClientId);
                    if (!Action1AgentInstall.IsValidInstallerUrl(url))
                    {
                        if (row.Status == Action1AgentStatus.Missing)
                        {
                            row.Failure = NoInstallerReason;
                        }
                    }
                    else if (!endpoint.IsOnline)
                    {
                        // A job for an endpoint that is off would only expire; it waits until the endpoint is back.
                        if (row.Status == Action1AgentStatus.Missing)
                        {
                            row.Failure = OfflineReason;
                        }
                    }
                    else if (job is { State: JobState.PendingSignature or JobState.Queued or JobState.Running })
                    {
                        row.JobId = job.Id;
                        row.Status = Action1AgentStatus.Installing;
                    }
                    else if (starts < MaxStartsPerPass)
                    {
                        starts++;
                        row.JobId = await StartAsync(db, endpoint, url!, policyName!, now, cancellationToken);
                        row.Status = Action1AgentStatus.Installing;
                        row.Failure = null;
                        row.NextAttemptAt = null;
                        row.JobSucceededAt = null;
                        row.AgentReportedAt = null;
                    }
                }

                if (before != (row.Status, row.Failure, row.NextAttemptAt, row.JobId))
                {
                    row.UpdatedAt = now;
                    changed.Add(endpoint.Id);
                }
            }

            transitions.AddRange(await MissingUpdateChecks.ForgetChecksAsync(db, CheckType.Action1Agent,
                [.. rows.Values.Where(r => r.Status == Action1AgentStatus.Removed && r.CheckWrittenAt is not null && endpoints.ContainsKey(r.EndpointId))
                    .Select(r => r.EndpointId)],
                now, RemovedReason, cancellationToken));
            foreach (var row in rows.Values.Where(r => r.Status == Action1AgentStatus.Removed && endpoints.ContainsKey(r.EndpointId)))
            {
                row.CheckWrittenAt = null;
                row.CheckDetail = null;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        var written = await WriteCheckResultsAsync(db, [.. rows.Values.Where(r => endpoints.ContainsKey(r.EndpointId))], now, cancellationToken);

        await _notifier.AddNotificationsAsync(db, transitions, cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await AlertCleanup.PublishAsync(_bus, transitions.Select(t => t.AlertId), cancellationToken);
        foreach (var endpointId in written)
        {
            await _bus.PublishAsync(NotificationChannels.CheckResults, endpointId.ToString(), cancellationToken);
        }

        foreach (var endpointId in changed)
        {
            // The Patches tab refreshes on this channel; the request service only wakes up and finds nothing new.
            await _bus.PublishAsync(NotificationChannels.PatchRequests, endpointId.ToString(), cancellationToken);
        }
    }

    /// <summary>What the pass does for an endpoint after its status was decided.</summary>
    private enum Next
    {
        Nothing,
        Install,
        RequestSync
    }

    /// <summary>
    /// Decides the status of one endpoint from what Fleeto knows of it. Returns <see cref="Next.Install"/> when an automatic
    /// installation is due, and <see cref="Next.RequestSync"/> when the Fleeto agent just reported the new Action1 agent,
    /// so Action1 is asked for the endpoint at once instead of at the next sync of the organization.
    /// </summary>
    private static Next Decide(Action1AgentInstallState row, bool automatic, bool covered, bool reported, JobView? job, Removal? removal, DateTime now)
    {
        // A removal through Fleeto that is newer than the newest installation: Fleeto keeps its hands off.
        if (removal is not null && removal.Id != row.RemovalId && (job is null || removal.At > job.CreatedAt))
        {
            row.RemovalId = removal.Id;
            Reset(row, Action1AgentStatus.Removed);
            row.JobId = job?.Id;
            return Next.Nothing;
        }

        // An installation Fleeto has not followed yet. One a technician started counts from zero again, also after a removal.
        if (job is not null && job.Id != row.JobId)
        {
            var byHand = job.InitiatedByUserId != Action1AgentInstall.AutomaticInitiator;
            if (row.Status != Action1AgentStatus.Removed || byHand)
            {
                if (byHand)
                {
                    row.Attempts = 0;
                }

                row.JobId = job.Id;
                row.Status = Action1AgentStatus.Installing;
                row.Failure = null;
                row.NextAttemptAt = null;
                row.JobSucceededAt = null;
                row.AgentReportedAt = null;
            }
        }

        if (covered)
        {
            if (row.Status != Action1AgentStatus.Installed)
            {
                Reset(row, Action1AgentStatus.Installed);
            }

            return Next.Nothing;
        }

        if (row.Status == Action1AgentStatus.Removed)
        {
            return Next.Nothing;
        }

        if (row.Status == Action1AgentStatus.Installing && job is not null && job.Id == row.JobId)
        {
            return Follow(row, automatic, reported, job, now);
        }

        if (reported)
        {
            // An agent Fleeto did not put there, or that it gave up on: patch management still does not cover the endpoint.
            if (row.Status is not (Action1AgentStatus.Failed or Action1AgentStatus.Stopped))
            {
                Reset(row, Action1AgentStatus.NotReported);
            }

            return Next.Nothing;
        }

        if (row.Status is Action1AgentStatus.Installed or Action1AgentStatus.NotReported or Action1AgentStatus.Installing)
        {
            // The agent is gone, or an installation lost its job: start over.
            Reset(row, Action1AgentStatus.Missing);
        }

        if (!automatic)
        {
            if (row.Status == Action1AgentStatus.Failed)
            {
                row.NextAttemptAt = null;
            }
            else if (row.Status == Action1AgentStatus.Stopped)
            {
                row.Status = Action1AgentStatus.Failed;
            }
            else
            {
                row.Failure = null;
            }

            return Next.Nothing;
        }

        if (row.Status == Action1AgentStatus.Failed && row.NextAttemptAt is null)
        {
            // The patch policy was switched on after a failure: try again, unless every attempt was used.
            if (row.Attempts <= RetryDelays.Count)
            {
                row.NextAttemptAt = now;
            }
            else
            {
                row.Status = Action1AgentStatus.Stopped;
            }
        }

        return row.Status == Action1AgentStatus.Missing || row.Status == Action1AgentStatus.Failed && row.NextAttemptAt <= now
            ? Next.Install
            : Next.Nothing;
    }

    /// <summary>Follows the installation job of a row through to Action1 reporting the endpoint.</summary>
    private static Next Follow(Action1AgentInstallState row, bool automatic, bool reported, JobView job, DateTime now)
    {
        switch (job.State)
        {
            case JobState.PendingSignature or JobState.Queued or JobState.Running:
                return Next.Nothing;
            case JobState.Succeeded:
                row.JobSucceededAt ??= job.CompletedAt ?? now;
                if (reported)
                {
                    if (row.AgentReportedAt is null)
                    {
                        row.AgentReportedAt = now;
                        return Next.RequestSync;
                    }

                    if (now - row.AgentReportedAt >= Action1ReportTimeout)
                    {
                        Fail(row, automatic, now, "The Action1 agent is installed, but Action1 does not report this endpoint in the organization of this client. " +
                            "Check that the endpoint reaches Action1 and that the installer link belongs to this client's organization.");
                    }
                }
                else if (now - row.JobSucceededAt >= AgentReportTimeout)
                {
                    Fail(row, automatic, now, "The installation finished, but the Fleeto agent does not find an Action1 agent on the endpoint. See the job output on the Jobs tab.");
                }

                return Next.Nothing;
            case JobState.Failed:
                Fail(row, automatic, now, job.Result switch
                {
                    JobResult.TimedOut => "The installation took longer than 20 minutes and was stopped. See the job output on the Jobs tab.",
                    JobResult.Exited when job.ExitCode is { } code => Action1AgentInstall.ExplainExitCode(code),
                    _ => job.Error is { Length: > 0 } error
                        ? $"The installation could not run on the endpoint: {error}"
                        : "The installation failed on the endpoint. See the job output on the Jobs tab."
                });
                return Next.Nothing;
            case JobState.Expired:
                Fail(row, automatic, now, "The endpoint was not online within 24 hours, so the installation never started.");
                return Next.Nothing;
            case JobState.Refused:
                Fail(row, automatic, now, job.RefusalReason is { Length: > 0 } reason
                    ? $"The installation was refused: {reason}"
                    : "The installation was refused. See the Jobs tab.");
                return Next.Nothing;
            case JobState.Lost:
                Fail(row, automatic, now, "No result of the installation arrived from the endpoint. Check on the Jobs tab whether the agent restarted during it.");
                return Next.Nothing;
            default:
                Fail(row, automatic, now, "The installation was cancelled before it ran.");
                return Next.Nothing;
        }
    }

    /// <summary>Records a failed attempt and plans the next one while the patch policy installs automatically.</summary>
    private static void Fail(Action1AgentInstallState row, bool automatic, DateTime now, string reason)
    {
        row.Attempts++;
        row.Failure = Cut(reason, 1000);
        row.JobSucceededAt = null;
        row.AgentReportedAt = null;
        if (automatic && row.Attempts > RetryDelays.Count)
        {
            row.Status = Action1AgentStatus.Stopped;
            row.NextAttemptAt = null;
        }
        else
        {
            row.Status = Action1AgentStatus.Failed;
            row.NextAttemptAt = automatic ? now + RetryDelays[row.Attempts - 1] : null;
        }
    }

    private static void Reset(Action1AgentInstallState row, Action1AgentStatus status)
    {
        row.Status = status;
        row.Attempts = 0;
        row.Failure = null;
        row.NextAttemptAt = null;
        row.JobSucceededAt = null;
        row.AgentReportedAt = null;
    }

    /// <summary>
    /// Writes the installation job of one endpoint without a user: the job, then its signing request and audit entry. The
    /// signer composes the body from the same link and checks the patch policy again.
    /// </summary>
    private static async Task<Guid> StartAsync(FleetoDbContext db, Candidate endpoint, string installerUrl, string policyName, DateTime now,
        CancellationToken cancellationToken)
    {
        var job = new Job
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            BatchId = Guid.NewGuid(),
            Type = JobType.Action1Agent,
            ScriptName = Action1AgentInstall.JobName,
            Language = ScriptLanguage.PowerShell,
            ScriptSha256 = ScriptLanguages.Sha256(Action1AgentInstall.Body(installerUrl)),
            TimeoutSeconds = Action1AgentInstall.TimeoutSeconds,
            MaxOutputBytes = Action1AgentInstall.MaxOutputBytes,
            RunAs = JobRunAs.Service,
            CreatedAt = now,
            ValidUntil = now + ScriptRules.DefaultValidity,
            InitiatedByUserId = Action1AgentInstall.AutomaticInitiator,
            InitiatedByName = Cut(Action1AgentInstall.AutomaticInitiatorName(policyName), 200)
        };

        // The job first: the database lets the workers ask for a signature only for a job of theirs that already exists.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        db.Jobs.Add(job);
        await db.SaveChangesAsync(cancellationToken);
        db.SigningRequests.Add(new SigningRequest
        {
            Id = Guid.NewGuid(),
            ClientId = endpoint.ClientId,
            Kind = SigningRequestKind.Job,
            SubjectId = job.Id,
            Payload = [],
            RequestedBy = "workers:action1-agent",
            CreatedAt = now
        });
        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.JobCreated, "Job", job.Id.ToString(), endpoint.ClientId,
            AuditActorType.System, "fleeto-workers", "fleeto-workers", new
            {
                endpoint.Hostname,
                EndpointId = endpoint.Id,
                Type = job.Type.ToString(),
                job.ScriptName,
                job.ValidUntil,
                PatchPolicy = policyName
            }), now));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return job.Id;
    }

    /// <summary>
    /// The newest removal of the Action1 agent through Fleeto per endpoint that succeeded: a request to Action1, or a job.
    /// A request is kept a day after it completed, which is many passes.
    /// </summary>
    private static async Task<Dictionary<Guid, Removal>> RemovalsAsync(FleetoDbContext db, List<Guid> ids, CancellationToken cancellationToken)
    {
        var requests = await db.PatchRequests.AsNoTracking()
            .Where(r => ids.Contains(r.EndpointId) && r.Kind == PatchRequestKind.RemoveAgent && r.CompletedAt != null && r.Failure == null)
            .Select(r => new { r.EndpointId, r.Id, At = r.RequestedAt })
            .ToListAsync(cancellationToken);
        var jobs = await db.Jobs.AsNoTracking()
            .Where(j => ids.Contains(j.EndpointId) && j.Type == JobType.Action1AgentRemoval && j.State == JobState.Succeeded)
            .Select(j => new { j.EndpointId, j.Id, At = j.CreatedAt })
            .ToListAsync(cancellationToken);
        return requests.Concat(jobs)
            .GroupBy(r => r.EndpointId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(r => r.At).Select(r => new Removal(r.Id, r.At)).First());
    }

    /// <summary>
    /// Gives the "Action1 agent" checks their result: when what they would say changed, or every
    /// <see cref="CheckRefreshInterval"/>. Nothing while an installation runs or after a removal through Fleeto.
    /// </summary>
    private static async Task<IReadOnlyList<Guid>> WriteCheckResultsAsync(FleetoDbContext db, IReadOnlyList<Action1AgentInstallState> rows,
        DateTime now, CancellationToken cancellationToken)
    {
        if (rows.Count == 0 || !await db.CheckDefinitions.AnyAsync(d => d.Type == CheckType.Action1Agent, cancellationToken))
        {
            return [];
        }

        var due = rows
            .Select(r => (Row: r, Result: Result(r, now)))
            .Where(x => x.Result is { } result &&
                        (result.Detail != x.Row.CheckDetail || x.Row.CheckWrittenAt is not { } at || now - at >= CheckRefreshInterval))
            .ToList();
        if (due.Count == 0)
        {
            return [];
        }

        var dueIds = due.Select(x => x.Row.EndpointId).ToList();
        var endpoints = await db.Endpoints.AsNoTracking().Where(e => dueIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken);
        var written = new List<Guid>();
        foreach (var (row, result) in due)
        {
            if (!endpoints.TryGetValue(row.EndpointId, out var endpoint))
            {
                continue;
            }

            var checks = (await EffectiveCheckResolver.LoadAsync(db, endpoint, includeDisabledOnEndpoint: false, cancellationToken))
                .Where(c => c.Type == CheckType.Action1Agent)
                .ToList();
            foreach (var check in checks)
            {
                db.CheckResults.Add(new CheckResult
                {
                    Time = now,
                    AgentTime = now,
                    ClientId = endpoint.ClientId,
                    EndpointId = endpoint.Id,
                    CheckDefinitionId = check.Id,
                    Target = string.Empty,
                    Value = result!.Value.Value,
                    Detail = result.Value.Detail
                });
            }

            row.CheckWrittenAt = now;
            row.CheckDetail = result!.Value.Detail;
            if (checks.Count > 0)
            {
                written.Add(endpoint.Id);
            }
        }

        return written;
    }

    /// <summary>What the "Action1 agent" check says for a row, or null when it says nothing now.</summary>
    internal static (double Value, string Detail)? Result(Action1AgentInstallState row, DateTime now) => row.Status switch
    {
        Action1AgentStatus.Installed => (1, "Action1 reports this endpoint."),
        Action1AgentStatus.Missing => (0, row.Failure ??
            "No Action1 agent is installed on this endpoint. Install it on the Patches tab, or let its patch policy install it automatically."),
        Action1AgentStatus.NotReported => (0,
            "An Action1 agent runs on this endpoint, but Action1 does not report it in the organization of this client. See the Patches tab."),
        Action1AgentStatus.Failed => (0, Cut(row.NextAttemptAt is { } next
            ? next > now
                ? $"{row.Failure} Fleeto tries again at {next.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} UTC."
                : $"{row.Failure} Fleeto tries again as soon as the endpoint is online."
            : row.Failure ?? "The installation of the Action1 agent failed.", 1000)),
        Action1AgentStatus.Stopped => (0, Cut(
            $"{row.Failure} Fleeto stopped after {row.Attempts} attempts; install the agent on the Patches tab to try again.", 1000)),
        _ => null
    };

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];

    private sealed record Candidate(Guid Id, Guid ClientId, string Hostname, string OsPlatform, bool IsOnline, EndpointTier Tier);

    private sealed record JobView(Guid Id, Guid EndpointId, DateTime CreatedAt, JobState State, JobResult? Result, int? ExitCode,
        string? RefusalReason, string? Error, Guid InitiatedByUserId, DateTime? CompletedAt);

    private sealed record Removal(Guid Id, DateTime At);
}
