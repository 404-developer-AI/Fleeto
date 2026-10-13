using Fleeto.Infrastructure.Settings;
using Fleeto.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Backups made outside Fleeto (0.6.0): stored as no destination with a description, shown as healthy on the dashboard, and
/// Back up now explains that Fleeto makes none.
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
            Assert.True((await settings.SaveBackupAsync(admin, Input("  Veeam, daily image of the VPS  "))).Success);

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
