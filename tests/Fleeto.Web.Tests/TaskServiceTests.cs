using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Tasks in web (0.6.0): a task runs a library script of its own client or a global one, is linked within its client, and a
/// global one is changed only by a user with access to everything it is linked to. Run now starts a job per managed endpoint
/// as the user who pressed it, skips one that cannot run the script, and the export of a run keeps output a spreadsheet would
/// read as a formula as text. A script a task runs cannot be deleted.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class TaskServiceTests
{
    private readonly WebFixture _fixture;

    public TaskServiceTests(WebFixture fixture) => _fixture = fixture;

    private TaskService Tasks => _fixture.Services.GetRequiredService<TaskService>();

    private TaskInput Input(Guid scriptId, Guid? clientId = null, string? name = null) => new(
        name ?? "Inventory " + Guid.NewGuid().ToString("N")[..6], null, clientId, scriptId, JobRunAs.Service, CheckAppliesTo.All, true,
        TaskScheduleKind.Daily, "Europe/Brussels", DateOnly.FromDateTime(_fixture.Database.Time.GetUtcNow().UtcDateTime), 120, null, 1,
        WeekDays.None, 1, null, [], false, 10, true, TaskOverlap.Skip, true, AlertSeverity.Warning);

    [Fact]
    public async Task Run_now_starts_a_job_per_managed_endpoint_as_the_user_and_skips_one_that_cannot_run_the_script()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var windows = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-RUNNOW");
        var linux = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "LNX-RUNNOW");
        var free = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-FREE");
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == linux.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.OsPlatform, "linux"));
        }

        var (_, script, _) = await _fixture.Database.CreateTaskAsync();
        var technician = WebFixture.Technician();
        var created = await Tasks.SaveAsync(technician, null, Input(script.Id));
        Assert.True(created.Success, created.Problem);
        Assert.True((await Tasks.LinkAsync(technician, created.Value, LinkLevel.Site, site.Id)).Success);

        var result = await Tasks.RunNowAsync(technician, created.Value);

        Assert.True(result.Success, result.Problem);
        Assert.Equal(1, result.Value!.Created);
        Assert.Equal("LNX-RUNNOW", Assert.Single(result.Value.Skipped).Hostname);
        await using var check = _fixture.Database.DbFactory.CreateSystem();
        var job = await check.Jobs.SingleAsync(j => j.TaskId == created.Value);
        Assert.Equal((windows.Id, technician.UserId, JobState.PendingSignature), (job.EndpointId, job.InitiatedByUserId, job.State));
        Assert.True(await check.SigningRequests.AnyAsync(r => r.SubjectId == job.Id));
        var run = await check.AutomationTaskRuns.SingleAsync(r => r.Id == job.TaskRunId);
        Assert.Equal(TaskTrigger.Manual, run.Trigger);
        Assert.False(await check.AutomationTaskRunTargets.AnyAsync(t => t.RunId == run.Id && t.EndpointId == free.Id));

        var targets = await Tasks.GetRunAsync(technician, run.Id);
        Assert.Equal(["LNX-RUNNOW", "WS-RUNNOW"], targets.Select(t => t.Hostname).ToArray());
        Assert.Equal("Skipped", TaskService.StateText(targets[0]));
    }

    [Fact]
    public async Task A_task_stays_within_its_client_and_a_global_task_runs_only_a_global_script()
    {
        var client = await _fixture.Database.CreateClientAsync();
        var other = await _fixture.Database.CreateClientAsync();
        var otherSite = await _fixture.Database.CreateSiteAsync(other.Id);
        var (_, clientScript, _) = await _fixture.Database.CreateTaskAsync(clientId: client.Id);
        var admin = WebFixture.Admin();

        var global = await Tasks.SaveAsync(admin, null, Input(clientScript.Id));
        Assert.False(global.Success);
        Assert.Contains("for every client can only run a script for every client", global.Problem);

        var own = await Tasks.SaveAsync(admin, null, Input(clientScript.Id, client.Id));
        Assert.True(own.Success, own.Problem);
        var link = await Tasks.LinkAsync(admin, own.Value, LinkLevel.Site, otherSite.Id);
        Assert.False(link.Success);
        Assert.Contains("only be linked within that client", link.Problem);
    }

    [Fact]
    public async Task A_user_limited_to_one_client_links_a_global_task_there_but_cannot_change_one_used_elsewhere()
    {
        var mine = await _fixture.Database.CreateClientAsync();
        var theirs = await _fixture.Database.CreateClientAsync();
        var (_, script, _) = await _fixture.Database.CreateTaskAsync();
        var admin = WebFixture.Admin();
        var task = await Tasks.SaveAsync(admin, null, Input(script.Id));
        Assert.True((await Tasks.LinkAsync(admin, task.Value, LinkLevel.Client, theirs.Id)).Success);

        var limited = WebFixture.CallerWith(new RestrictedClientScope([mine.Id]), FleetoRoles.Technician);
        Assert.True((await Tasks.LinkAsync(limited, task.Value, LinkLevel.Client, mine.Id)).Success);

        var change = await Tasks.SaveAsync(limited, task.Value, Input(script.Id, name: "Changed"));
        Assert.False(change.Success);
        Assert.Contains("also used where your groups give you no access", change.Problem);

        var detail = await Tasks.GetAsync(limited, task.Value);
        Assert.Equal(mine.Id, Assert.Single(detail!.Links).ClientId);
    }

    [Fact]
    public async Task Read_only_users_see_tasks_but_cannot_create_link_or_run_them()
    {
        var (_, script, _) = await _fixture.Database.CreateTaskAsync();
        var readOnly = WebFixture.CallerWith(SystemClientScope.Instance, FleetoRoles.ReadOnly);

        Assert.Equal(ServiceResult.ForbiddenProblem, (await Tasks.SaveAsync(readOnly, null, Input(script.Id))).Problem);
        Assert.Equal(ServiceResult.ForbiddenProblem, (await Tasks.RunNowAsync(readOnly, Guid.NewGuid())).Problem);
        Assert.NotNull(await Tasks.ListAsync(readOnly));
    }

    [Fact]
    public async Task The_export_of_a_run_keeps_output_that_looks_like_a_formula_as_text()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-EXPORT");
        var (_, script, _) = await _fixture.Database.CreateTaskAsync();
        var admin = WebFixture.Admin();
        var task = await Tasks.SaveAsync(admin, null, Input(script.Id));
        await Tasks.LinkAsync(admin, task.Value, LinkLevel.Site, site.Id);
        var run = await Tasks.RunNowAsync(admin, task.Value);
        Assert.True(run.Success, run.Problem);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var job = await db.Jobs.SingleAsync(j => j.TaskId == task.Value);
            db.JobOutputChunks.Add(new JobOutputChunk
            {
                JobId = job.Id, ClientId = client.Id, Stream = JobStream.Stdout, Sequence = 0, Data = Encoding.UTF8.GetBytes("=HYPERLINK(\"x\")\nserial \"ABC\""),
                ReceivedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var export = await Tasks.ExportRunAsync(admin, run.Value!.BatchId);

        Assert.NotNull(export);
        Assert.EndsWith(".csv", export.Value.FileName);
        Assert.Contains("\"WS-EXPORT\"", export.Value.Csv);
        Assert.Contains("\"'=HYPERLINK(\"\"x\"\")\nserial \"\"ABC\"\"\"", export.Value.Csv);
    }

    [Fact]
    public async Task A_script_that_a_task_runs_cannot_be_deleted()
    {
        var (_, script, _) = await _fixture.Database.CreateTaskAsync();

        var result = await _fixture.Services.GetRequiredService<ScriptService>().DeleteAsync(WebFixture.Admin(), script.Id);

        Assert.False(result.Success);
        Assert.Contains("A task runs this script", result.Problem);
    }
}
