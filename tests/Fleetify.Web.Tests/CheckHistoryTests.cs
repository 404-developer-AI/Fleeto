using Fleetify.Core.Domain;
using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Web.Components.Shared;
using Fleetify.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// Guarantees the check history in web: the hour and day come from raw results in aligned buckets, week, month and year from the
/// rollups, every bucket of the range is present, errors and missing responses stay out of the values, other clients and agent-only
/// endpoints get nothing, and the chart geometry breaks the line at a gap.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class CheckHistoryTests
{
    private readonly WebFixture _fixture;

    public CheckHistoryTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private CheckHistoryService History => _fixture.Services.GetRequiredService<CheckHistoryService>();

    private async Task<(Client Client, Endpoint Endpoint, CheckDefinition Check)> SetupAsync(CheckType type, string parameters, double? warning,
        EndpointTier tier = EndpointTier.Managed)
    {
        var db = _fixture.Database;
        await db.LoadTestLicenseAsync(1000);
        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var endpoint = await db.CreateEndpointAsync(site, tier, "SRV-CHART", EndpointClass.Server);
        var now = db.Time.GetUtcNow().UtcDateTime;
        var check = new CheckDefinition
        {
            Id = Guid.NewGuid(), ClientId = client.Id, EndpointId = endpoint.Id, Name = "Check", Type = type, ParametersJson = parameters,
            WarningThreshold = warning, IntervalSeconds = 60, CreatedAt = now, UpdatedAt = now
        };
        await using var context = db.DbFactory.CreateSystem();
        context.CheckDefinitions.Add(check);
        context.CheckStates.Add(new CheckState { EndpointId = endpoint.Id, ClientId = client.Id, CheckDefinitionId = check.Id, Target = "", LastResultAt = now, UpdatedAt = now });
        await context.SaveChangesAsync();
        return (client, endpoint, check);
    }

    private async Task ResultAsync(Endpoint endpoint, CheckDefinition check, DateTime time, double value, string error = "")
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.CheckResults.Add(new CheckResult
        {
            Time = time, ClientId = endpoint.ClientId, EndpointId = endpoint.Id, CheckDefinitionId = check.Id, AgentTime = time, Value = value, Error = error
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task The_last_hour_comes_from_raw_results_in_aligned_one_minute_buckets()
    {
        var (_, endpoint, check) = await SetupAsync(CheckType.TcpPort, """{"host":"db","port":"5432"}""", 100);
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await ResultAsync(endpoint, check, now.AddMinutes(-10), 20);
        await ResultAsync(endpoint, check, now.AddMinutes(-10).AddSeconds(1), 40);
        await ResultAsync(endpoint, check, now.AddMinutes(-5), -1);
        await ResultAsync(endpoint, check, now.AddMinutes(-4), 0, "timeout");
        await ResultAsync(endpoint, check, now.AddHours(-3), 999);

        var view = await History.GetAsync(WebFixture.Technician(), endpoint.Id, check.Id, null, HistoryRange.Hour);

        Assert.NotNull(view);
        Assert.Equal(60, view.Points.Count);
        Assert.Equal(TimeSpan.FromMinutes(1), view.BucketSize);
        Assert.True(view.To > now && view.To - now <= TimeSpan.FromMinutes(1));
        Assert.Equal(ThresholdKind.Reachability, view.Kind);
        Assert.Equal(100, view.WarningThreshold);
        Assert.Equal(2, view.Points.Sum(p => p.Values));
        Assert.Equal(1, view.Points.Sum(p => p.NoResponses));
        Assert.Equal(1, view.Points.Sum(p => p.Errors));
        var measured = Assert.Single(view.Points, p => p.Values > 0);
        Assert.True(measured.Values == 2 && measured.Min == 20 && measured.Max == 40 && measured.Avg == 30);
        Assert.DoesNotContain(view.Points, p => p.Max == 999);
    }

    [Fact]
    public async Task Longer_ranges_come_from_the_rollups_and_other_clients_or_agent_only_endpoints_get_nothing()
    {
        var (client, endpoint, check) = await SetupAsync(CheckType.ServiceRunning, """{"service":"Spooler"}""", null);
        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var day = new DateTime(now.Year, now.Month, now.Day, 0, 0, 0, DateTimeKind.Utc).AddDays(-2);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.CheckResultsDaily.Add(new CheckResultDaily
            {
                EndpointId = endpoint.Id, ClientId = client.Id, CheckDefinitionId = check.Id, Bucket = day, MinValue = 0, MaxValue = 1, SumValue = 20,
                ValueCount = 24
            });
            db.CheckResultsHourly.Add(new CheckResultHourly
            {
                EndpointId = endpoint.Id, ClientId = client.Id, CheckDefinitionId = check.Id, Bucket = day.AddHours(7), MinValue = 0, MaxValue = 1, SumValue = 5,
                ValueCount = 6, ErrorCount = 1
            });
            await db.SaveChangesAsync();
        }

        var year = await History.GetAsync(WebFixture.Technician(), endpoint.Id, check.Id, null, HistoryRange.Year);
        Assert.Equal(365, year!.Points.Count);
        Assert.Equal(ThresholdKind.Flag, year.Kind);
        var point = Assert.Single(year.Points, p => p.HasData);
        Assert.Equal(day, point.Time);
        Assert.Equal(0, point.Min);

        var week = await History.GetAsync(WebFixture.Technician(), endpoint.Id, check.Id, null, HistoryRange.Week);
        Assert.Equal(168, week!.Points.Count);
        Assert.Equal(1, week.Points.Sum(p => p.Errors));

        var other = WebFixture.CallerWith(new RestrictedClientScope([Guid.NewGuid()]), FleetifyRoles.Technician);
        Assert.Null(await History.GetAsync(other, endpoint.Id, check.Id, null, HistoryRange.Year));

        var (_, agentOnly, agentOnlyCheck) = await SetupAsync(CheckType.CpuUsage, "{}", 80, EndpointTier.AgentOnly);
        Assert.Null(await History.GetAsync(WebFixture.Technician(), agentOnly.Id, agentOnlyCheck.Id, null, HistoryRange.Day));
    }

    [Fact]
    public void The_chart_breaks_its_line_at_a_bucket_without_values()
    {
        var from = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);
        var points = new List<HistoryPoint>
        {
            new(from, 10, 15, 20, 2, 0, 0),
            new(from.AddMinutes(1), 20, 25, 30, 2, 0, 0),
            new(from.AddMinutes(2), null, null, null, 0, 1, 0),
            new(from.AddMinutes(3), 40, 45, 50, 2, 0, 0)
        };

        var layout = new HistoryChartLayout(points, from, from.AddMinutes(4), "ms", 60, null);

        Assert.Equal(2, layout.AveragePath().Count(c => c == 'M') - 1); // the lone last bucket adds its own short stroke
        Assert.Equal(2, layout.BandPath().Count(c => c == 'Z'));
        Assert.Equal(0, layout.YMin);
        Assert.True(layout.YMax > 60);
        Assert.Equal(HistoryChartLayout.Top + layout.PlotHeight, layout.Y(0), 3);
        Assert.Equal((0d, 100d), (new HistoryChartLayout(points, from, from.AddMinutes(4), "%", null, null).YMin,
            new HistoryChartLayout(points, from, from.AddMinutes(4), "%", null, null).YMax));
    }
}
