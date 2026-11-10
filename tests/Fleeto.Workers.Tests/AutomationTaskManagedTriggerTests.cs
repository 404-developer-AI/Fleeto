using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Testing;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Jobs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

// Every statement here is a constant of this test class, with values as parameters.
#pragma warning disable CA2100

namespace Fleeto.Workers.Tests;

/// <summary>
/// "When an endpoint becomes managed" starts a task from Endpoints.ManagedSince, which only the tier service in web writes (security
/// review of 0.9.0, migration EndpointColumnsPerRole). With the production grants, neither the gateway nor the workers can write
/// ManagedSince or the tier, so neither can start such a run again; the legitimate switch to managed starts it once.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class AutomationTaskManagedTriggerTests : IAsyncLifetime
{
    private readonly WorkersFixture _fixture;
    private NpgsqlDataSource? _workersSource;
    private NpgsqlDataSource? _gatewaySource;
    private IFleetoDbContextFactory _workers = null!;

    public AutomationTaskManagedTriggerTests(WorkersFixture fixture) => _fixture = fixture;

    private DateTime Now => _fixture.Now;

    public async Task InitializeAsync()
    {
        await using (var command = _fixture.Db.DataSource.CreateCommand(string.Join('\n', DatabaseRoles.Application.Select(r =>
                         $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;"))))
        {
            await command.ExecuteNonQueryAsync();
        }

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Database.ExecuteSqlRawAsync(DatabaseGrants.BuildSql());
        }

        var workers = new NpgsqlConnectionStringBuilder(_fixture.Db.ConnectionString) { Options = "-c role=" + DatabaseRoles.Workers };
        _workersSource = new NpgsqlDataSourceBuilder(workers.ConnectionString).Build();
        _workers = new FleetoDbContextFactory(FleetoDbContextFactory.BuildOptions(_workersSource));
        var gateway = new NpgsqlConnectionStringBuilder(_fixture.Db.ConnectionString) { Options = "-c role=" + DatabaseRoles.Gateway };
        _gatewaySource = new NpgsqlDataSourceBuilder(gateway.ConnectionString).Build();
        await _fixture.Db.LoadTestLicenseAsync(1000);
    }

    public async Task DisposeAsync()
    {
        if (_workersSource is not null)
        {
            await _workersSource.DisposeAsync();
        }

        if (_gatewaySource is not null)
        {
            await _gatewaySource.DisposeAsync();
        }
    }

    private AutomationTaskService Service() =>
        new(_workers, _fixture.Db.Bus, new AlertNotificationService(_fixture.Db.Time), _fixture.Db.Licenses, _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<AutomationTaskService>.Instance);

    private async Task PassAsync(AutomationTaskService service)
    {
        await service.StartRunsAsync(CancellationToken.None);
        await service.FollowOutcomesAsync(CancellationToken.None);
        await service.StartJobsAsync(CancellationToken.None);
    }

    private async Task<List<Job>> JobsAsync(Guid endpointId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Jobs.AsNoTracking().Where(j => j.EndpointId == endpointId).OrderBy(j => j.CreatedAt).ToListAsync();
    }

    private async Task EndJobAsync(Guid jobId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
        job.Signature = new byte[64];
        job.State = JobState.Succeeded;
        job.ExitCode = 0;
        job.CompletedAt = Now;
        await db.SaveChangesAsync();
    }

    /// <summary>Runs and commits one statement as a role with the production grants; returns the error or null.</summary>
    private static async Task<PostgresException?> AsRoleAsync(NpgsqlDataSource source, string sql, params object[] values)
    {
        await using var command = source.CreateCommand(sql);
        foreach (var value in values)
        {
            command.Parameters.Add(new NpgsqlParameter { Value = value });
        }

        try
        {
            await command.ExecuteNonQueryAsync();
            return null;
        }
        catch (PostgresException error)
        {
            return error;
        }
    }

    [Fact]
    public async Task Only_the_switch_to_managed_starts_a_managed_run_not_a_write_of_ManagedSince_by_the_gateway_or_the_workers()
    {
        var client = await _fixture.Db.CreateClientAsync();
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Managed trigger " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.AgentOnly, "MT-" + Guid.NewGuid().ToString("N")[..6]);
        var (task, _, _) = await _fixture.Db.CreateTaskAsync(site, t => t.OnManaged = true);
        var service = Service();
        await PassAsync(service);

        // The legitimate switch to managed (as the tier service does it) starts the task once.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(1));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var row = await db.Endpoints.SingleAsync(e => e.Id == endpoint.Id);
            (row.Tier, row.ManagedSince) = (EndpointTier.Managed, Now);
            await db.SaveChangesAsync();
        }

        await PassAsync(service);
        await PassAsync(service);
        var first = Assert.Single(await JobsAsync(endpoint.Id));
        Assert.Equal(task.Id, first.TaskId);
        await EndJobAsync(first.Id);

        // Past the 15-minute "already ran on this endpoint" cooldown of StartJobsAsync, which would only mark the target skipped.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(20));
        foreach (var source in new[] { _gatewaySource!, _workersSource! })
        {
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await AsRoleAsync(source, """UPDATE "Endpoints" SET "ManagedSince" = $1 WHERE "Id" = $2""", Now, endpoint.Id))?.SqlState);
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
                (await AsRoleAsync(source, """UPDATE "Endpoints" SET "Tier" = 'AgentOnly' WHERE "Id" = $1""", endpoint.Id))?.SqlState);
        }

        await PassAsync(service);
        await PassAsync(service);

        Assert.Single(await JobsAsync(endpoint.Id));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Single(await db.AutomationTaskRuns.AsNoTracking()
                .Where(r => r.AutomationTaskId == task.Id && r.Trigger == TaskTrigger.Managed).ToListAsync());
        }
    }

    [Fact]
    public async Task The_gateway_cannot_write_ManagedSince_on_an_agent_only_endpoint()
    {
        var client = await _fixture.Db.CreateClientAsync();
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Managed trigger b " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.AgentOnly, "MTB-" + Guid.NewGuid().ToString("N")[..6]);
        var (task, _, _) = await _fixture.Db.CreateTaskAsync(site, t => t.OnManaged = true);
        var service = Service();
        await PassAsync(service);

        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsRoleAsync(_gatewaySource!, """UPDATE "Endpoints" SET "ManagedSince" = $1 WHERE "Id" = $2""", Now, endpoint.Id))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsRoleAsync(_gatewaySource!, """UPDATE "Endpoints" SET "Tier" = 'Managed' WHERE "Id" = $1""", endpoint.Id))?.SqlState);

        await PassAsync(service);
        await PassAsync(service);

        Assert.Empty(await JobsAsync(endpoint.Id));
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Null((await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id)).ManagedSince);
        Assert.False(await db.AutomationTaskRuns.AnyAsync(r => r.AutomationTaskId == task.Id));
    }
}
