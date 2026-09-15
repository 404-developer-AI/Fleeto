using System.Globalization;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees policy maintenance windows in web (0.2.0): windows are validated and saved with the policy, their occurrences are
/// stored ahead at once and replaced on every save, copies keep them, and the endpoint list and detail show the running window as
/// maintenance of the policy for the classes it applies to.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class PolicyMaintenanceWindowTests
{
    private readonly WebFixture _fixture;

    public PolicyMaintenanceWindowTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private PolicyService Policies => _fixture.Services.GetRequiredService<PolicyService>();
    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    private MaintenanceWindow RunningWindow(CheckAppliesTo appliesTo) =>
        new("Patch night", WeekDays.All, Now.AddMinutes(-30).ToString("HH:mm", CultureInfo.InvariantCulture), 120, "UTC", appliesTo);

    private static PolicyInput Input(string name, IReadOnlyList<MaintenanceWindow>? windows) =>
        new(name, null, 30, 3600, 10, AlertSeverity.Critical, windows);

    private async Task<List<MaintenanceWindowOccurrence>> OccurrencesAsync(Guid policyId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.MaintenanceWindowOccurrences.AsNoTracking().Where(o => o.PolicyId == policyId).OrderBy(o => o.StartsAt).ToListAsync();
    }

    [Fact]
    public async Task Windows_are_validated_saved_with_occurrences_and_copied()
    {
        var technician = WebFixture.Technician();
        var name = "Windows " + Guid.NewGuid().ToString("N")[..6];

        Assert.False((await Policies.CreateAsync(technician, null, Input(name, [RunningWindow(CheckAppliesTo.All) with { TimeZone = "Nowhere/City" }]))).Success);
        Assert.False((await Policies.CreateAsync(technician, null, Input(name, [RunningWindow(CheckAppliesTo.All) with { Days = WeekDays.None }]))).Success);

        var created = await Policies.CreateAsync(technician, null, Input(name, [RunningWindow(CheckAppliesTo.Server)]));
        Assert.True(created.Success, created.Problem);
        var occurrences = await OccurrencesAsync(created.Value);
        Assert.InRange(occurrences.Count, 8, 10);
        Assert.All(occurrences, o => Assert.Equal(CheckAppliesTo.Server, o.AppliesTo));

        var listed = Assert.Single(await Policies.ListAsync(technician), p => p.Id == created.Value);
        Assert.Equal("Patch night", Assert.Single(listed.MaintenanceWindows).Name);

        // Saving without windows removes them; null keeps them.
        Assert.True((await Policies.UpdateAsync(technician, created.Value, Input(name, null))).Success);
        Assert.NotEmpty(await OccurrencesAsync(created.Value));
        var copy = await Policies.CopyAsync(technician, created.Value, name + " copy", null);
        Assert.True(copy.Success, copy.Problem);
        Assert.NotEmpty(await OccurrencesAsync(copy.Value));
        Assert.True((await Policies.UpdateAsync(technician, created.Value, Input(name, []))).Success);
        Assert.Empty(await OccurrencesAsync(created.Value));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.TargetId == created.Value.ToString()).OrderBy(a => a.Id).FirstAsync();
        Assert.Contains("Every day", audit.DetailsJson);
    }

    [Fact]
    public async Task The_endpoint_list_and_detail_show_a_running_window_for_its_class()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        var technician = WebFixture.Technician();
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var server = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "SRV-PW-01", EndpointClass.Server);
        var workstation = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.Managed, "WS-PW-02");

        var policy = await Policies.CreateAsync(technician, client.Id, Input("Servers patch " + Guid.NewGuid().ToString("N")[..6], [RunningWindow(CheckAppliesTo.Server)]));
        Assert.True(policy.Success, policy.Problem);
        var linked = await _fixture.Services.GetRequiredService<SiteService>().SetPolicyAsync(technician, site.Id, policy.Value);
        Assert.True(linked.Success, linked.Problem);

        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var page = await endpoints.ListAsync(technician, new EndpointListQuery(client.Id, null, null, null, EndpointStatusFilter.InMaintenance, null));
        var row = Assert.Single(page.Rows);
        Assert.Equal(server.Id, row.Id);
        Assert.Equal(MaintenanceSource.PolicyWindow, row.Maintenance!.Source);
        Assert.StartsWith("Servers patch", row.Maintenance.SourceName);
        Assert.Equal("Patch night", row.Maintenance.Reason);
        Assert.False(row.OwnMaintenanceActive);

        var detail = await endpoints.GetAsync(technician, workstation.Id);
        Assert.Null(detail!.Maintenance);
        Assert.Equal(MaintenanceSource.PolicyWindow, (await endpoints.GetAsync(technician, server.Id))!.Maintenance!.Source);
        Assert.Equal(1, Assert.Single(await _fixture.Services.GetRequiredService<ClientService>().ListTreeAsync(technician), c => c.Id == client.Id)
            .InMaintenanceCount);
    }
}
