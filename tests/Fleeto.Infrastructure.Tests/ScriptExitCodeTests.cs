using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using ExitCodesMigration = Fleeto.Infrastructure.Migrations.ScriptExitCodes;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

// Every statement here is a constant of this test class, with values as parameters.
#pragma warning disable CA2100

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// What the exit codes of a script mean (0.6.0, decided 2026-10-15): the lists are read as typed, a code is in one list at most, a code
/// that is not listed keeps the default of where the script runs (a job: 0 success and the rest an error; a script check: 0 OK, 1 a
/// warning and the rest critical), the gateway reads the lists but nothing else of a script, a job keeps the exit status it ended
/// with, and the codes a task step counted as success move onto its script.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ScriptExitCodeTests
{
    private static readonly ExitCodeRules Developer = new([0], [4], [1, 2], [3]);

    private readonly TestDatabase _db;

    public ScriptExitCodeTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Theory]
    [InlineData("", new int[0])]
    [InlineData("1, 2", new[] { 1, 2 })]
    [InlineData("5-8;2 2", new[] { 2, 5, 6, 7, 8 })]
    [InlineData("-1", new[] { -1 })]
    [InlineData("-3--1", new[] { -3, -2, -1 })]
    [InlineData("0xC0000005", new[] { -1073741819 })]
    public void Lists_are_read_as_typed(string text, int[] codes)
    {
        var (parsed, error) = ScriptExitCodes.Parse(text);
        Assert.Null(error);
        Assert.Equal(codes, parsed);
    }

    [Theory]
    [InlineData("1, two", "\"two\" is not an exit code")]
    [InlineData("8-5", "runs backwards")]
    [InlineData("1-1000", "more than 100 codes")]
    [InlineData("2.5", "is not an exit code")]
    public void A_list_that_cannot_be_read_says_why(string text, string problem)
    {
        var (codes, error) = ScriptExitCodes.Parse(text);
        Assert.Empty(codes);
        Assert.Contains(problem, error);
    }

    [Fact]
    public void A_list_is_written_back_with_ranges()
    {
        Assert.Equal("1-3, 7, 9, 10", ScriptExitCodes.Format([10, 3, 1, 2, 7, 9, 2]));
        Assert.Equal(string.Empty, ScriptExitCodes.Format([]));
        Assert.Equal([1, 2, 3, 7, 9, 10], ScriptExitCodes.Parse(ScriptExitCodes.Format([10, 3, 1, 2, 7, 9])).Codes);
    }

    [Fact]
    public void A_code_is_in_one_list_at_most_and_the_lists_hold_100_codes_together()
    {
        Assert.Empty(ScriptExitCodes.Validate(Developer));
        Assert.Contains("Exit code 1 is listed as Warning and as Error", Assert.Single(ScriptExitCodes.Validate(new ExitCodeRules([], [], [1, 2], [1]))));
        Assert.Single(ScriptExitCodes.Validate(new ExitCodeRules(Enumerable.Range(1, 60).ToList(), [], [], Enumerable.Range(100, 41).ToList())));
    }

    [Theory]
    [InlineData(0, ExitStatus.Success)]
    [InlineData(1, ExitStatus.Warning)]
    [InlineData(2, ExitStatus.Warning)]
    [InlineData(3, ExitStatus.Error)]
    [InlineData(4, ExitStatus.Info)]
    [InlineData(5, ExitStatus.Error)]
    public void A_job_reads_the_lists_and_otherwise_succeeds_only_on_0(int code, ExitStatus status)
    {
        Assert.Equal(status, ScriptExitCodes.StatusForJob(Developer, code));
        Assert.Equal(code == 0 ? ExitStatus.Success : ExitStatus.Error, ScriptExitCodes.StatusForJob(ExitCodeRules.None, code));
        Assert.Equal(status != ExitStatus.Error, ScriptExitCodes.Succeeds(status));
    }

    [Fact]
    public void A_remapped_0_counts_as_the_list_says()
    {
        var rules = new ExitCodeRules([], [], [], [0]);
        Assert.Equal(ExitStatus.Error, ScriptExitCodes.StatusForJob(rules, 0));
        Assert.Equal(CheckStatus.Critical, ScriptExitCodes.StatusForCheck(rules, 0));
    }

    [Theory]
    [InlineData(0, CheckStatus.Ok, CheckStatus.Ok)]
    [InlineData(1, CheckStatus.Warning, CheckStatus.Warning)]
    [InlineData(2, CheckStatus.Warning, CheckStatus.Critical)]
    [InlineData(3, CheckStatus.Critical, CheckStatus.Critical)]
    [InlineData(4, CheckStatus.Ok, CheckStatus.Critical)]
    [InlineData(5, CheckStatus.Critical, CheckStatus.Critical)]
    public void A_script_check_reads_the_lists_and_otherwise_keeps_1_as_a_warning(int code, CheckStatus withRules, CheckStatus withoutRules)
    {
        var parameters = new Dictionary<string, string> { ["script"] = Guid.NewGuid().ToString("D") };
        Assert.Equal(withRules, CheckEvaluator.Evaluate(CheckType.Script, string.Empty, code, null, null, null, parameters, Developer));
        Assert.Equal(withoutRules, CheckEvaluator.Evaluate(CheckType.Script, string.Empty, code, null, null, null, parameters));
        Assert.Equal(CheckStatus.Critical, CheckEvaluator.Evaluate(CheckType.Script, string.Empty, 1.5, null, null, null, parameters, Developer));
    }

    [Fact]
    public async Task The_database_refuses_a_code_in_two_lists()
    {
        var user = await _db.CreateUserAsync(FleetoRoles.Technician);
        var (script, _) = await _db.CreateScriptAsync(null, user.Id);
        await using var db = _db.DbFactory.CreateSystem();

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Scripts.Where(s => s.Id == script.Id).ExecuteUpdateAsync(s => s
            .SetProperty(x => x.WarningExitCodes, new List<int> { 1 })
            .SetProperty(x => x.ErrorExitCodes, new List<int> { 1, 3 })));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task The_gateway_reads_the_lists_of_a_script_but_not_its_name()
    {
        var user = await _db.CreateUserAsync(FleetoRoles.Technician);
        var (script, _) = await _db.CreateScriptAsync(null, user.Id);

        Assert.Null(await AsGatewayAsync("""
            SELECT "SuccessExitCodes", "InfoExitCodes", "WarningExitCodes", "ErrorExitCodes" FROM "Scripts" WHERE "Id" = $1
            """, script.Id));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, (await AsGatewayAsync("""SELECT "Name" FROM "Scripts" WHERE "Id" = $1""", script.Id))?.SqlState);
    }

    [Fact]
    public async Task A_job_gets_its_exit_status_once_with_its_result_and_keeps_it()
    {
        var jobId = await JobAsync();
        await using (var db = _db.DbFactory.CreateSystem())
        {
            // Not before the job ended (the trigger refuses it; the check constraint would too).
            var early = await Assert.ThrowsAsync<PostgresException>(() => db.Jobs.Where(j => j.Id == jobId)
                .ExecuteUpdateAsync(s => s.SetProperty(j => j.ExitStatus, ExitStatus.Success)));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, early.SqlState);

            await db.Jobs.Where(j => j.Id == jobId).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.State, JobState.Succeeded)
                .SetProperty(j => j.Result, JobResult.Exited)
                .SetProperty(j => j.ExitCode, 1)
                .SetProperty(j => j.ExitStatus, ExitStatus.Warning)
                .SetProperty(j => j.CompletedAt, _db.Time.GetUtcNow().UtcDateTime));
        }

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "Jobs" SET "ExitStatus" = 'Success' WHERE "Id" = $1""", jobId))?.SqlState);
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "Jobs" SET "ExitStatus" = NULL WHERE "Id" = $1""", jobId))?.SqlState);
    }

    [Fact]
    public async Task A_script_with_jobs_can_be_deleted_and_its_jobs_keep_their_snapshot_but_never_point_at_another_script()
    {
        var jobId = await JobAsync();
        var user = await _db.CreateUserAsync(FleetoRoles.Technician);
        var (other, _) = await _db.CreateScriptAsync(null, user.Id);

        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege,
            (await AsGatewayAsync("""UPDATE "Jobs" SET "ScriptId" = $1 WHERE "Id" = $2""", other.Id, jobId))?.SqlState);

        await using var db = _db.DbFactory.CreateSystem();
        var job = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        await db.Scripts.Where(s => s.Id == job.ScriptId).ExecuteDeleteAsync();

        var kept = await db.Jobs.AsNoTracking().SingleAsync(j => j.Id == jobId);
        Assert.Equal(((Guid?)null, (Guid?)null, job.ScriptName, job.ScriptSha256), (kept.ScriptId, kept.ScriptVersionId, kept.ScriptName, kept.ScriptSha256));
    }

    [Fact]
    public async Task The_codes_a_step_counted_as_success_move_onto_its_script()
    {
        var (task, script, _) = await _db.CreateTaskAsync();
        var (_, other, _) = await _db.CreateTaskAsync();
        await _db.AddTaskStepsAsync(task,
            new AutomationTaskStep { Kind = TaskStepKind.Script, ScriptId = script.Id, SuccessExitCodes = [3] },
            new AutomationTaskStep { Kind = TaskStepKind.Script, ScriptId = script.Id, SuccessExitCodes = [5, 3] },
            new AutomationTaskStep { Kind = TaskStepKind.Script, ScriptId = other.Id });

        await using var db = _db.DbFactory.CreateSystem();
        await db.Database.ExecuteSqlRawAsync(ExitCodesMigration.MoveStepCodesSql);

        Assert.Equal([3, 5], (await db.Scripts.AsNoTracking().SingleAsync(s => s.Id == script.Id)).SuccessExitCodes);
        Assert.Empty((await db.Scripts.AsNoTracking().SingleAsync(s => s.Id == other.Id)).SuccessExitCodes);
    }

    private async Task<Guid> JobAsync()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var endpoint = await _db.CreateEndpointAsync(site, EndpointTier.Managed);
        var user = await _db.CreateUserAsync(FleetoRoles.Technician);
        var (script, version) = await _db.CreateScriptAsync(client.Id, user.Id);
        var now = _db.Time.GetUtcNow().UtcDateTime;
        var job = new Job
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, BatchId = Guid.NewGuid(), ScriptId = script.Id, ScriptVersionId = version.Id,
            ScriptName = script.Name, ScriptVersionNumber = 1, Language = script.Language, ScriptSha256 = version.Sha256, TimeoutSeconds = 600,
            MaxOutputBytes = 1024 * 1024, CreatedAt = now, ValidUntil = now.AddHours(1), InitiatedByUserId = user.Id, InitiatedByName = "Tess",
            State = JobState.Running, Payload = [1, 2, 3], Signature = new byte[64], SigningKeyId = "key", SignedAt = now, DeliveredAt = now
        };
        await using var db = _db.DbFactory.CreateSystem();
        db.Jobs.Add(job);
        await db.SaveChangesAsync();
        return job.Id;
    }

    /// <summary>Runs one statement as the gateway with the grants of production; returns the error, or null when it was allowed.</summary>
    private async Task<PostgresException?> AsGatewayAsync(string sql, params object[] values)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var roles = new NpgsqlCommand(string.Join('\n', Hosting.DatabaseRoles.Application.Select(r =>
                         $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;")),
                         connection, transaction))
        {
            await roles.ExecuteNonQueryAsync();
        }

        await using (var grants = new NpgsqlCommand(DatabaseGrants.BuildSql(), connection, transaction))
        {
            await grants.ExecuteNonQueryAsync();
        }

        await using (var role = new NpgsqlCommand("SET LOCAL ROLE fleeto_gateway", connection, transaction))
        {
            await role.ExecuteNonQueryAsync();
        }

        await using var command = new NpgsqlCommand(sql, connection, transaction);
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
        finally
        {
            await transaction.RollbackAsync();
        }
    }
}
