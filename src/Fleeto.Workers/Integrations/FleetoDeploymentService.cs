using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Workers.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Fleeto.Workers.Integrations;

/// <summary>
/// Follows the deployments the Fleeto agent carries out (0.9.0 step 3, <c>PATCHING.md</c> §7): every endpoint of such a deployment has an
/// installation job, and its state is the state of that job. When a job ended its output gives the steps of the history (one line per
/// update, in the form of <see cref="PatchRules.ParseInstallOutput"/>) and what became of the restart. The deployment completes when
/// every endpoint has an answer, and is abandoned with the rest unknown after <see cref="PatchDeployment.FollowUntil"/>.
/// <para>Woken by <c>fleeto_jobs</c>; every minute it also catches up, so a lost notification only delays the history.</para>
/// </summary>
public sealed class FleetoDeploymentService : WorkerLoop
{
    /// <summary>A job that ended this long ago without its whole output is read with what arrived.</summary>
    internal static readonly TimeSpan OutputGrace = TimeSpan.FromMinutes(10);

    private const int MaxDeploymentsPerPass = 200;

    private readonly IFleetoDbContextFactory _dbFactory;
    private readonly INotificationBus _bus;
    private IDisposable? _subscription;

    public FleetoDeploymentService(IFleetoDbContextFactory dbFactory, INotificationBus bus, WorkerHeartbeat heartbeat, TimeProvider time,
        ILogger<FleetoDeploymentService> logger)
        : base("patch-installs", heartbeat, time, logger)
    {
        _dbFactory = dbFactory;
        _bus = bus;
    }

    protected override TimeSpan Interval => TimeSpan.FromMinutes(1);

    protected override void OnStarting() =>
        _subscription = _bus.Subscribe(NotificationChannels.Jobs, (_, _) =>
        {
            Wake();
            return Task.CompletedTask;
        });

    protected override void OnStopping() => _subscription?.Dispose();

    protected override async Task<bool> RunOnceAsync(CancellationToken cancellationToken) =>
        await FollowAsync(cancellationToken) >= MaxDeploymentsPerPass;

