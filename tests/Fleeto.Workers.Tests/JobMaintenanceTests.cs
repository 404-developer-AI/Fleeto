using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Workers.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees job maintenance (0.2.0): an unsigned job is refused after 15 minutes, a queued job expires after its validity and
/// the agent's tolerance, a running job without result becomes lost after timeout and grace, missing output becomes incomplete
/// after 7 days, and retention removes old output and history but never an active job.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class JobMaintenanceTests
{
    private readonly WorkersFixture _fixture;

    public JobMaintenanceTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Job> JobAsync(Endpoint endpoint, JobState state, Action<Job>? configure = null)
    {
        var user = await _fixture.Db.CreateUserAsync(FleetoRoles.Technician);
        var (script, version) = await _fixture.Db.CreateScriptAsync(null, user.Id);
        var now = _fixture.Now;
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = Guid.NewGuid(), ScriptId = script.Id, ScriptVersionId = version.Id,
            ScriptName = script.Name, ScriptVersionNumber = 1, Language = script.Language, ScriptSha256 = version.Sha256, TimeoutSeconds = 600,
            MaxOutputBytes = ScriptRules.MaxOutputBytes, CreatedAt = now, ValidUntil = now.AddHours(1), InitiatedByUserId = user.Id, InitiatedByName = "Tech",
            State = state, Signature = state == JobState.PendingSignature ? null : new byte[64], Payload = state == JobState.PendingSignature ? null : [1]
        };
        configure?.Invoke(job);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<Job> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    [Fact]
    public async Task Jobs_nobody_will_finish_reach_their_final_state_at_the_right_time()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-JOBS");
        var unsigned = await JobAsync(endpoint, JobState.PendingSignature);
        var queued = await JobAsync(endpoint, JobState.Queued);
        var running = await JobAsync(endpoint, JobState.Running, j => { j.StartedAt = _fixture.Now; j.DeliveredAt = _fixture.Now; });
        var receiving = await JobAsync(endpoint, JobState.Succeeded, j =>
        {
            j.CompletedAt = _fixture.Now; j.OutputState = JobOutputState.Receiving; j.DeliveredAt = _fixture.Now;
        });
        var service = new JobMaintenanceService(_fixture.Db.DbFactory, _fixture.Db.Bus, _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<JobMaintenanceService>.Instance);

        await service.RunAsync(CancellationToken.None);
        Assert.Equal(JobState.PendingSignature, (await ReadAsync(unsigned.Id)).State);
        Assert.Equal(JobState.Queued, (await ReadAsync(queued.Id)).State);

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await service.RunAsync(CancellationToken.None);
        var refused = await ReadAsync(unsigned.Id);
        Assert.Equal(JobState.Refused, refused.State);
        Assert.Equal(JobMaintenanceService.SignerTimeoutReason, refused.RefusalReason);
        Assert.Equal(JobState.Running, (await ReadAsync(running.Id)).State);

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(55));
        await service.RunAsync(CancellationToken.None);
        Assert.Equal(JobState.Expired, (await ReadAsync(queued.Id)).State);
        Assert.Equal(JobState.Lost, (await ReadAsync(running.Id)).State);
        Assert.Contains(endpoint.Id.ToString(), _fixture.Db.Bus.PayloadsFor(NotificationChannels.Jobs));

        _fixture.Db.Time.Advance(TimeSpan.FromDays(7));
        await service.RunAsync(CancellationToken.None);
        Assert.Equal(JobOutputState.Incomplete, (await ReadAsync(receiving.Id)).OutputState);
    }

    [Fact]
    public async Task Retention_removes_old_output_and_history_but_never_an_active_job()
    {
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-JOBS-OLD");
        var old = await JobAsync(endpoint, JobState.Succeeded, j => { j.CompletedAt = _fixture.Now; j.DeliveredAt = _fixture.Now; });
        var active = await JobAsync(endpoint, JobState.Running, j => { j.StartedAt = _fixture.Now; j.DeliveredAt = _fixture.Now; });
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.JobOutputChunks.Add(new JobOutputChunk { JobId = active.Id, ClientId = active.ClientId, Stream = JobStream.Stdout, Sequence = 0, Data = [1], ReceivedAt = _fixture.Now });
            await db.SaveChangesAsync();
        }

        _fixture.Db.Time.Advance(TimeSpan.FromDays(91));
        await _fixture.Retention().RunAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.False(await db.JobOutputChunks.AnyAsync(c => c.JobId == active.Id));
            Assert.True(await db.Jobs.AnyAsync(j => j.Id == old.Id));
        }

        // The fake clock is shared; move the rows instead of the clock for the 13-month rule.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var longAgo = _fixture.Now.AddDays(-401);
            await db.Jobs.Where(j => j.Id == old.Id || j.Id == active.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.CreatedAt, longAgo).SetProperty(j => j.ValidUntil, longAgo.AddHours(1)));
        }

        await _fixture.Retention().RunAsync(CancellationToken.None);
        await using var check = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await check.Jobs.AnyAsync(j => j.Id == old.Id));
        Assert.True(await check.Jobs.AnyAsync(j => j.Id == active.Id));
    }
}
