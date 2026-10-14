using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Backups made outside Fleeto (0.6.0): stored as no destination with a description, shown as healthy on the dashboard, and
/// Back up now explains that Fleeto makes none. Saving asks the workers to measure instance health again.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class ExternalBackupTests
{
    private readonly WebFixture _fixture;

    public ExternalBackupTests(WebFixture fixture) => _fixture = fixture;

    private static BackupInput Input(string? external) =>
        new(BackupDestinationType.None, null, null, null, null, null, null, null, null, 2, external);

    [Fact]
    public async Task Backups_made_outside_Fleeto_are_kept_with_their_description_and_shown_as_handled()
    {
        var settings = _fixture.Services.GetRequiredService<SettingsService>();
        var dashboard = _fixture.Services.GetRequiredService<DashboardService>();
        var admin = WebFixtureBase.Admin();
        try
        {
            var measured = _fixture.Database.Bus.PayloadsFor(NotificationChannels.InstanceHealth).Count;
            Assert.True((await settings.SaveBackupAsync(admin, Input("  Veeam, daily image of the VPS  "))).Success);
            // The workers measure instance health again at once instead of showing the old backup problem for 5 minutes.
            Assert.True(_fixture.Database.Bus.PayloadsFor(NotificationChannels.InstanceHealth).Count > measured);

            Assert.Equal("Veeam, daily image of the VPS", (await settings.GetBackupAsync(admin)).ExternalDescription);
            Assert.Equal("Veeam, daily image of the VPS", (await dashboard.GetAsync(admin)).Backups.ExternalDescription);
            var now = await settings.RequestBackupNowAsync(admin);
            Assert.False(now.Success);
            Assert.Contains("outside Fleeto", now.Problem);

            Assert.False((await settings.SaveBackupAsync(admin, Input(new string('x', BackupSettings.MaxExternalDescriptionLength + 1)))).Success);
            Assert.False((await settings.SaveBackupAsync(WebFixtureBase.Technician(), Input("Veeam"))).Success);
        }
        finally
        {
            Assert.True((await settings.SaveBackupAsync(admin, Input(null))).Success);
        }

        Assert.Null((await settings.GetBackupAsync(admin)).ExternalDescription);
        Assert.Null((await dashboard.GetAsync(admin)).Backups.ExternalDescription);
    }
}