    /// <summary>One pass over the open deployments by Fleeto. Returns the number looked at.</summary>
    public async Task<int> FollowAsync(CancellationToken cancellationToken)
    {
        await using var db = _dbFactory.CreateSystem();
        var open = await db.PatchDeployments
            .Include(d => d.Targets)
            .Where(d => d.Engine == PatchEngine.Fleeto && (d.State == PatchDeploymentState.Requested || d.State == PatchDeploymentState.Running))
            .OrderBy(d => d.RequestedAt)
            .Take(MaxDeploymentsPerPass)
            .ToListAsync(cancellationToken);
        if (open.Count == 0)
        {
            return 0;
        }

        var now = Time.GetUtcNow().UtcDateTime;
        var jobIds = open.SelectMany(d => d.Targets).Where(t => t.JobId != null).Select(t => t.JobId!.Value).ToList();
        var jobs = await db.Jobs.AsNoTracking().Where(j => jobIds.Contains(j.Id)).ToDictionaryAsync(j => j.Id, cancellationToken);
        var changed = new List<Guid>();
        foreach (var deployment in open)
        {
            var before = deployment.Targets.Select(t => (t.State, t.Restart, t.Message)).ToList();
            foreach (var target in deployment.Targets.Where(t => t.State is PatchDeploymentTargetState.Pending or PatchDeploymentTargetState.Running))
            {
                var job = target.JobId is { } jobId ? jobs.GetValueOrDefault(jobId) : null;
                await FollowTargetAsync(db, target, job, now, cancellationToken);
            }

            var ended = deployment.Targets.All(t => t.State is not (PatchDeploymentTargetState.Pending or PatchDeploymentTargetState.Running));
            if (!ended && deployment.FollowUntil is { } until && now > until)
            {
                foreach (var target in deployment.Targets.Where(t => t.State is PatchDeploymentTargetState.Pending or PatchDeploymentTargetState.Running))
                {
                    target.State = PatchDeploymentTargetState.Unknown;
                    target.Message = "Fleeto stopped following this endpoint: its installation never reported an end. The next scan shows what was installed.";
                    target.UpdatedAt = now;
                }

                Complete(db, deployment, PatchDeploymentState.Abandoned, now);
            }
            else if (ended)
            {
                Complete(db, deployment, PatchDeploymentState.Completed, now);
            }

            if (!before.SequenceEqual(deployment.Targets.Select(t => (t.State, t.Restart, t.Message))) || !deployment.IsOpen)
            {
                changed.Add(deployment.Id);
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        foreach (var id in changed)
        {
            await _bus.PublishAsync(NotificationChannels.PatchDeployments, id.ToString(), cancellationToken);
        }

        return open.Count;
    }

    /// <summary>
    /// The state of one endpoint from its installation job. A job that ended gets its steps and restart from its output, once that has
    /// arrived (or after <see cref="OutputGrace"/> with what arrived).
    /// </summary>
    private static async Task FollowTargetAsync(FleetoDbContext db, PatchDeploymentTarget target, Job? job, DateTime now, CancellationToken cancellationToken)
    {
        if (job is null)
        {
            target.State = PatchDeploymentTargetState.Unknown;
            target.Message = "The installation job of this endpoint no longer exists.";
            target.UpdatedAt = now;
            return;
        }

        var (state, message) = job.State switch
        {
            JobState.PendingSignature or JobState.Queued => (PatchDeploymentTargetState.Pending, (string?)null),
            JobState.Running => (PatchDeploymentTargetState.Running, null),
            JobState.Succeeded => (PatchDeploymentTargetState.Succeeded, null),
            JobState.Failed => (PatchDeploymentTargetState.Failed, job.Error ?? "An update could not be installed. The output of the job says why."),
            JobState.Refused => (PatchDeploymentTargetState.Unknown, job.RefusalReason ?? "The installation was refused."),
            JobState.Expired => (PatchDeploymentTargetState.Unknown, "The endpoint was not online before the installation expired. Nothing was installed."),
            JobState.Lost => (PatchDeploymentTargetState.Unknown,
                "The agent stopped while it installed, so whether everything was installed is unknown. The next scan shows what was installed."),
            JobState.Cancelled => (PatchDeploymentTargetState.Unknown, "The installation was cancelled before it reached the endpoint."),
            _ => (PatchDeploymentTargetState.Unknown, null)
        };
        var ended = job.CompletedAt is not null && state is not (PatchDeploymentTargetState.Pending or PatchDeploymentTargetState.Running);
        if (ended && job.Result is not null && job.OutputState != JobOutputState.Complete && now - job.CompletedAt!.Value < OutputGrace)
        {
            // The output is still on its way; the state waits for it so the history and the restart arrive together.
            state = PatchDeploymentTargetState.Running;
        }

        if (state != target.State || message != target.Message)
        {
            target.State = state;
            target.Message = message is null ? null : Cut(message, 1000);
            target.UpdatedAt = now;
        }

        if (state is PatchDeploymentTargetState.Pending or PatchDeploymentTargetState.Running || job.Result is null)
        {
            return;
        }

        var chunks = await db.JobOutputChunks.AsNoTracking()
            .Where(c => c.JobId == job.Id && c.Stream == JobStream.Stdout)
            .OrderBy(c => c.Sequence)
            .Select(c => c.Data)
            .ToListAsync(cancellationToken);
        var output = Encoding.UTF8.GetString([.. chunks.SelectMany(c => c)]);
        var (lines, restart) = PatchRules.ParseInstallOutput(output);
        target.Restart = restart;
        db.PatchDeploymentSteps.RemoveRange(await db.PatchDeploymentSteps.Where(s => s.TargetId == target.Id).ToListAsync(cancellationToken));
        var position = 0;
        foreach (var line in lines)
        {
            db.PatchDeploymentSteps.Add(new PatchDeploymentStep
            {
                Id = Guid.NewGuid(),
                TargetId = target.Id,
                ClientId = target.ClientId,
                Position = position++,
                Time = job.CompletedAt,
                Operation = "Install",
                Status = PatchRules.StatusWord(line.Status),
                Details = Cut(line.Title.Length > 0 ? line.Title : line.Id, 2000)
            });
        }

        target.StepsReadAt = now;
    }

    /// <summary>Ends the deployment and audits it with its counts, never a package list.</summary>
    private static void Complete(FleetoDbContext db, PatchDeployment deployment, PatchDeploymentState state, DateTime now)
    {
        deployment.State = state;
        deployment.CompletedAt = now;
        db.AuditEntries.Add(AuditLog.ToEntry(new AuditRecord(AuditActions.PatchDeploymentCompleted, "PatchDeployment", deployment.Id.ToString(),
            deployment.ClientId, AuditActorType.System, "fleeto-workers", "fleeto-workers", new
            {
                Engine = deployment.Engine.ToString(),
                State = state.ToString(),
                Succeeded = deployment.Targets.Count(t => t.State == PatchDeploymentTargetState.Succeeded),
                Failed = deployment.Targets.Count(t => t.State == PatchDeploymentTargetState.Failed),
                Unknown = deployment.Targets.Count(t => t.State == PatchDeploymentTargetState.Unknown),
                Restarted = deployment.Targets.Count(t => t.Restart == PatchTargetRestart.Restarted)
            }), now));
    }

    private static string Cut(string value, int max) => value.Length <= max ? value : value[..max];
}
