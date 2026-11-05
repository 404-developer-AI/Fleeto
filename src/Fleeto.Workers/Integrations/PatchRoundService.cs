using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Runs the rounds of the patch policies of engine Fleeto (0.9.0 step 4, <c>PATCHING.md</c> §7). Every pass:
/// <list type="number">
/// <item><b>Starts rounds</b>: for every enabled policy of engine Fleeto the occurrences of its schedule that passed, per time zone of
/// its endpoints (<see cref="PatchRounds"/>), each as one deployment of scope <see cref="PatchDeploymentScope.PatchPolicy"/> per client
/// with a target per managed endpoint the policy applies to and Fleeto patches. A target is due at the occurrence plus its share of the
/// random delay; one whose agent is too old, whose previous installation has not ended or whose last scan found nothing the policy
/// installs is answered at once with the reason. A policy the workers see for the first time, or whose schedule changed, starts counting
/// from then (<see cref="PatchRoundState"/>), and an occurrence runs once per client (unique index), whatever the workers retry.</item>
/// <item><b>Starts installations</b>: a target whose turn came gets its installation job, without a user, and a signing request, in one
/// transaction; fleeto-signer decides again from the policy (<c>JobHandler</c>) and composes what is installed.</item>
/// <item><b>Keeps "Scheduled patching" counting</b>: the patch state of an endpoint Fleeto patches remembers since when an enabled policy
/// of engine Fleeto schedules it (<see cref="EndpointPatchState.ScheduledSince"/>), as the automations of Action1 do.</item>
/// </list>
/// <see cref="FleetoDeploymentService"/> follows the jobs as it does for a deployment a technician started.
/// </summary>
public sealed class PatchRoundService : WorkerLoop
{
    public const int BatchSize = 200;

    public const string PolicyGoneMessage = "The patch policy was switched off, deleted or no longer patches by Fleeto before this endpoint's turn.";
    public const string ExpiredMessage = "The retry window of the round passed before this endpoint's turn.";

    /// <summary>How often the scheduled-since of the patch states is brought in step with the policies.</summary>
    private static readonly TimeSpan ScheduleTrackInterval = TimeSpan.FromMinutes(5);

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private readonly LicenseService _licenses;
    private DateTime _trackedAt = DateTime.MinValue;

