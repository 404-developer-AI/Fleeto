using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Testing;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Guarantees the database rules of tasks (0.6.0) with the production grants: the workers write a job without a user only for a
/// run Fleeto started of an enabled task, with that task's script and account, and ask for its signature only for such a job; a
/// link never reaches a task of another client; a task of a client runs no script of another client; and what a job asked stays
/// as it is, except that its task and run become empty when they are deleted.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AutomationTaskRuleTests
{
    private readonly TestDatabase _db;

    public AutomationTaskRuleTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Theory]
    [InlineData("valid", true)]
    [InlineData("other-script", false)]
    [InlineData("disabled", false)]
    [InlineData("manual-run", false)]
    [InlineData("other-account", false)]
    public async Task The_workers_write_a_job_without_a_user_only_for_a_run_of_an_enabled_task(string variant, bool allowed)
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-TASKORIGIN");
        var (task, script, version) = await _db.CreateTaskAsync(site, t => t.Enabled = variant != "disabled");
        var (_, otherScript, otherVersion) = await _db.CreateTaskAsync();
        var runId = await CreateRunAsync(task.Id, client.Id, variant == "manual-run" ? TaskTrigger.Manual : TaskTrigger.Schedule);

        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_workers");
        var jobId = Guid.NewGuid();
        await using var insert = InsertJob(connection, transaction, jobId, client.Id, endpoint.Id, task.Id, runId,
            variant == "other-script" ? otherScript.Id : script.Id, variant == "other-script" ? otherVersion.Id : version.Id,
            variant == "other-account" ? "LoggedOnUser" : "Service");

        if (allowed)
        {
            await insert.ExecuteNonQueryAsync();
            await using var request = SigningRequest(connection, transaction, client.Id, jobId);
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
    public async Task Web_cannot_write_a_job_of_a_task_without_a_user()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-TASKWEB");
        var (task, script, version) = await _db.CreateTaskAsync(site);
        var runId = await CreateRunAsync(task.Id, client.Id, TaskTrigger.Manual);

        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_web");
        await using (var automatic = InsertJob(connection, transaction, Guid.NewGuid(), client.Id, endpoint.Id, task.Id, runId, script.Id, version.Id, "Service"))
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => automatic.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_link_or_a_run_never_reaches_a_task_of_another_client_and_a_client_task_runs_no_script_of_another_client()
    {
        var client = await _db.CreateClientAsync();
        var other = await _db.CreateClientAsync();
        var otherSite = await _db.CreateSiteAsync(other.Id);
        var (task, _, _) = await _db.CreateTaskAsync(clientId: client.Id);

        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.SiteAutomationTasks.Add(new SiteAutomationTask { SiteId = otherSite.Id, ClientId = other.Id, AutomationTaskId = task.Id, CreatedAt = DateTime.UtcNow });
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Contains("another client", error.InnerException?.Message ?? error.Message);
        }

        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.AutomationTaskRuns.Add(new AutomationTaskRun
            {
                Id = Guid.NewGuid(), AutomationTaskId = task.Id, ClientId = other.Id, Trigger = TaskTrigger.Schedule, OccurrenceAt = DateTime.UtcNow,
                InitiatedByName = "Fleeto", CreatedAt = DateTime.UtcNow
            });
            await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
        }

        var (_, otherScript, _) = await _db.CreateTaskAsync(clientId: other.Id);
        await using (var db = _db.DbFactory.CreateSystem())
        {
            var stored = await db.AutomationTasks.FindAsync(task.Id);
            stored!.ScriptId = otherScript.Id;
            var error = await Assert.ThrowsAnyAsync<Exception>(() => db.SaveChangesAsync());
            Assert.Contains("own client", error.InnerException?.Message ?? error.Message);
        }
    }

    [Fact]
    public async Task A_job_keeps_its_task_and_run_until_they_are_deleted()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed, "WS-TASKBIND");
        var (task, script, version) = await _db.CreateTaskAsync(site);
        var (otherTask, _, _) = await _db.CreateTaskAsync(site);
        var runId = await CreateRunAsync(task.Id, client.Id, TaskTrigger.Schedule);
        var jobId = Guid.NewGuid();
        await using (var connection = await _db.DataSource.OpenConnectionAsync())
        await using (var transaction = await connection.BeginTransactionAsync())
        {
            await using var insert = InsertJob(connection, transaction, jobId, client.Id, endpoint.Id, task.Id, runId, script.Id, version.Id, "Service");
            await insert.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }

        await using (var connection = await _db.DataSource.OpenConnectionAsync())
        {
#pragma warning disable CA2100 // test-only statements built from generated ids
            await using var move = new NpgsqlCommand($"""UPDATE "Jobs" SET "TaskId" = '{otherTask.Id}' WHERE "Id" = '{jobId}'""", connection);
#pragma warning restore CA2100
            var error = await Assert.ThrowsAsync<PostgresException>(() => move.ExecuteNonQueryAsync());
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        }

        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.AutomationTasks.Remove((await db.AutomationTasks.FindAsync(task.Id))!);
            await db.SaveChangesAsync();
            var job = await db.Jobs.FindAsync(jobId);
            Assert.NotNull(job);
            Assert.Null(job.TaskId);
            Assert.Null(job.TaskRunId);
        }
    }

    private async Task<Guid> CreateRunAsync(Guid taskId, Guid clientId, TaskTrigger trigger)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var run = new AutomationTaskRun
        {
            Id = Guid.NewGuid(), AutomationTaskId = taskId, ClientId = clientId, Trigger = trigger, OccurrenceAt = DateTime.UtcNow,
            InitiatedByName = "Fleeto", CreatedAt = DateTime.UtcNow
        };
        db.AutomationTaskRuns.Add(run);
        await db.SaveChangesAsync();
        return run.Id;
    }

    private static NpgsqlCommand InsertJob(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid id, Guid clientId, Guid endpointId,
        Guid taskId, Guid runId, Guid scriptId, Guid versionId, string runAs)
    {
        var insert = new NpgsqlCommand(
            """
            INSERT INTO "Jobs" ("Id","ClientId","EndpointId","BatchId","Type","ScriptId","ScriptVersionId","ScriptName","ScriptVersionNumber","Language",
              "ScriptSha256","TimeoutSeconds","MaxOutputBytes","RunAs","TaskId","TaskRunId","CreatedAt","ValidUntil","InitiatedByUserId","InitiatedByName",
              "State","ReceivedOutputBytes","OutputTruncated","OutputState")
            VALUES (@id, @client, @endpoint, @run, 'Script', @script, @version, 'Inventory', 1, 'PowerShell', repeat('a', 64), 600, 1024, @runAs,
              @task, @run, now(), now() + interval '1 day', '00000000-0000-0000-0000-000000000000', 'Fleeto (task)', 'PendingSignature', 0, false, 'None')
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("client", clientId);
        insert.Parameters.AddWithValue("endpoint", endpointId);
        insert.Parameters.AddWithValue("task", taskId);
        insert.Parameters.AddWithValue("run", runId);
        insert.Parameters.AddWithValue("script", scriptId);
        insert.Parameters.AddWithValue("version", versionId);
        insert.Parameters.AddWithValue("runAs", runAs);
        return insert;
    }

    private static NpgsqlCommand SigningRequest(NpgsqlConnection connection, NpgsqlTransaction transaction, Guid clientId, Guid jobId)
    {
        var request = new NpgsqlCommand(
            """INSERT INTO "SigningRequests" ("Id","ClientId","Kind","SubjectId","Payload","RequestedBy","State","CreatedAt") VALUES (gen_random_uuid(), @client, 'Job', @job, ''::bytea, 'test', 'Pending', now())""",
            connection, transaction);
        request.Parameters.AddWithValue("client", clientId);
        request.Parameters.AddWithValue("job", jobId);
        return request;
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
