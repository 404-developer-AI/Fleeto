using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Guarantees the database rules of the steps of tasks (0.6.0, steps) with the production grants: the workers write the job of a
/// step only as that step says (its script and account, or a restart for a restart step) in a run Fleeto started or a run started
/// by hand that names its user; nobody else writes a restart; only web writes a run started by hand and only such a run names a
/// user; what started a run and the step of a job never change; a step runs once per run and endpoint; and a step runs a global
/// script or one of its task's client.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AutomationTaskStepRuleTests
{
    private readonly TestDatabase _db;

    public AutomationTaskStepRuleTests(DatabaseFixture fixture) => _db = fixture.Database;

    private sealed record Scope(Client Client, Endpoint Endpoint, AutomationTask Task, ScriptVersion Version, Script OtherScript,
        AutomationTaskStep ScriptStep, AutomationTaskStep RestartStep);

    private async Task<Scope> ScopeAsync()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-STEPRULE");
        var (task, script, version) = await _db.CreateTaskAsync(site);
        var (_, otherScript, _) = await _db.CreateTaskAsync();
        var steps = await _db.AddTaskStepsAsync(task, new AutomationTaskStep { Kind = TaskStepKind.Script, ScriptId = script.Id },
            new AutomationTaskStep { Kind = TaskStepKind.Restart, RestartWaitMinutes = 30 });
        return new Scope(client, endpoint, task, version, otherScript, steps[0], steps[1]);
    }

    private async Task<Guid> RunAsync(Scope scope, TaskTrigger trigger, Guid? userId = null)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var run = new AutomationTaskRun
        {
            Id = Guid.NewGuid(), AutomationTaskId = scope.Task.Id, ClientId = scope.Client.Id, Trigger = trigger, OccurrenceAt = DateTime.UtcNow,
            InitiatedByName = "Fleeto", InitiatedByUserId = userId, CreatedAt = DateTime.UtcNow
        };
        db.AutomationTaskRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    [Theory]
    [InlineData("script step", true)]
    [InlineData("another script for the step", false)]
    [InlineData("restart for the restart step", true)]
    [InlineData("restart for the script step", false)]
    [InlineData("run by hand with its user", true)]
    [InlineData("run by hand without a user", false)]
    public async Task The_workers_write_the_job_of_a_step_only_as_the_step_says(string variant, bool allowed)
    {
        var scope = await ScopeAsync();
        var runId = await RunAsync(scope, variant.StartsWith("run by hand") ? TaskTrigger.Manual : TaskTrigger.Schedule,
            variant == "run by hand with its user" ? Guid.NewGuid() : null);
        var restart = variant.StartsWith("restart");
        var step = variant == "restart for the restart step" ? scope.RestartStep : scope.ScriptStep;
        Guid? scriptId = restart ? null : variant == "another script for the step" ? scope.OtherScript.Id : scope.Version.ScriptId;

        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await AsWorkersAsync(connection);
        var jobId = Guid.NewGuid();
        await using var insert = InsertJob(connection, transaction, jobId, scope, runId, step.Id, restart ? "Restart" : "Script", scriptId,
            restart ? null : scope.Version.Id, Guid.Empty);

        if (allowed)
        {
            await insert.ExecuteNonQueryAsync();
            await using var request = new NpgsqlCommand(
                """INSERT INTO "SigningRequests" ("Id","ClientId","Kind","SubjectId","Payload","RequestedBy","State","CreatedAt") VALUES (gen_random_uuid(), @client, 'Job', @job, ''::bytea, 'test', 'Pending', now())""",
                connection, transaction);
            request.Parameters.AddWithValue("client", scope.Client.Id);
            request.Parameters.AddWithValue("job", jobId);
            await request.ExecuteNonQueryAsync();
        }
        else
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task Web_never_writes_a_restart_even_for_a_user()
    {
        var scope = await ScopeAsync();
        var runId = await RunAsync(scope, TaskTrigger.Schedule);

        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_web");
        await using var insert = InsertJob(connection, transaction, Guid.NewGuid(), scope, runId, null, "Restart", null, null, Guid.NewGuid());

        var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        await transaction.RollbackAsync();
    }

    [Theory]
    [InlineData("fleeto_workers", "Manual", true)]
    [InlineData("fleeto_workers", "Schedule", false)]
    [InlineData("fleeto_web", "Schedule", true)]
    [InlineData("fleeto_web", "Manual", false)]
    public async Task Only_web_writes_a_run_started_by_hand_and_only_such_a_run_names_a_user(string role, string trigger, bool refused)
    {
        var scope = await ScopeAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await EnsureRolesAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, $"SET LOCAL ROLE {role}");
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO "AutomationTaskRuns" ("Id","AutomationTaskId","ClientId","Trigger","OccurrenceAt","InitiatedByName","InitiatedByUserId","CreatedAt")
            VALUES (gen_random_uuid(), @task, @client, @trigger, now(), 'Tess', CASE WHEN @trigger = 'Manual' THEN gen_random_uuid() END, now())
            """, connection, transaction);
        insert.Parameters.AddWithValue("task", scope.Task.Id);
        insert.Parameters.AddWithValue("client", scope.Client.Id);
        insert.Parameters.AddWithValue("trigger", trigger);

        if (refused)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => insert.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }
        else
        {
            await insert.ExecuteNonQueryAsync();
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task What_started_a_run_and_the_step_of_a_job_never_change_and_a_step_runs_once_per_run_and_endpoint()
    {
        var scope = await ScopeAsync();
        var runId = await RunAsync(scope, TaskTrigger.Schedule);
        await using (var db = _db.DbFactory.CreateSystem())
        {
            var run = await db.AutomationTaskRuns.SingleAsync(r => r.Id == runId);
            run.Trigger = TaskTrigger.CheckAlert;
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Contains("cannot be changed", error.InnerException?.Message ?? error.Message);
        }

        var jobId = await StepJobAsync(scope, runId, scope.ScriptStep);
        await using (var db = _db.DbFactory.CreateSystem())
        {
            var job = await db.Jobs.SingleAsync(j => j.Id == jobId);
            job.TaskStepId = scope.RestartStep.Id;
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Contains("cannot be changed", error.InnerException?.Message ?? error.Message);
        }

        var duplicate = await Assert.ThrowsAnyAsync<Exception>(() => StepJobAsync(scope, runId, scope.ScriptStep));
        Assert.Contains("IX_Jobs_TaskStep", duplicate.InnerException?.Message ?? duplicate.Message);
    }

    [Fact]
    public async Task A_step_of_a_task_of_a_client_never_runs_a_script_of_another_client()
    {
        var client = await _db.CreateClientAsync();
        var other = await _db.CreateClientAsync();
        var (task, _, _) = await _db.CreateTaskAsync(clientId: client.Id);
        var (_, otherScript, _) = await _db.CreateTaskAsync(clientId: other.Id);

        var error = await Assert.ThrowsAnyAsync<Exception>(() =>
            _db.AddTaskStepsAsync(task, new AutomationTaskStep { Kind = TaskStepKind.Script, ScriptId = otherScript.Id }));

        Assert.Contains("global script or one of the client of its task", error.InnerException?.Message ?? error.Message);
    }

    private async Task<Guid> StepJobAsync(Scope scope, Guid runId, AutomationTaskStep step)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = scope.Client.Id, EndpointId = scope.Endpoint.Id, BatchId = runId, Type = JobType.Script,
            ScriptId = scope.Version.ScriptId, ScriptVersionId = scope.Version.Id, ScriptName = "Inventory", ScriptVersionNumber = 1,
            ScriptSha256 = scope.Version.Sha256, TimeoutSeconds = 600, MaxOutputBytes = 1024, TaskId = scope.Task.Id, TaskRunId = runId,
            TaskStepId = step.Id, CreatedAt = DateTime.UtcNow, ValidUntil = DateTime.UtcNow.AddHours(1), InitiatedByUserId = Guid.Empty,
            InitiatedByName = "Fleeto"
        };
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    private async Task AsWorkersAsync(NpgsqlConnection connection)
    {
        await EnsureRolesAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_workers");
    }

    private static NpgsqlCommand InsertJob(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Scope scope, Guid runId, Guid? stepId,
        string type, Guid? scriptId, Guid? versionId, Guid initiator)
    {
        var insert = new NpgsqlCommand(
            """
            INSERT INTO "Jobs" ("Id","ClientId","EndpointId","BatchId","Type","ScriptId","ScriptVersionId","ScriptName","ScriptVersionNumber","Language",
              "ScriptSha256","TimeoutSeconds","MaxOutputBytes","RunAs","TaskId","TaskRunId","TaskStepId","CreatedAt","ValidUntil","InitiatedByUserId",
              "InitiatedByName","State","ReceivedOutputBytes","OutputTruncated","OutputState")
            VALUES (@id, @client, @endpoint, @run, @type, @script, @version, 'Step', 1, 'PowerShell', '', 600, 1024, 'Service',
              @task, @run, @step, now(), now() + interval '1 day', @initiator, 'Fleeto (task)', 'PendingSignature', 0, false, 'None')
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("client", scope.Client.Id);
        insert.Parameters.AddWithValue("endpoint", scope.Endpoint.Id);
        insert.Parameters.AddWithValue("run", runId);
        insert.Parameters.AddWithValue("type", type);
        insert.Parameters.AddWithValue("script", (object?)scriptId ?? DBNull.Value);
        insert.Parameters.AddWithValue("version", (object?)versionId ?? DBNull.Value);
        insert.Parameters.AddWithValue("task", scope.Task.Id);
        insert.Parameters.AddWithValue("step", (object?)stepId ?? DBNull.Value);
        insert.Parameters.AddWithValue("initiator", initiator);
        return insert;
    }

    private async Task EnsureRolesAsync()
    {
        await using var command = _db.DataSource.CreateCommand(string.Join('\n', Hosting.DatabaseRoles.Application.Select(r =>
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;")));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // test-only statements built from constants
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
