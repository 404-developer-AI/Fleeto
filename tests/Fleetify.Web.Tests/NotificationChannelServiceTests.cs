using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Data;
using Fleetify.Infrastructure.Notifications;
using Fleetify.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleetify.Web.Tests;

/// <summary>
/// Guarantees notification channels in web (0.2.0): only admins manage them; webhook URLs are validated, stored encrypted and
/// never returned or audited; a generic channel's signing secret is shown once and can be replaced; a blank URL keeps the
/// current one; the type cannot change; client routing needs existing clients; and a test message is queued.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class NotificationChannelServiceTests
{
    private const string SlackUrl = "https://hooks.slack.com/services/T000/B000/very-secret-token";
    private readonly WebFixture _fixture;

    public NotificationChannelServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private NotificationChannelService Channels => _fixture.Services.GetRequiredService<NotificationChannelService>();

    private static NotificationChannelInput Webhook(string? url, WebhookFormat format = WebhookFormat.Generic, bool allClients = true,
        IReadOnlyCollection<Guid>? clients = null) =>
        new("Hook " + Guid.NewGuid().ToString("N")[..6], NotificationChannelType.Webhook, null, format, url, AlertSeverity.Warning, true, true,
            allClients, clients ?? []);

    private async Task<NotificationChannel> ReadAsync(Guid id)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.NotificationChannels.AsNoTracking().Include(c => c.Clients).SingleAsync(c => c.Id == id);
    }

    [Fact]
    public async Task Only_admins_manage_channels()
    {
        var result = await Channels.SaveAsync(WebFixture.Technician(), null, Webhook(SlackUrl, WebhookFormat.Slack));

        Assert.False(result.Success);
        await Assert.ThrowsAnyAsync<Exception>(() => Channels.ListAsync(WebFixture.Technician()));
    }

    [Fact]
    public async Task A_generic_webhook_gets_a_secret_shown_once_and_its_url_is_never_returned_or_audited()
    {
        var admin = WebFixture.Admin();
        var saved = await Channels.SaveAsync(admin, null, Webhook("https://ticketing.example.com/fleeto?key=abc123"));

        Assert.True(saved.Success, saved.Problem);
        var secret = Assert.IsType<string>(saved.Value!.SigningSecret);
        var stored = await ReadAsync(saved.Value.Id);
        Assert.Equal("ticketing.example.com", stored.WebhookHost);
        Assert.DoesNotContain("abc123", stored.EncryptedWebhook);
        var target = WebhookTargets.Unprotect(_fixture.Database.SecretProtector, stored.Id, stored.EncryptedWebhook!);
        Assert.Equal("https://ticketing.example.com/fleeto?key=abc123", target.Url);
        Assert.Equal(secret, target.SigningSecret);

        var view = Assert.Single(await Channels.ListAsync(admin), c => c.Id == stored.Id);
        Assert.DoesNotContain("abc123", System.Text.Json.JsonSerializer.Serialize(view));

        // Editing without a URL keeps the URL and the secret, and returns no secret.
        var edited = await Channels.SaveAsync(admin, stored.Id, Webhook(null) with { Name = "Renamed" });
        Assert.True(edited.Success, edited.Problem);
        Assert.Null(edited.Value!.SigningSecret);
        var kept = await ReadAsync(stored.Id);
        Assert.Equal(target, WebhookTargets.Unprotect(_fixture.Database.SecretProtector, kept.Id, kept.EncryptedWebhook!));

        var rotated = await Channels.RotateSigningSecretAsync(admin, stored.Id);
        Assert.True(rotated.Success, rotated.Problem);
        Assert.NotEqual(secret, rotated.Value);
        var afterRotate = await ReadAsync(stored.Id);
        Assert.Equal(rotated.Value, WebhookTargets.Unprotect(_fixture.Database.SecretProtector, afterRotate.Id, afterRotate.EncryptedWebhook!).SigningSecret);

        // Switching to Slack drops the signing secret.
        var slack = await Channels.SaveAsync(admin, stored.Id, Webhook(SlackUrl, WebhookFormat.Slack));
        Assert.True(slack.Success, slack.Problem);
        var slackStored = await ReadAsync(stored.Id);
        Assert.Null(WebhookTargets.Unprotect(_fixture.Database.SecretProtector, slackStored.Id, slackStored.EncryptedWebhook!).SigningSecret);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.TargetId == stored.Id.ToString()).ToListAsync();
        Assert.True(audit.Count >= 4);
        Assert.All(audit, a => Assert.DoesNotContain("abc123", a.DetailsJson));
        Assert.All(audit, a => Assert.DoesNotContain("very-secret-token", a.DetailsJson));
        Assert.All(audit, a => Assert.DoesNotContain(secret, a.DetailsJson));
    }

    [Theory]
    [InlineData("http://example.com/hook")]
    [InlineData("https://10.0.0.5/hook")]
    [InlineData("https://localhost/hook")]
    [InlineData("")]
    public async Task Invalid_webhook_urls_are_refused(string url)
    {
        var result = await Channels.SaveAsync(WebFixture.Admin(), null, Webhook(url));

        Assert.False(result.Success);
    }

    [Fact]
    public async Task The_type_cannot_change_and_client_routing_needs_existing_clients()
    {
        var admin = WebFixture.Admin();
        var client = await _fixture.Database.CreateClientAsync();
        var email = new NotificationChannelInput("Mail " + Guid.NewGuid().ToString("N")[..6], NotificationChannelType.Email, "ops@example.com",
            WebhookFormat.Generic, null, AlertSeverity.Critical, false, true, false, [client.Id]);

        var saved = await Channels.SaveAsync(admin, null, email);
        Assert.True(saved.Success, saved.Problem);
        var stored = await ReadAsync(saved.Value!.Id);
        Assert.False(stored.AllClients);
        Assert.Equal(client.Id, Assert.Single(stored.Clients).ClientId);
        Assert.Null(saved.Value.SigningSecret);

        Assert.False((await Channels.SaveAsync(admin, stored.Id, Webhook(SlackUrl, WebhookFormat.Slack))).Success);
        Assert.False((await Channels.SaveAsync(admin, null, email with { ClientIds = [] })).Success);
        Assert.False((await Channels.SaveAsync(admin, null, email with { ClientIds = [Guid.NewGuid()] })).Success);

        var allClients = await Channels.SaveAsync(admin, stored.Id, email with { AllClients = true });
        Assert.True(allClients.Success, allClients.Problem);
        Assert.Empty((await ReadAsync(stored.Id)).Clients);
    }

    [Fact]
    public async Task A_test_message_is_queued_for_webhook_channels_only()
    {
        var admin = WebFixture.Admin();
        var hook = await Channels.SaveAsync(admin, null, Webhook(SlackUrl, WebhookFormat.Slack));
        Assert.True(hook.Success, hook.Problem);

        Assert.True((await Channels.SendTestAsync(admin, hook.Value!.Id)).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var delivery = Assert.Single(await db.OutboxWebhooks.AsNoTracking().Where(w => w.NotificationChannelId == hook.Value.Id).ToListAsync());
        Assert.Equal("test", delivery.Category);
        Assert.Contains("\"text\"", delivery.Payload);

        var mail = await Channels.SaveAsync(admin, null, new NotificationChannelInput("Mail test", NotificationChannelType.Email, "ops@example.com",
            WebhookFormat.Generic, null, AlertSeverity.Warning, true, true, true, []));
        Assert.False((await Channels.SendTestAsync(admin, mail.Value!.Id)).Success);
    }
}
