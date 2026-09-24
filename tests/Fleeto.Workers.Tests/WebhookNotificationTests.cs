using System.Text.Json;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Notifications;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Options;
using Fleeto.Workers.Webhooks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees routing and webhooks (0.2.0): a channel limited to clients only gets alerts of those clients, webhook channels
/// get one signed delivery per notification, deliveries retry with backoff and stop on a permanent refusal, one failing
/// channel does not pause another, a disabled channel's delivery is dropped, and the real sender never connects to a local
/// address.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class WebhookNotificationTests
{
    private readonly WorkersFixture _fixture;

    public WebhookNotificationTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FakeSender : IWebhookSender
    {
        public Func<Uri, WebhookDeliveryException?> Behaviour { get; set; } = _ => null;
        public List<(Uri Url, string Body, Dictionary<string, string> Headers)> Sent { get; } = [];

        public Task SendAsync(Uri url, string body, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
        {
            if (Behaviour(url) is { } failure)
            {
                throw failure;
            }

            Sent.Add((url, body, headers.ToDictionary(h => h.Key, h => h.Value)));
            return Task.CompletedTask;
        }
    }

    private OutboxWebhookService Outbox(IWebhookSender sender, int breakerFailures = 1000) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus, sender, _fixture.Bundler(), _fixture.Db.SecretProtector,
            MsOptions.Create(new WebhookOptions { CircuitBreakerFailures = breakerFailures, CircuitBreakerPauseMinutes = 5 }), _fixture.Heartbeat(),
            _fixture.Db.Time, NullLogger<OutboxWebhookService>.Instance);

    private async Task ClearDeliveriesAsync()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var now = _fixture.Now;
        await db.OutboxWebhooks.Where(w => w.SentAt == null).ExecuteUpdateAsync(s => s.SetProperty(w => w.SentAt, now));
    }

    private async Task<(NotificationChannel Channel, string? Secret)> WebhookChannelAsync(WebhookFormat format, string url, bool allClients = true,
        Guid[]? clients = null)
    {
        var id = Guid.NewGuid();
        var secret = format == WebhookFormat.Generic ? WebhookTargets.NewSigningSecret() : null;
        var channel = new NotificationChannel
        {
            Id = id, Name = "Hook " + id.ToString("N")[..8], Type = NotificationChannelType.Webhook, WebhookFormat = format, WebhookHost = new Uri(url).Host,
            EncryptedWebhook = WebhookTargets.Protect(_fixture.Db.SecretProtector, id, new WebhookTarget(url, secret)),
            MinimumSeverity = AlertSeverity.Warning, NotifyOnResolve = true, AllClients = allClients, CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now,
            Clients = (clients ?? []).Select(c => new NotificationChannelClient { NotificationChannelId = id, ClientId = c }).ToList()
        };
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.NotificationChannels.Add(channel);
        await db.SaveChangesAsync();
        return (channel, secret);
    }

    private async Task DeleteChannelsAsync(params Guid[] ids)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.NotificationChannels.Where(c => ids.Contains(c.Id)).ExecuteDeleteAsync();
    }

    private async Task<Guid> EnqueueAsync(Guid channelId, string payload = """{"type":"test"}""")
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var delivery = new OutboxWebhook
        {
            Id = Guid.NewGuid(), NotificationChannelId = channelId, Category = "test", Payload = payload, NextAttemptAt = _fixture.Now, CreatedAt = _fixture.Now
        };
        db.OutboxWebhooks.Add(delivery);
        await db.SaveChangesAsync();
        return delivery.Id;
    }

    private async Task<OutboxWebhook> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.OutboxWebhooks.AsNoTracking().SingleAsync(w => w.Id == id);
    }

    [Fact]
    public async Task Channels_limited_to_clients_only_receive_alerts_of_those_clients()
    {
        await ClearDeliveriesAsync();
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (clientA, siteA) = await _fixture.CreateClientAndSiteAsync();
        var (_, siteB) = await _fixture.CreateClientAndSiteAsync();
        var endpointA = await _fixture.Db.CreateEndpointAsync(siteA, EndpointTier.Managed, "SRV-ROUTE-A");
        var endpointB = await _fixture.Db.CreateEndpointAsync(siteB, EndpointTier.Managed, "SRV-ROUTE-B");
        var checkA = await _fixture.CreateCheckAsync(siteA, CheckType.CpuUsage, 80, 90);
        var checkB = await _fixture.CreateCheckAsync(siteB, CheckType.CpuUsage, 80, 90);

        var (onlyA, _) = await WebhookChannelAsync(WebhookFormat.Generic, "https://hooks.test.example/a", allClients: false, clients: [clientA.Id]);
        var recipient = $"route-{Guid.NewGuid():N}@test.example";
        var emailId = Guid.NewGuid();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.NotificationChannels.Add(new NotificationChannel
            {
                Id = emailId, Name = "Route mail", Recipients = recipient, AllClients = false, CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now,
                Clients = [new NotificationChannelClient { NotificationChannelId = emailId, ClientId = clientA.Id }]
            });
            await db.SaveChangesAsync();
        }

        try
        {
            var evaluation = _fixture.CheckEvaluation();
            await _fixture.InsertResultAsync(endpointA, checkA, 85);
            await _fixture.InsertResultAsync(endpointB, checkB, 95);
            await evaluation.EvaluateEndpointAsync(endpointA.Id, CancellationToken.None);
            await evaluation.EvaluateEndpointAsync(endpointB.Id, CancellationToken.None);

            await using var db = _fixture.Db.DbFactory.CreateSystem();
            var delivery = Assert.Single(await db.OutboxWebhooks.AsNoTracking().Where(w => w.NotificationChannelId == onlyA.Id).ToListAsync());
            Assert.Equal(AlertNotificationService.CategoryOpened, delivery.Category);
            using var payload = JsonDocument.Parse(delivery.Payload);
            Assert.Equal(delivery.Id, payload.RootElement.GetProperty("id").GetGuid());
            Assert.Equal("SRV-ROUTE-A", payload.RootElement.GetProperty("alert").GetProperty("endpoint").GetProperty("hostname").GetString());
            var email = Assert.Single(await db.OutboxEmails.AsNoTracking().Where(e => e.ToAddress == recipient).ToListAsync());
            Assert.Contains("SRV-ROUTE-A", email.Subject);
        }
        finally
        {
            await DeleteChannelsAsync(onlyA.Id, emailId);
        }
    }

    [Fact]
    public async Task A_generic_delivery_is_signed_and_sent_once_with_its_delivery_id()
    {
        await ClearDeliveriesAsync();
        var (channel, secret) = await WebhookChannelAsync(WebhookFormat.Generic, "https://hooks.test.example/signed");
        var (slack, _) = await WebhookChannelAsync(WebhookFormat.Slack, "https://hooks.slack.com/services/T0/B0/x");
        try
        {
            var id = await EnqueueAsync(channel.Id);
            var slackId = await EnqueueAsync(slack.Id, """{"text":"x"}""");
            var sender = new FakeSender();
            var outbox = Outbox(sender);

            Assert.Equal(2, await outbox.DeliverPendingAsync(CancellationToken.None));
            Assert.Equal(0, await outbox.DeliverPendingAsync(CancellationToken.None));

            var signed = Assert.Single(sender.Sent, s => s.Url.AbsolutePath == "/signed");
            Assert.Equal(id.ToString("D"), signed.Headers[WebhookTargets.DeliveryHeader]);
            Assert.Equal("test", signed.Headers[WebhookTargets.EventHeader]);
            Assert.Equal(WebhookTargets.Sign(secret!, _fixture.Db.Time.GetUtcNow(), """{"type":"test"}"""), signed.Headers[WebhookTargets.SignatureHeader]);
            var unsigned = Assert.Single(sender.Sent, s => s.Url.Host == "hooks.slack.com");
            Assert.False(unsigned.Headers.ContainsKey(WebhookTargets.SignatureHeader));

            Assert.NotNull((await ReadAsync(id)).SentAt);
            Assert.NotNull((await ReadAsync(slackId)).SentAt);
        }
        finally
        {
            await DeleteChannelsAsync(channel.Id, slack.Id);
        }
    }

    [Fact]
    public async Task Failures_back_off_permanent_refusals_stop_and_one_failing_channel_does_not_pause_another()
    {
        await ClearDeliveriesAsync();
        var (broken, _) = await WebhookChannelAsync(WebhookFormat.Generic, "https://down.test.example/hook");
        var (healthy, _) = await WebhookChannelAsync(WebhookFormat.Generic, "https://up.test.example/hook");
        var (gone, _) = await WebhookChannelAsync(WebhookFormat.Slack, "https://gone.test.example/hook");
        try
        {
            var failing = new[] { await EnqueueAsync(broken.Id), await EnqueueAsync(broken.Id), await EnqueueAsync(broken.Id) };
            var refused = await EnqueueAsync(gone.Id);
            var sender = new FakeSender
            {
                Behaviour = url => url.Host switch
                {
                    "down.test.example" => new WebhookDeliveryException("down.test.example answered 503. Fleeto tries again.", permanent: false),
                    "gone.test.example" => new WebhookDeliveryException("gone.test.example answered 410.", permanent: true),
                    _ => null
                }
            };
            var outbox = Outbox(sender, breakerFailures: 2);

            await outbox.DeliverPendingAsync(CancellationToken.None);

            var afterFirstPass = await Task.WhenAll(failing.Select(ReadAsync));
            Assert.Equal(2, afterFirstPass.Count(w => w.Attempts == 1));
            Assert.Equal(1, afterFirstPass.Count(w => w.Attempts == 0));
            Assert.All(afterFirstPass.Where(w => w.Attempts == 1), w =>
                Assert.InRange(w.NextAttemptAt - _fixture.Now, TimeSpan.FromSeconds(59), TimeSpan.FromSeconds(61)));
            Assert.Equal(WebhookTargets.MaxDeliveryAttempts, (await ReadAsync(refused)).Attempts);

            // The broken channel is paused; the healthy channel still delivers.
            var healthyId = await EnqueueAsync(healthy.Id);
            _fixture.Db.Time.Advance(TimeSpan.FromMinutes(2));
            await outbox.DeliverPendingAsync(CancellationToken.None);
            Assert.NotNull((await ReadAsync(healthyId)).SentAt);
            Assert.All(await Task.WhenAll(failing.Select(ReadAsync)), w => Assert.True(w.Attempts <= 1));

            sender.Behaviour = _ => null;
            _fixture.Db.Time.Advance(TimeSpan.FromMinutes(4));
            await outbox.DeliverPendingAsync(CancellationToken.None);
            Assert.All(await Task.WhenAll(failing.Select(ReadAsync)), w => Assert.NotNull(w.SentAt));
        }
        finally
        {
            await DeleteChannelsAsync(broken.Id, healthy.Id, gone.Id);
        }
    }

    [Fact]
    public async Task A_delivery_of_a_disabled_channel_is_dropped()
    {
        await ClearDeliveriesAsync();
        var (channel, _) = await WebhookChannelAsync(WebhookFormat.Teams, "https://teams.test.example/hook");
        try
        {
            var id = await EnqueueAsync(channel.Id);
            await using (var db = _fixture.Db.DbFactory.CreateSystem())
            {
                await db.NotificationChannels.Where(c => c.Id == channel.Id).ExecuteUpdateAsync(s => s.SetProperty(c => c.Enabled, false));
            }

            var sender = new FakeSender();
            await Outbox(sender).DeliverPendingAsync(CancellationToken.None);

            Assert.Empty(sender.Sent);
            var delivery = await ReadAsync(id);
            Assert.Null(delivery.SentAt);
            Assert.Equal(WebhookTargets.MaxDeliveryAttempts, delivery.Attempts);
        }
        finally
        {
            await DeleteChannelsAsync(channel.Id);
        }
    }

    [Theory]
    [InlineData("https://127.0.0.1:9/hook")]
    [InlineData("https://localhost:9/hook")]
    [InlineData("https://[::1]:9/hook")]
    public async Task The_sender_never_connects_to_a_local_address(string url)
    {
        using var sender = new HttpWebhookSender(MsOptions.Create(new WebhookOptions { TimeoutSeconds = 5 }));

        var error = await Assert.ThrowsAsync<WebhookDeliveryException>(() =>
            sender.SendAsync(new Uri(url), "{}", [], CancellationToken.None));

        Assert.True(error.IsPermanent);
        Assert.Contains("private or local address", error.Message);
    }
}