    public PatchRoundService(IFleetoDbContextFactory dbFactory, INotificationBus bus, LicenseService licenses, WorkerHeartbeat heartbeat,
        TimeProvider time, ILogger<PatchRoundService> logger)
        : base("patch-rounds", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
        _licenses = licenses;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken)
    {
        await StartRoundsAsync(cancellationToken);
        var started = await StartJobsAsync(cancellationToken);
        var now = Time.GetUtcNow().UtcDateTime;
        if (now - _trackedAt >= ScheduleTrackInterval)
        {
            await TrackScheduledAsync(cancellationToken);
            _trackedAt = now;
        }

        return started >= BatchSize;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // 1. Rounds
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Starts the rounds that are due. Returns the number of deployments written.</summary>
    public async Task<int> StartRoundsAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var policies = await db.PatchPolicies.AsNoTracking()
            .Where(p => p.Enabled && p.Engine == PatchEngine.Fleeto)
            .OrderBy(p => p.CreatedAt)
            .ToListAsync(cancellationToken);
        var states = await db.PatchRoundStates.ToDictionaryAsync(s => s.PatchPolicyId, cancellationToken);
        if (policies.Count == 0)
        {
            return 0;
        }

        var license = await _licenses.GetStatusAsync(db, cancellationToken);
        var written = 0;
        foreach (var policy in policies)
        {
            var hash = PatchRounds.ScheduleHash(policy);
            if (!states.TryGetValue(policy.Id, out var state) || state.ScheduleHash != hash)
            {
                // New to the workers or a new schedule: count from now, so nothing from before it starts a round.
                if (state is null)
                {
                    state = new PatchRoundState { PatchPolicyId = policy.Id };
                    db.PatchRoundStates.Add(state);
                }

                state.CheckedUntil = now;
                state.ScheduleHash = hash;
                state.UpdatedAt = now;
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var from = state.CheckedUntil;
            if (now - from > PatchRounds.RetryWindow(policy))
            {
                // A job later than the retry window could not start anyway.
                from = now - PatchRounds.RetryWindow(policy);
            }

            if (from >= now)
            {
                continue;
            }

            var endpoints = await EndpointsOfAsync(db, policy, license.AllowsManaged, cancellationToken);
            var rounds = endpoints
                .SelectMany(e => PatchRounds.Occurrences(policy, PatchRounds.ZoneOf(policy, e.TimeZone), from, now).Select(at => (At: at, Endpoint: e)))
                .GroupBy(r => (r.At, r.Endpoint.ClientId))
                .OrderBy(g => g.Key.At);
            foreach (var round in rounds)
            {
                if (await WriteRoundAsync(db, policy, round.Key.ClientId, round.Key.At, [.. round.Select(r => r.Endpoint)], now, cancellationToken))
                {
                    written++;
                }
            }

            state.CheckedUntil = now;
            state.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
        }

        return written;
    }

    internal sealed record RoundEndpoint(Guid Id, Guid ClientId, string Hostname, string OsPlatform, string AgentVersion, string TimeZone);

    /// <summary>The managed endpoints whose patch policy is <paramref name="policy"/> and whose engine is Fleeto (<see cref="PatchEngineRules"/>).</summary>
    private static async Task<List<RoundEndpoint>> EndpointsOfAsync(FleetoDbContext db, PatchPolicy policy, bool allowsManaged, CancellationToken cancellationToken) =>
        await (from p in EffectivePolicies.Query(db)
               where p.PatchPolicyId == policy.Id
               join engine in PatchEngines.Query(db, allowsManaged) on p.EndpointId equals engine.EndpointId
               where engine.Engine == EndpointPatchEngine.Fleeto
               join e in db.Endpoints on p.EndpointId equals e.Id
               select new RoundEndpoint(e.Id, e.ClientId, e.Hostname, e.OsPlatform, e.AgentVersion,
                   db.InventorySnapshots.Where(i => i.EndpointId == e.Id).Select(i => i.TimeZone).FirstOrDefault() ?? string.Empty))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Writes one round for one client and occurrence: the deployment, a target per endpoint and the audit entry. False when the round was
    /// already written (by an earlier pass or another worker).
    /// </summary>
    private async Task<bool> WriteRoundAsync(FleetoDbContext db, PatchPolicy policy, Guid clientId, DateTime roundAt, IReadOnlyList<RoundEndpoint> endpoints,
        DateTime now, CancellationToken cancellationToken)
    {
        if (await db.PatchDeployments.AnyAsync(d => d.ClientId == clientId && d.PatchPolicyId == policy.Id && d.RoundAt == roundAt, cancellationToken))
        {
            return false;
        }

        var ids = endpoints.Select(e => e.Id).ToList();
        // An earlier installation that has not ended: the agent refuses a second one, so the round says so instead.
        var busy = await (from t in db.PatchDeploymentTargets
                          join d in db.PatchDeployments on t.DeploymentId equals d.Id
                          where ids.Contains(t.EndpointId) && d.Engine == PatchEngine.Fleeto &&
                                (t.State == PatchDeploymentTargetState.Pending || t.State == PatchDeploymentTargetState.Running)
                          select t.EndpointId)
            .Distinct().ToListAsync(cancellationToken);
        var scanned = await db.EndpointPatchStates.AsNoTracking()
            .Where(s => ids.Contains(s.EndpointId) && s.Engine == PatchEngine.Fleeto && s.ProductLastSeenAt != null && s.ScanProblem == null)
            .Select(s => s.EndpointId)
            .ToListAsync(cancellationToken);
        var missing = (await db.EndpointMissingUpdates.AsNoTracking().Where(u => ids.Contains(u.EndpointId)).ToListAsync(cancellationToken))
            .ToLookup(u => u.EndpointId);

        var deployment = new PatchDeployment
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            BatchId = Guid.NewGuid(),
            Engine = PatchEngine.Fleeto,
            Scope = PatchDeploymentScope.PatchPolicy,
            AutoReboot = policy.AutoReboot,
            SecurityOnly = policy.LinuxSecurityOnly,
            State = PatchDeploymentState.Running,
            StartedAt = now,
            PatchPolicyId = policy.Id,
            RoundAt = roundAt,
            FollowUntil = PatchRounds.ValidUntil(policy, roundAt) + TimeSpan.FromSeconds(PatchRounds.TimeoutSeconds(policy)) + TimeSpan.FromHours(1),
            RequestedByUserId = PatchRounds.AutomaticInitiator,
            RequestedByName = Cut(PatchRounds.AutomaticInitiatorName(policy.Name), 200),
            RequestedAt = now
        };
        foreach (var endpoint in endpoints.OrderBy(e => e.Hostname, StringComparer.OrdinalIgnoreCase))
        {
            var target = new PatchDeploymentTarget
            {
                Id = Guid.NewGuid(), DeploymentId = deployment.Id, ClientId = clientId, EndpointId = endpoint.Id, Hostname = endpoint.Hostname, UpdatedAt = now
            };
            if (PatchRules.InstallProblem(endpoint.OsPlatform, endpoint.AgentVersion) is { } problem)
            {
                (target.State, target.Message) = (PatchDeploymentTargetState.Unknown, Cut(problem, 1000));
            }
            else if (busy.Contains(endpoint.Id))
            {
                (target.State, target.Message) = (PatchDeploymentTargetState.Unknown, PatchRounds.PreviousOpenMessage);
            }
            else if (scanned.Contains(endpoint.Id) && !missing[endpoint.Id].Any(u => PatchRounds.Allows(policy, u, endpoint.OsPlatform, now)))
            {
                // The last scan read everything and found nothing this policy installs: the round has nothing to do here.
                (target.State, target.Message, target.Restart) = (PatchDeploymentTargetState.Succeeded, PatchRounds.NothingToInstallMessage, PatchTargetRestart.NotRequired);
            }
            else
            {
                target.DueAt = PatchRounds.DueAt(policy, deployment.Id, endpoint.Id, roundAt);
            }

            deployment.Targets.Add(target);
        }

        db.PatchDeployments.Add(deployment);
        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.PatchRoundStarted, "PatchDeployment", deployment.Id.ToString(), clientId,
            AuditActorType.System, "fleeto-workers", "fleeto-workers", new
            {
                PatchPolicy = policy.Name,
                PatchPolicyId = policy.Id,
                RoundAt = roundAt,
                Endpoints = deployment.Targets.Count,
                Due = deployment.Targets.Count(t => t.DueAt is not null),
                Answered = deployment.Targets.Count(t => t.DueAt is null)
            }), now));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: Npgsql.PostgresErrorCodes.UniqueViolation })
        {
            // Another worker wrote the same round first.
            db.ChangeTracker.Clear();
            return false;
        }

        await _bus.PublishAsync(NotificationChannels.PatchDeployments, deployment.Id.ToString(), cancellationToken);
        return true;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // 2. Installations
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>Writes the installation job of every target whose turn came. Returns the number of targets handled.</summary>
    public async Task<int> StartJobsAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var due = await (from t in db.PatchDeploymentTargets
                         join d in db.PatchDeployments on t.DeploymentId equals d.Id
                         where t.State == PatchDeploymentTargetState.Pending && t.JobId == null && t.DueAt != null && t.DueAt <= now &&
                               d.Engine == PatchEngine.Fleeto && d.RoundAt != null
                         orderby t.DueAt
                         select new { Target = t, d.PatchPolicyId, d.RoundAt, d.BatchId, d.RequestedByName })
            .Take(BatchSize)
            .ToListAsync(cancellationToken);
        if (due.Count == 0)
        {
            return 0;
        }

        var policyIds = due.Select(d => d.PatchPolicyId).Distinct().ToList();
        var policies = await db.PatchPolicies.AsNoTracking().Where(p => policyIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, cancellationToken);
        var deployments = new HashSet<Guid>();
        foreach (var item in due)
        {
            var target = item.Target;
            var policy = item.PatchPolicyId is { } policyId ? policies.GetValueOrDefault(policyId) : null;
            deployments.Add(target.DeploymentId);
            if (policy is not { Enabled: true, Engine: PatchEngine.Fleeto } || (policy.ClientId is { } owner && owner != target.ClientId))
            {
                Answer(target, PolicyGoneMessage, now);
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var validUntil = PatchRounds.ValidUntil(policy, DateTime.SpecifyKind(item.RoundAt!.Value, DateTimeKind.Utc));
            if (validUntil <= now + TimeSpan.FromMinutes(1))
            {
                Answer(target, ExpiredMessage, now);
                await db.SaveChangesAsync(cancellationToken);
                continue;
            }

            var job = new Job
            {
                Id = Guid.NewGuid(),
                ClientId = target.ClientId,
                EndpointId = target.EndpointId,
                BatchId = item.BatchId,
                Type = JobType.PatchInstall,
                ScriptName = PatchRules.InstallJobName,
                TimeoutSeconds = PatchRounds.TimeoutSeconds(policy),
                MaxOutputBytes = PatchRules.InstallMaxOutputBytes,
                RunAs = JobRunAs.Service,
                CreatedAt = now,
                ValidUntil = validUntil,
                InitiatedByUserId = PatchRounds.AutomaticInitiator,
                InitiatedByName = item.RequestedByName,
                PatchDeploymentId = target.DeploymentId,
                PatchDeploymentTargetId = target.Id
            };

            // The job first: the database lets the workers ask for a signature only for a job of theirs that already exists.
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            db.Jobs.Add(job);
            await db.SaveChangesAsync(cancellationToken);
            target.JobId = job.Id;
            target.UpdatedAt = now;
            db.SigningRequests.Add(new SigningRequest
            {
                Id = Guid.NewGuid(), ClientId = job.ClientId, Kind = SigningRequestKind.Job, SubjectId = job.Id, Payload = [],
                RequestedBy = "workers:patch-rounds", CreatedAt = now
            });
            db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.JobCreated, "Job", job.Id.ToString(), job.ClientId,
                AuditActorType.System, "fleeto-workers", "fleeto-workers", new
                {
                    target.EndpointId, target.Hostname, Type = JobType.PatchInstall.ToString(), job.ScriptName, PatchPolicyId = policy.Id,
                    DeploymentId = target.DeploymentId, job.ValidUntil
                }), now));
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var id in deployments)
        {
            await _bus.PublishAsync(NotificationChannels.PatchDeployments, id.ToString(), cancellationToken);
        }

        return due.Count;
    }

    private static void Answer(PatchDeploymentTarget target, string message, DateTime now)
    {
        target.State = PatchDeploymentTargetState.Unknown;
        target.Message = message;
        target.UpdatedAt = now;
    }

    // -----------------------------------------------------------------------------------------------------------------
    // 3. Scheduled patching
    // -----------------------------------------------------------------------------------------------------------------

    /// <summary>
    /// Brings <see cref="EndpointPatchState.ScheduledPolicyId"/> and <see cref="EndpointPatchState.ScheduledSince"/> of the patch states of
    /// engine Fleeto in step with the enabled policies of engine Fleeto that apply to them, so the "Scheduled patching" check counts from
    /// when a policy first scheduled the endpoint. Returns the number of states changed.
    /// </summary>
    public async Task<int> TrackScheduledAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var now = Time.GetUtcNow().UtcDateTime;
        var rows = await (from s in db.EndpointPatchStates
                          where s.Engine == PatchEngine.Fleeto && s.AutomationId == null
                          join p in EffectivePolicies.Query(db) on s.EndpointId equals p.EndpointId
                          select new
                          {
                              State = s,
                              PolicyId = db.PatchPolicies.Where(pp => pp.Id == p.PatchPolicyId && pp.Enabled && pp.Engine == PatchEngine.Fleeto)
                                  .Select(pp => (Guid?)pp.Id).FirstOrDefault()
                          })
            .ToListAsync(cancellationToken);
        var changed = 0;
        foreach (var row in rows)
        {
            if (row.PolicyId is { } policyId)
            {
                if (row.State.ScheduledPolicyId != policyId || row.State.ScheduledSince is null)
                {
                    row.State.ScheduledPolicyId = policyId;
                    row.State.ScheduledSince = now;
                    changed++;
                }
            }
            else if (row.State.ScheduledPolicyId is not null || row.State.ScheduledSince is not null)
            {
                row.State.ScheduledPolicyId = null;
                row.State.ScheduledSince = null;
                changed++;
            }
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return changed;
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
