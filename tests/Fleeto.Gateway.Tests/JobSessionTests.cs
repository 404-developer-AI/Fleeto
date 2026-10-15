using System.Security.Cryptography;
using Fleeto.Core.Entities;
using Fleeto.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.EntityFrameworkCore;
using Endpoint = Fleeto.Core.Entities.Endpoint;
using JobResult = Fleeto.Protocol.Agent.V1.JobResult;
using JobStream = Fleeto.Protocol.Agent.V1.JobStream;

namespace Fleeto.Gateway.Tests;

/// <summary>
/// Guarantees job handling in the gateway (0.2.0): only queued, valid jobs reach a managed agent, once per connection; started,
/// output and completion are stored idempotently for the endpoint's own jobs and acknowledged after the write; output becomes
/// complete when every chunk arrived with the announced hashes, incomplete when a hash differs; the byte limit holds.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class JobSessionTests
{
    private readonly GatewayFixture _fixture;

    public JobSessionTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    private async Task<Job> CreateJobAsync(Endpoint endpoint, JobState state = JobState.Queued, TimeSpan? validity = null, long maxOutput = ScriptRules.DefaultMaxOutputBytes,
        bool delivered = false, Core.Entities.JobRunAs runAs = Core.Entities.JobRunAs.Service)
    {
        var user = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var (script, version) = await _fixture.Database.CreateScriptAsync(null, user.Id);
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, BatchId = Guid.NewGuid(), ScriptId = script.Id, ScriptVersionId = version.Id,
            ScriptName = script.Name, ScriptVersionNumber = 1, Language = script.Language, ScriptSha256 = version.Sha256, TimeoutSeconds = 600, MaxOutputBytes = maxOutput,
            CreatedAt = Now, ValidUntil = Now + (validity ?? TimeSpan.FromHours(1)), InitiatedByUserId = user.Id, InitiatedByName = "Tech", State = state,
            Payload = state == JobState.PendingSignature ? null : [1, 2, 3], Signature = state == JobState.PendingSignature ? null : new byte[64], SigningKeyId = "key",
            DeliveredAt = delivered ? Now : null, RunAs = runAs
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job;
    }

    private async Task<Job> ReadAsync(Guid id)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == id);
    }

    [Fact]
    public async Task Only_queued_valid_jobs_are_delivered_to_managed_agents_once_per_connection()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var queued = await CreateJobAsync(endpoint);
        await CreateJobAsync(endpoint, JobState.Cancelled);
        await CreateJobAsync(endpoint, JobState.PendingSignature);
        var session = await harness.OpenAsync(endpoint);

        var message = await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.Job);
        Assert.Equal(queued.Payload, message.Job.Payload.ToByteArray());
        Assert.NotNull((await ReadAsync(queued.Id)).DeliveredAt);

        await harness.Manager.DeliverJobsAsync([endpoint.Id], CancellationToken.None);
        Assert.DoesNotContain(GatewayHarness.Drain(session), m => m.BodyCase == ServerMessage.BodyOneofCase.Job);

        var agentOnly = await _fixture.CreateEndpointAsync();
        var notManaged = await CreateJobAsync(agentOnly);
        var agentOnlySession = await harness.OpenAsync(agentOnly);
        await harness.Manager.DeliverJobsAsync([agentOnly.Id], CancellationToken.None);
        Assert.DoesNotContain(GatewayHarness.Drain(agentOnlySession), m => m.BodyCase == ServerMessage.BodyOneofCase.Job);
        Assert.Null((await ReadAsync(notManaged.Id)).DeliveredAt);
    }

    [Fact]
    public async Task The_account_the_agent_reports_is_stored_only_for_a_job_that_runs_as_the_signed_in_user()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var asUser = await CreateJobAsync(endpoint, delivered: true, runAs: Core.Entities.JobRunAs.LoggedOnUser);
        var asService = await CreateJobAsync(endpoint, delivered: true);
        var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);

        foreach (var job in new[] { asUser, asService })
        {
            await harness.Manager.HandleAsync(session, new AgentMessage
            {
                JobStarted = new JobStarted { JobId = job.Id.ToString("D"), RunAsAccount = @"CONTOSO\jan" }
            }, CancellationToken.None);
        }

        Assert.Equal(@"CONTOSO\jan",(await ReadAsync(asUser.Id)).RunAsAccount);
        Assert.Null((await ReadAsync(asService.Id)).RunAsAccount);
    }

    [Fact]
    public async Task Started_output_and_completion_are_stored_once_and_the_output_is_verified()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var job = await CreateJobAsync(endpoint, delivered: true);
        var session = await harness.OpenAsync(endpoint);
        GatewayHarness.Drain(session);
        var id = job.Id.ToString("D");

        await harness.Manager.HandleAsync(session, new AgentMessage { JobStarted = new JobStarted { JobId = id } }, CancellationToken.None);
        Assert.Equal(JobState.Running, (await ReadAsync(job.Id)).State);

        byte[][] stdout = [RandomNumberGenerator.GetBytes(1000), RandomNumberGenerator.GetBytes(10)];
        for (var i = 0; i < stdout.Length; i++)
        {
            for (var copy = 0; copy < 2; copy++)
            {
                await harness.Manager.HandleAsync(session, new AgentMessage
                {
                    JobOutput = new JobOutput { JobId = id, Stream = JobStream.Stdout, Sequence = (ulong)i, Data = ByteString.CopyFrom(stdout[i]) }
                }, CancellationToken.None);
            }
        }

        var acks = GatewayHarness.Drain(session).Where(m => m.BodyCase == ServerMessage.BodyOneofCase.JobAck).Select(m => m.JobAck).ToList();
        Assert.Contains(acks, a => a.Kind == JobAckKind.Started);
        Assert.Equal(4, acks.Count(a => a.Kind == JobAckKind.Output));

        var afterChunks = await ReadAsync(job.Id);
        Assert.Equal(JobOutputState.Receiving, afterChunks.OutputState);
        Assert.Equal(1010, afterChunks.ReceivedOutputBytes);

        var completion = new JobCompletion
        {
            JobId = id, Result = JobResult.Exited, ExitCode = 3, Error = "",
            Stdout = new JobStreamSummary { Chunks = 2, Bytes = 1010, Sha256 = Convert.ToHexStringLower(SHA256.HashData(stdout[0].Concat(stdout[1]).ToArray())) },
            Stderr = new JobStreamSummary()
        };
        await harness.Manager.HandleAsync(session, new AgentMessage { JobCompletion = completion }, CancellationToken.None);
        await harness.Manager.HandleAsync(session, new AgentMessage { JobCompletion = completion }, CancellationToken.None);

        var done = await ReadAsync(job.Id);
        Assert.Equal(JobState.Failed, done.State);
        Assert.Equal(3, done.ExitCode);
        Assert.Equal(Core.Entities.JobResult.Exited, done.Result);
        Assert.Equal(JobOutputState.Complete, done.OutputState);
        Assert.Equal(2, GatewayHarness.Drain(session).Count(m => m.BodyCase == ServerMessage.BodyOneofCase.JobAck && m.JobAck.Kind == JobAckKind.Completion));

        // Completion first, then chunks with a hash that does not match: incomplete.
        var second = await CreateJobAsync(endpoint, delivered: true);
        var secondId = second.Id.ToString("D");
        await harness.Manager.HandleAsync(session, new AgentMessage
        {
            JobCompletion = new JobCompletion
            {
                JobId = secondId, Result = JobResult.Exited, ExitCode = 0, Stdout = new JobStreamSummary(),
                Stderr = new JobStreamSummary { Chunks = 1, Bytes = 3, Sha256 = new string('0', 64) }
            }
        }, CancellationToken.None);
        Assert.Equal(JobOutputState.Receiving, (await ReadAsync(second.Id)).OutputState);
        await harness.Manager.HandleAsync(session, new AgentMessage
        {
            JobOutput = new JobOutput { JobId = secondId, Stream = JobStream.Stderr, Sequence = 0, Data = ByteString.CopyFrom([1, 2, 3]) }
        }, CancellationToken.None);
        var incomplete = await ReadAsync(second.Id);
        Assert.Equal(JobState.Succeeded, incomplete.State);
        Assert.Equal(JobOutputState.Incomplete, incomplete.OutputState);
    }

    [Theory]
    [InlineData(0, JobState.Succeeded, Core.Entities.ExitStatus.Success)]
    [InlineData(1, JobState.Succeeded, Core.Entities.ExitStatus.Warning)]
    [InlineData(2, JobState.Succeeded, Core.Entities.ExitStatus.Warning)]
    [InlineData(3, JobState.Failed, Core.Entities.ExitStatus.Error)]
    [InlineData(4, JobState.Succeeded, Core.Entities.ExitStatus.Info)]
    [InlineData(5, JobState.Failed, Core.Entities.ExitStatus.Error)]
    [InlineData(-1, JobState.Succeeded, Core.Entities.ExitStatus.Success)]
    public async Task The_exit_code_rules_of_the_script_decide_whether_a_job_succeeded(int exitCode, JobState state, Core.Entities.ExitStatus status)
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var job = await CreateJobAsync(endpoint, delivered: true);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            // 0 = success and 1, 2 = warning, 3 = error, 4 = info as the developer described; -1 is listed as success, 5 is not listed.
            await db.Scripts.Where(s => s.Id == job.ScriptId).ExecuteUpdateAsync(s => s
                .SetProperty(x => x.SuccessExitCodes, new List<int> { -1 })
                .SetProperty(x => x.WarningExitCodes, new List<int> { 1, 2 })
                .SetProperty(x => x.ErrorExitCodes, new List<int> { 3 })
                .SetProperty(x => x.InfoExitCodes, new List<int> { 4 }));
        }

        var session = await harness.OpenAsync(endpoint);
        await harness.Manager.HandleAsync(session, new AgentMessage
        {
            JobCompletion = new JobCompletion
            {
                JobId = job.Id.ToString("D"), Result = JobResult.Exited, ExitCode = exitCode, Stdout = new JobStreamSummary(), Stderr = new JobStreamSummary()
            }
        }, CancellationToken.None);

        var done = await ReadAsync(job.Id);
        Assert.Equal((state, status, exitCode), (done.State, done.ExitStatus!.Value, done.ExitCode!.Value));
    }

    [Fact]
    public async Task A_script_without_rules_succeeds_only_on_0_and_a_job_that_did_not_exit_has_no_exit_status()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var plain = await CreateJobAsync(endpoint, delivered: true);
        var timedOut = await CreateJobAsync(endpoint, delivered: true);
        var deleted = await CreateJobAsync(endpoint, delivered: true);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            // A job keeps running when its script is deleted; its exit code then gets the default.
            await db.Scripts.Where(s => s.Id == deleted.ScriptId).ExecuteDeleteAsync();
        }

        var session = await harness.OpenAsync(endpoint);
        foreach (var (job, result, code) in new[] { (plain, JobResult.Exited, 1), (timedOut, JobResult.TimedOut, 0), (deleted, JobResult.Exited, 0) })
        {
            await harness.Manager.HandleAsync(session, new AgentMessage
            {
                JobCompletion = new JobCompletion
                {
                    JobId = job.Id.ToString("D"), Result = result, ExitCode = code, Stdout = new JobStreamSummary(), Stderr = new JobStreamSummary()
                }
            }, CancellationToken.None);
        }

        var plainDone = await ReadAsync(plain.Id);
        Assert.Equal((JobState.Failed, Core.Entities.ExitStatus.Error), (plainDone.State, plainDone.ExitStatus!.Value));
        var timedOutDone = await ReadAsync(timedOut.Id);
        Assert.Equal((JobState.Failed, (Core.Entities.ExitStatus?)null), (timedOutDone.State, timedOutDone.ExitStatus));
        var deletedDone = await ReadAsync(deleted.Id);
        Assert.Equal((JobState.Succeeded, Core.Entities.ExitStatus.Success), (deletedDone.State, deletedDone.ExitStatus!.Value));
    }

    [Fact]
    public async Task Another_endpoints_job_and_output_beyond_the_limit_are_not_stored()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var other = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var foreign = await CreateJobAsync(other, delivered: true);
        var limited = await CreateJobAsync(endpoint, maxOutput: 10, delivered: true);
        var session = await harness.OpenAsync(endpoint);

        await harness.Manager.HandleAsync(session, new AgentMessage { JobStarted = new JobStarted { JobId = foreign.Id.ToString("D") } }, CancellationToken.None);
        await harness.Manager.HandleAsync(session, new AgentMessage
        {
            JobCompletion = new JobCompletion { JobId = foreign.Id.ToString("D"), Result = JobResult.Exited, ExitCode = 0 }
        }, CancellationToken.None);
        Assert.Equal(JobState.Queued, (await ReadAsync(foreign.Id)).State);

        await harness.Manager.HandleAsync(session, new AgentMessage
        {
            JobOutput = new JobOutput { JobId = limited.Id.ToString("D"), Stream = JobStream.Stdout, Sequence = 0, Data = ByteString.CopyFrom(new byte[65536]) }
        }, CancellationToken.None);
        await harness.Manager.HandleAsync(session, new AgentMessage
        {
            JobOutput = new JobOutput { JobId = limited.Id.ToString("D"), Stream = JobStream.Stdout, Sequence = 1, Data = ByteString.CopyFrom(new byte[100]) }
        }, CancellationToken.None);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(1, await db.JobOutputChunks.CountAsync(c => c.JobId == limited.Id));
        Assert.Equal(65536, (await ReadAsync(limited.Id)).ReceivedOutputBytes);
    }
}
