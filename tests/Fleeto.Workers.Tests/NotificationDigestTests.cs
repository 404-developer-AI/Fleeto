using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Alert notifications during a flood (0.6.0): per email address and per Slack or Teams channel, the first five within ten
/// minutes go out on their own and the rest is combined into one digest per ten minutes; a generic webhook and every email
/// that is not an alert always go out on their own; a digest lists what it combines, encoded like every other email.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class NotificationDigestTests
{
    private readonly WorkersFixture _fixture;

    public NotificationDigestTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private static string Line(int number, NotificationEvent kind = NotificationEvent.Opened, string hostname = "WS-01") =>
        NotificationDigest.Serialize(new DigestLine(kind, AlertSeverity.Critical, $"Disk C: is full ({number})", hostname, "ACME", "Acme Ltd",
            "Main office", DateTime.UtcNow, "https://rmm.test.example/endpoints/" + Guid.NewGuid()));

    private async Task<List<Guid>> QueueEmailsAsync(string address, int count, bool alerts = true)
    {
        var ids = new List<Guid>();
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        for (var i = 0; i < count; i++)
        {
            var email = OutboxEmails.Create(address, new EmailContent($"Alert {i}", "<p>x</p>", "x"), alerts ? "alert.opened" : OutboxEmails.CategoryBackup,
                _fixture.Now.AddSeconds(-count + i));
            email.DigestLine = alerts ? Line(i) : null;
            db.OutboxEmails.Add(email);
            ids.Add(email.Id);
        }

        await db.SaveChangesAsync();
        return ids;
    }

    private async Task<List<OutboxEmail>> EmailsToAsync(string address)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.OutboxEmails.AsNoTracking().Where(e => e.ToAddress == address).OrderBy(e => e.CreatedAt).ToListAsync();
    }

    [Fact]
    public void The_first_five_go_alone_the_rest_waits_for_one_digest_per_window()
    {
        var now = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal(new DigestDecision(3, false, null), NotificationDigest.Decide(0, null, 3, now));
        Assert.Equal(new DigestDecision(5, true, null), NotificationDigest.Decide(0, null, 12, now));
        Assert.Equal(new DigestDecision(2, true, null), NotificationDigest.Decide(3, null, 4, now));
        // A digest went out four minutes ago: the rest waits until its window ends.
        Assert.Equal(new DigestDecision(0, false, now.AddMinutes(6)), NotificationDigest.Decide(6, now.AddMinutes(-4), 3, now));
        Assert.Equal(new DigestDecision(0, true, null), NotificationDigest.Decide(6, now.AddMinutes(-10), 3, now));
    }

    [Fact]
    public async Task A_flood_of_alert_emails_becomes_five_emails_and_one_digest_per_address()
    {
        var flooded = $"flood-{Guid.NewGuid():N}@test.example";
        var quiet = $"quiet-{Guid.NewGuid():N}@test.example";
        await QueueEmailsAsync(flooded, 12);
        await QueueEmailsAsync(flooded, 3, alerts: false);
        await QueueEmailsAsync(quiet, 2);

        await _fixture.Bundler().BundleEmailsAsync(10, CancellationToken.None);

        var emails = await EmailsToAsync(flooded);
        var digest = Assert.Single(emails, e => e.Category == NotificationDigest.Category);
        var bundled = emails.Where(e => e.BundledInto == digest.Id).ToList();
        Assert.Equal(7, bundled.Count);
        Assert.All(bundled, e => Assert.NotNull(e.SentAt));
        Assert.Equal(5, emails.Count(e => e.DigestLine != null && e.BundledInto == null && e.SentAt == null));
        Assert.Equal(3, emails.Count(e => e.Category == OutboxEmails.CategoryBackup && e.BundledInto == null));
        Assert.StartsWith("7 alert notifications from ", digest.Subject);
        Assert.Contains("Disk C: is full (11)", digest.TextBody);
        Assert.Contains("Acme Ltd (ACME) - Main office", digest.TextBody);

        Assert.All(await EmailsToAsync(quiet), e => Assert.Null(e.BundledInto));

        // More alerts during the same window wait for the next digest instead of going out one by one.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.OutboxEmails.Where(e => e.ToAddress == flooded && e.SentAt == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.SentAt, _fixture.Now));
        }

        var later = await QueueEmailsAsync(flooded, 2);
        await _fixture.Bundler().BundleEmailsAsync(10, CancellationToken.None);
        var waiting = (await EmailsToAsync(flooded)).Where(e => later.Contains(e.Id)).ToList();
        Assert.All(waiting, e =>
        {
            Assert.Null(e.SentAt);
            Assert.Null(e.BundledInto);
            Assert.Equal(digest.CreatedAt + NotificationDigest.Window, e.NextAttemptAt, TimeSpan.FromMilliseconds(1));
        });
    }

    [Fact]
    public async Task Chat_channels_get_a_digest_and_generic_webhooks_never_do()
    {
        var channels = new Dictionary<WebhookFormat, Guid>();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            foreach (var format in new[] { WebhookFormat.Slack, WebhookFormat.Teams, WebhookFormat.Generic })
            {
                var id = Guid.NewGuid();
                channels[format] = id;
                // Disabled and routed to no client, so no other test's alert reaches it.
                db.NotificationChannels.Add(new NotificationChannel
                {
                    Id = id, Name = $"Digest {format} {id:N}", Type = NotificationChannelType.Webhook, WebhookFormat = format, Enabled = false,
                    EncryptedWebhook = WebhookTargets.Protect(_fixture.Db.SecretProtector, id, new WebhookTarget("https://hooks.test.example/x", null)),
                    AllClients = false, CreatedAt = _fixture.Now, UpdatedAt = _fixture.Now
                });
                for (var i = 0; i < 8; i++)
                {
                    db.OutboxWebhooks.Add(new OutboxWebhook
                    {
                        Id = Guid.NewGuid(), NotificationChannelId = id, Category = "alert.opened", Payload = "{}", NextAttemptAt = _fixture.Now,
                        CreatedAt = _fixture.Now.AddSeconds(-8 + i),
                        // As AlertNotificationService writes it: generic webhooks carry no digest line.
                        DigestLine = format == WebhookFormat.Generic ? null : Line(i, i % 2 == 0 ? NotificationEvent.Opened : NotificationEvent.Resolved)
                    });
                }
            }

            await db.SaveChangesAsync();
        }

        try
        {
            await _fixture.Bundler().BundleWebhooksAsync(10, CancellationToken.None);

            await using var check = _fixture.Db.DbFactory.CreateSystem();
            foreach (var (format, id) in channels)
            {
                var rows = await check.OutboxWebhooks.AsNoTracking().Where(w => w.NotificationChannelId == id).ToListAsync();
                if (format == WebhookFormat.Generic)
                {
                    Assert.Equal(8, rows.Count);
                    Assert.All(rows, w => Assert.Null(w.BundledInto));
                    continue;
                }

                var digest = Assert.Single(rows, w => w.Category == NotificationDigest.Category);
                Assert.Equal(3, rows.Count(w => w.BundledInto == digest.Id));
                Assert.Contains("3 alert notifications", digest.Payload);
                Assert.Contains("Resolved", digest.Payload);
            }
        }
        finally
        {
            await using var db = _fixture.Db.DbFactory.CreateSystem();
            await db.NotificationChannels.Where(c => channels.Values.Contains(c.Id)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public void A_digest_email_encodes_what_agents_and_users_wrote()
    {
        var line = new DigestLine(NotificationEvent.Opened, AlertSeverity.Warning, "<b>CPU</b>", "<script>alert(1)</script>", "ACME", "Acme & Co",
            "Main", DateTime.UtcNow, "https://rmm.test.example/endpoints/1");

        var email = EmailTemplates.AlertDigest("rmm.test.example", [line], "https://rmm.test.example/alerts");

        Assert.DoesNotContain("<script>", email.HtmlBody);
        Assert.DoesNotContain("<b>CPU</b>", email.HtmlBody);
        Assert.Contains("&lt;script&gt;", email.HtmlBody);
        Assert.Contains("Acme &amp; Co", email.HtmlBody);
        Assert.Contains("https://rmm.test.example/alerts", email.HtmlBody);
    }
}
