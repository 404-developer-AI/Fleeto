using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Data;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees the output of script checks in web (0.9.0): the latest run shows its output, a run that left its unchanged output out
/// shows that of the latest earlier run (also within one batch), an output cut off at the limit says how to raise it, an old agent
/// and a lost output say so, the runs of a block are listed with their status, other clients, agent-only endpoints and other check
/// types get nothing, and every shown output is audited without the output.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class ScriptOutputServiceTests
{
    private readonly WebFixture _fixture;

    public ScriptOutputServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private ScriptOutputService Outputs => _fixture.Services.GetRequiredService<ScriptOutputService>();

    private async Task<(Endpoint Endpoint, CheckDefinition Check)> SetupAsync(CheckType type = CheckType.Script, EndpointTier tier = EndpointTier.Managed)
    {
        var db = _fixture.Database;
        await db.LoadTestLicenseAsync(1000);
        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var endpoint = await db.CreateEndpointAsync(site, tier, "SRV-SCRIPT", EndpointClass.Server);
        var now = db.Time.GetUtcNow().UtcDateTime;
        var check = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = "Pending updates", Type = type,
            ParametersJson = type == CheckType.Script ? """{"output_limit_kb":"8"}""" : "{}", IntervalSeconds = 300, CreatedAt = now, UpdatedAt = now
        };
        await using var context = db.DbFactory.CreateSystem();
        context.CheckDefinitions.Add(check);
        await context.SaveChangesAsync();
        return (endpoint, check);
    }

    private async Task<CheckResult> ResultAsync(Endpoint endpoint, CheckDefinition check, DateTime time, double value, string? output,
        long? bytes, string? errorOutput = null, int? limit = 8192, string error = "")
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var result = new CheckResult
        {
            Time = time, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, CheckDefinitionId = check.Id, AgentTime = time, Value = value,
            Error = error, Detail = FirstLine(output), Output = output, ErrorOutput = output is null ? null : errorOutput ?? string.Empty,
            OutputBytes = bytes, OutputLimitBytes = bytes is null ? null : limit
        };
        db.CheckResults.Add(result);
        await db.SaveChangesAsync();
        return result;

        // Like the agent: the first line, at most 200 characters.
        static string FirstLine(string? text) => text is null ? string.Empty : text.Split('\n')[0] is var line && line.Length > 200 ? line[..200] : line;
    }

    [Fact]
    public async Task The_latest_run_shows_its_whole_output_and_showing_it_is_audited_without_the_output()
    {
        var (endpoint, check) = await SetupAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await ResultAsync(endpoint, check, now.AddMinutes(-10), 0, "old run", 7);
        await ResultAsync(endpoint, check, now.AddMinutes(-5), 1, "3 updates pending\nKB5001\nKB5002", 30, "a warning");

        var result = await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, null, null);

        Assert.True(result.Success, result.Problem);
        var view = result.Value!;
        Assert.Equal(ScriptOutputState.Available, view.State);
        Assert.Equal("3 updates pending\nKB5001\nKB5002", view.Stdout);
        Assert.Equal("a warning", view.Stderr);
        Assert.Equal(CheckStatus.Warning, view.Run.Status);
        Assert.Null(view.CutMessage);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = await db.AuditEntries.AsNoTracking()
            .SingleAsync(a => a.Action == AuditActions.ScriptOutputViewed && a.TargetId == endpoint.Id.ToString());
        Assert.DoesNotContain("KB5001", audit.DetailsJson ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_run_without_output_shows_that_of_the_latest_earlier_run_also_within_one_batch()
    {
        var (endpoint, check) = await SetupAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var batch = now.AddMinutes(-20);
        var first = await ResultAsync(endpoint, check, batch, 0, "first", 5);
        var unchanged = await ResultAsync(endpoint, check, batch, 0, null, 5);
        var changed = await ResultAsync(endpoint, check, batch, 0, "second", 6);
        var later = await ResultAsync(endpoint, check, now.AddMinutes(-10), 0, null, 6);

        Assert.Equal("first", (await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, unchanged.Id, unchanged.Time)).Value!.Stdout);
        Assert.Equal("first", (await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, first.Id, first.Time)).Value!.Stdout);
        Assert.Equal("second", (await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, changed.Id, changed.Time)).Value!.Stdout);
        Assert.Equal("second", (await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, later.Id, later.Time)).Value!.Stdout);
    }

    [Fact]
    public async Task An_output_cut_off_at_the_limit_says_where_to_raise_it()
    {
        var (endpoint, check) = await SetupAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await ResultAsync(endpoint, check, now.AddMinutes(-1), 0, new string('x', 8192), 20_000);

        var view = (await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, null, null)).Value!;

        Assert.True(view.Run.IsCut);
        Assert.NotNull(view.CutMessage);
        Assert.Contains("20 KB", view.CutMessage, StringComparison.Ordinal);
        Assert.Contains("first 8 KB", view.CutMessage, StringComparison.Ordinal);
        Assert.Contains("Checks tab of this endpoint", view.CutMessage, StringComparison.Ordinal);
        Assert.Contains("Output limit (KB)", view.CutMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_old_agent_and_a_lost_output_say_so()
    {
        var (endpoint, check) = await SetupAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var old = await ResultAsync(endpoint, check, now.AddMinutes(-30), 0, null, null);
        // Unchanged output whose run with output is older than the lookback: gone with retention or an evicted agent buffer.
        await ResultAsync(endpoint, check, now - ScriptOutputRules.Lookback - TimeSpan.FromHours(1), 0, "too old", 7);
        var lost = await ResultAsync(endpoint, check, now.AddMinutes(-20), 0, null, 7);

        var oldView = (await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, old.Id, old.Time)).Value!;
        Assert.Equal(ScriptOutputState.OldAgent, oldView.State);
        var lostView = (await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, lost.Id, lost.Time)).Value!;
        Assert.Equal(ScriptOutputState.Missing, lostView.State);
        Assert.Empty(lostView.Stdout);
    }

    [Fact]
    public async Task The_runs_of_a_block_are_listed_newest_first_with_their_status()
    {
        var (endpoint, check) = await SetupAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var from = now.AddHours(-2);
        await ResultAsync(endpoint, check, now.AddHours(-3), 0, "outside", 7);
        await ResultAsync(endpoint, check, from.AddMinutes(10), 0, "ok", 2);
        await ResultAsync(endpoint, check, from.AddMinutes(20), 2, "broken", 6);
        await ResultAsync(endpoint, check, from.AddMinutes(30), 0, null, null, error: "the script did not finish within 1m0s and was ended", limit: null);

        var runs = (await Outputs.GetRunsAsync(WebFixture.Technician(), endpoint.Id, check.Id, from, from.AddHours(1))).Value!;

        Assert.Equal(3, runs.Count);
        Assert.True(runs[0].At > runs[1].At && runs[1].At > runs[2].At);
        Assert.Equal(CheckStatus.Unknown, runs[0].Status);
        Assert.Equal(CheckStatus.Critical, runs[1].Status);
        Assert.Equal(CheckStatus.Ok, runs[2].Status);
    }

    [Fact]
    public async Task A_run_is_judged_by_the_exit_codes_of_its_script()
    {
        var user = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var (script, _) = await _fixture.Database.CreateScriptAsync(null, user.Id);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            (await db.Scripts.SingleAsync(s => s.Id == script.Id)).SuccessExitCodes = [3];
            await db.SaveChangesAsync();
        }

        var (endpoint, check) = await SetupAsync();
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            (await db.CheckDefinitions.SingleAsync(c => c.Id == check.Id)).ParametersJson = $$"""{"script":"{{script.Id}}"}""";
            await db.SaveChangesAsync();
        }

        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await ResultAsync(endpoint, check, now.AddMinutes(-1), 3, "done", 4);

        Assert.Equal(CheckStatus.Ok, (await Outputs.GetOutputAsync(WebFixture.Technician(), endpoint.Id, check.Id, null, null)).Value!.Run.Status);
    }

    [Fact]
    public async Task Other_clients_agent_only_endpoints_and_other_check_types_get_nothing()
    {
        var (endpoint, check) = await SetupAsync();
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await ResultAsync(endpoint, check, now.AddMinutes(-1), 0, "secret", 6);

        var other = WebFixture.CallerWith(new RestrictedClientScope([Guid.NewGuid()]), FleetoRoles.Technician);
        Assert.False((await Outputs.GetOutputAsync(other, endpoint.Id, check.Id, null, null)).Success);
        Assert.False((await Outputs.GetRunsAsync(other, endpoint.Id, check.Id, now.AddHours(-1), now)).Success);

        var (agentOnly, agentOnlyCheck) = await SetupAsync(tier: EndpointTier.AgentOnly);
        await ResultAsync(agentOnly, agentOnlyCheck, now.AddMinutes(-1), 0, "secret", 6);
        Assert.False((await Outputs.GetOutputAsync(WebFixture.Technician(), agentOnly.Id, agentOnlyCheck.Id, null, null)).Success);

        var (cpuEndpoint, cpu) = await SetupAsync(CheckType.CpuUsage);
        Assert.False((await Outputs.GetOutputAsync(WebFixture.Technician(), cpuEndpoint.Id, cpu.Id, null, null)).Success);
    }
}
