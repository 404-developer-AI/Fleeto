using System.Security.Cryptography.X509Certificates;
using Fleetify.Infrastructure.Settings;
using Fleetify.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// Guarantees the email settings (0.2.0): Microsoft Graph is validated, its secret is write-only and never audited, it can be
/// chosen only when complete and must stay complete while chosen; a certificate waits as pending until the admin switches and
/// only its public part can be downloaded; the dashboard warns about a credential that expires within 30 days and says when
/// it has expired.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class EmailSettingsTests
{
    private const string ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d";
    private readonly WebFixture _fixture;

    public EmailSettingsTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private SettingsService Settings => _fixture.Services.GetRequiredService<SettingsService>();

    private DateTime Today => _fixture.Database.Time.GetUtcNow().UtcDateTime.Date;

    private async Task CleanAsync()
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.Settings.Where(s => s.Key.StartsWith("email.")).ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Graph_settings_are_validated_and_the_secret_is_write_only()
    {
        await CleanAsync();
        var admin = WebFixture.Admin();
        try
        {
            Assert.False((await Settings.SaveGraphAsync(admin, new GraphInput("not a tenant", ClientId, "fleeto@contoso.com", GraphCredentialType.ClientSecret, "s", Today.AddDays(90)))).Success);
            Assert.False((await Settings.SaveGraphAsync(admin, new GraphInput("contoso.onmicrosoft.com", "abc", "fleeto@contoso.com", GraphCredentialType.ClientSecret, "s", Today.AddDays(90)))).Success);
            Assert.False((await Settings.SaveGraphAsync(admin, new GraphInput("contoso.onmicrosoft.com", ClientId, "fleeto@contoso.com", GraphCredentialType.ClientSecret, "s", null))).Success);
            Assert.False((await Settings.SaveGraphAsync(admin, new GraphInput("contoso.onmicrosoft.com", ClientId, "fleeto@contoso.com", GraphCredentialType.ClientSecret, "s", Today.AddDays(-2)))).Success);
            Assert.False((await Settings.SaveProviderAsync(admin, EmailProvider.MicrosoftGraph)).Success);
            Assert.False((await Settings.SaveGraphAsync(WebFixture.Technician(), new GraphInput("contoso.onmicrosoft.com", ClientId, "fleeto@contoso.com", GraphCredentialType.ClientSecret, "s", Today.AddDays(90)))).Success);

            var saved = await Settings.SaveGraphAsync(admin, new GraphInput("contoso.onmicrosoft.com", ClientId, "fleeto@contoso.com",
                GraphCredentialType.ClientSecret, "super-secret-value", Today.AddDays(90)));
            Assert.True(saved.Success, saved.Problem);
            Assert.True((await Settings.SaveProviderAsync(admin, EmailProvider.MicrosoftGraph)).Success);

            var view = await Settings.GetEmailAsync(admin);
            Assert.Equal(EmailProvider.MicrosoftGraph, view.Provider);
            Assert.True(view.Graph.HasClientSecret);
            Assert.True(view.Graph.IsComplete);
            Assert.Equal(Today.AddDays(91).AddSeconds(-1), view.Graph.ClientSecretExpiresAt);
            Assert.DoesNotContain("super-secret-value", System.Text.Json.JsonSerializer.Serialize(view));

            // Blank keeps the secret; switching to a certificate without one is refused while Graph sends the email.
            Assert.True((await Settings.SaveGraphAsync(admin, new GraphInput("contoso.onmicrosoft.com", ClientId, "alerts@contoso.com", GraphCredentialType.ClientSecret, null, Today.AddDays(90)))).Success);
            Assert.True((await Settings.GetEmailAsync(admin)).Graph.HasClientSecret);
            Assert.False((await Settings.SaveGraphAsync(admin, new GraphInput("contoso.onmicrosoft.com", ClientId, "alerts@contoso.com", GraphCredentialType.Certificate, null, null))).Success);

            await using var db = _fixture.Database.DbFactory.CreateSystem();
            var stored = await db.Settings.AsNoTracking().SingleAsync(s => s.Key == SettingKeys.Graph);
            Assert.True(stored.IsEncrypted);
            Assert.DoesNotContain("super-secret-value", stored.Value);
            Assert.All(await db.AuditEntries.AsNoTracking().Where(a => a.TargetId == SettingKeys.Graph).ToListAsync(),
                a => Assert.DoesNotContain("super-secret-value", a.DetailsJson));
        }
        finally
        {
            await CleanAsync();
        }
    }

    [Fact]
    public async Task A_certificate_waits_until_the_admin_switches_and_only_its_public_part_is_downloaded()
    {
        await CleanAsync();
        var admin = WebFixture.Admin();
        try
        {
            Assert.True((await Settings.SaveGraphAsync(admin, new GraphInput("contoso.onmicrosoft.com", ClientId, "fleeto@contoso.com",
                GraphCredentialType.Certificate, null, null))).Success);
            Assert.False((await Settings.GetEmailAsync(admin)).Graph.IsComplete);
            Assert.False((await Settings.GetGraphPublicCertificateAsync(admin)).Success);

            Assert.True((await Settings.CreateGraphCertificateAsync(admin)).Success);
            var pending = (await Settings.GetEmailAsync(admin)).Graph;
            Assert.NotNull(pending.PendingCertificateThumbprint);
            Assert.Null(pending.CertificateThumbprint);
            Assert.False(pending.IsComplete);

            var download = await Settings.GetGraphPublicCertificateAsync(admin);
            Assert.True(download.Success, download.Problem);
            using (var certificate = X509CertificateLoader.LoadCertificate(download.Value!.Der))
            {
                Assert.False(certificate.HasPrivateKey);
                Assert.Equal(pending.PendingCertificateThumbprint, certificate.Thumbprint);
            }

            Assert.True((await Settings.ActivateGraphCertificateAsync(admin)).Success);
            var active = (await Settings.GetEmailAsync(admin)).Graph;
            Assert.Equal(pending.PendingCertificateThumbprint, active.CertificateThumbprint);
            Assert.Null(active.PendingCertificateThumbprint);
            Assert.True(active.IsComplete);
            Assert.False((await Settings.ActivateGraphCertificateAsync(admin)).Success);
            Assert.False((await Settings.CreateGraphCertificateAsync(WebFixture.Technician())).Success);
        }
        finally
        {
            await CleanAsync();
        }
    }

    [Fact]
    public async Task The_dashboard_warns_about_an_expiring_credential_in_use()
    {
        await CleanAsync();
        var admin = WebFixture.Admin();
        var dashboard = _fixture.Services.GetRequiredService<DashboardService>();
        try
        {
            Assert.True((await Settings.SaveGraphAsync(admin, new GraphInput("contoso.onmicrosoft.com", ClientId, "fleeto@contoso.com",
                GraphCredentialType.ClientSecret, "secret", Today.AddDays(10)))).Success);
            Assert.Empty((await dashboard.GetAsync(admin)).CredentialWarnings);

            Assert.True((await Settings.SaveProviderAsync(admin, EmailProvider.MicrosoftGraph)).Success);
            var warning = Assert.Single((await dashboard.GetAsync(WebFixture.Technician())).CredentialWarnings);
            Assert.False(warning.Expired);
            Assert.Equal("/settings/email", warning.SettingsPath);
            Assert.False(warning.FallbackInUse);

            // The fake clock only moves forward; every other test works relative to it.
            _fixture.Database.Time.Advance(TimeSpan.FromDays(12));
            Assert.True(Assert.Single((await dashboard.GetAsync(admin)).CredentialWarnings).Expired);
        }
        finally
        {
            await CleanAsync();
        }
    }
}
