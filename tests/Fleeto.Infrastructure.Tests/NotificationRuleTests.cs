using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Notifications;
using Fleeto.Infrastructure.Security;
using Fleeto.Testing;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Notification rules (0.2.0): routing per client, severity and resolve; webhook URLs only to public https addresses, the
/// address policy that the delivery re-checks on the resolved address, the documented signature, and payloads that are valid
/// for Slack, Teams and the generic format.
/// </summary>
public class NotificationRuleTests
{
    private static readonly Guid ClientA = Guid.NewGuid();
    private static readonly Guid ClientB = Guid.NewGuid();

    [Fact]
    public void Routing_follows_clients_minimum_severity_resolves_and_enabled()
    {
        var all = new NotificationChannel { AllClients = true, MinimumSeverity = AlertSeverity.Warning, NotifyOnResolve = true };
        var onlyA = new NotificationChannel { AllClients = false, MinimumSeverity = AlertSeverity.Warning, NotifyOnResolve = false };
        var critical = new NotificationChannel { AllClients = true, MinimumSeverity = AlertSeverity.Critical, NotifyOnResolve = true };
        var off = new NotificationChannel { AllClients = true, Enabled = false };
        IReadOnlySet<Guid> none = new HashSet<Guid>();
        IReadOnlySet<Guid> a = new HashSet<Guid> { ClientA };

        Assert.True(NotificationRouting.Receives(all, none, ClientB, AlertSeverity.Warning, NotificationEvent.Opened));
        Assert.True(NotificationRouting.Receives(onlyA, a, ClientA, AlertSeverity.Warning, NotificationEvent.Opened));
        Assert.False(NotificationRouting.Receives(onlyA, a, ClientB, AlertSeverity.Critical, NotificationEvent.Opened));
        Assert.False(NotificationRouting.Receives(onlyA, a, ClientA, AlertSeverity.Warning, NotificationEvent.Resolved));
        Assert.False(NotificationRouting.Receives(critical, none, ClientA, AlertSeverity.Warning, NotificationEvent.Opened));
        Assert.True(NotificationRouting.Receives(critical, none, ClientA, AlertSeverity.Critical, NotificationEvent.Escalated));
        Assert.False(NotificationRouting.Receives(off, none, ClientA, AlertSeverity.Critical, NotificationEvent.Opened));
    }

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.18.0.5", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("100.100.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("::", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("::ffff:127.0.0.1", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("64:ff9b::10.0.0.1", false)]
    [InlineData("2002:c0a8:0101::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001:0:4136:e378:8000:63bf:3fff:fdd2", false)]
    public void Only_public_addresses_are_allowed(string address, bool expected)
    {
        Assert.Equal(expected, NetworkAddressPolicy.IsPublic(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("https://hooks.slack.com/services/T0/B0/xyz", null)]
    [InlineData("https://example.com:8443/hook?a=1", null)]
    [InlineData("http://example.com/hook", "https")]
    [InlineData("https://user:pass@example.com/hook", "user name or password")]
    [InlineData("https://127.0.0.1/hook", "private or local address")]
    [InlineData("https://[::1]/hook", "private or local address")]
    [InlineData("https://169.254.169.254/latest", "private or local address")]
    [InlineData("https://localhost/hook", "local host name")]
    [InlineData("https://fleeto-web/hook", "local host name")]
    [InlineData("https://metadata.google.internal/", "local host name")]
    [InlineData("ftp://example.com/", "https")]
    [InlineData("", "Enter the webhook URL")]
    public void Webhook_urls_must_be_public_https_without_credentials(string url, string? problem)
    {
        var result = WebhookTargets.ValidateUrl(url, out var uri);

        if (problem is null)
        {
            Assert.Null(result);
            Assert.NotNull(uri);
        }
        else
        {
            Assert.NotNull(result);
            Assert.Contains(problem, result);
            Assert.Null(uri);
        }
    }

    [Fact]
    public void The_signature_is_hmac_sha256_over_timestamp_and_body()
    {
        var secret = WebhookTargets.NewSigningSecret();
        var body = """{"type":"test"}""";
        var time = DateTimeOffset.FromUnixTimeSeconds(1_789_000_000);

        var header = WebhookTargets.Sign(secret, time, body);

        var expected = Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes("1789000000." + body)));
        Assert.Equal($"t=1789000000,v1={expected}", header);
        Assert.StartsWith("whsec_", secret);
        Assert.Equal(6 + 43, secret.Length);
        Assert.NotEqual(secret, WebhookTargets.NewSigningSecret());
    }

    private static AlertNotification Alert(NotificationEvent kind) => new(kind, Guid.NewGuid(), "SRV-01 has less than 10% free disk space on C:",
        "Free space 4.2 GB <of> 100 GB & falling", AlertSeverity.Critical, new DateTime(2026, 9, 15, 8, 0, 0, DateTimeKind.Utc),
        kind == NotificationEvent.Resolved ? new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc) : null,
        kind == NotificationEvent.Resolved ? "The check recovered." : null, Guid.NewGuid(), "SRV-01", Guid.NewGuid(), "Monitoring", Guid.NewGuid(),
        "ACME", "Acme Corp", "https://rmm.example.com/endpoints/1");

    [Fact]
    public void Generic_payload_carries_the_alert_facts()
    {
        var delivery = Guid.NewGuid();
        var alert = Alert(NotificationEvent.Escalated);

        using var json = JsonDocument.Parse(WebhookPayloads.Alert(WebhookFormat.Generic, delivery, "rmm.example.com", alert, alert.OpenedAt));
        var root = json.RootElement;

        Assert.Equal(delivery, root.GetProperty("id").GetGuid());
        Assert.Equal("alert.escalated", root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("2026-09-15T08:00:00Z", root.GetProperty("createdAt").GetString());
        var body = root.GetProperty("alert");
        Assert.Equal("critical", body.GetProperty("severity").GetString());
        Assert.Equal("open", body.GetProperty("state").GetString());
        Assert.Equal(JsonValueKind.Null, body.GetProperty("resolvedAt").ValueKind);
        Assert.Equal("ACME", body.GetProperty("client").GetProperty("code").GetString());
        Assert.Equal("SRV-01", body.GetProperty("endpoint").GetProperty("hostname").GetString());
        Assert.Equal(alert.Detail, body.GetProperty("detail").GetString());
    }

    [Fact]
    public void Slack_and_Teams_payloads_are_escaped_and_shaped_for_their_receivers()
    {
        var alert = Alert(NotificationEvent.Resolved);

        using var slack = JsonDocument.Parse(WebhookPayloads.Alert(WebhookFormat.Slack, Guid.NewGuid(), "rmm.example.com", alert, alert.OpenedAt));
        Assert.Equal("Resolved: SRV-01 has less than 10% free disk space on C:", slack.RootElement.GetProperty("text").GetString());
        var blocks = slack.RootElement.GetProperty("blocks");
        Assert.Contains("Client: Acme Corp (ACME)", blocks[0].GetProperty("text").GetProperty("text").GetString());
        Assert.Equal("The check recovered.", blocks[1].GetProperty("text").GetProperty("text").GetString());
        Assert.Equal("<https://rmm.example.com/endpoints/1|Open the endpoint>", blocks[2].GetProperty("elements")[0].GetProperty("text").GetString());

        var opened = Alert(NotificationEvent.Opened) with { Title = "A <b>&</b> title" };
        using var escaped = JsonDocument.Parse(WebhookPayloads.Alert(WebhookFormat.Slack, Guid.NewGuid(), "rmm.example.com", opened, opened.OpenedAt));
        Assert.StartsWith("*A &lt;b&gt;&amp;&lt;/b&gt; title*", escaped.RootElement.GetProperty("blocks")[0].GetProperty("text").GetProperty("text").GetString());

        using var teams = JsonDocument.Parse(WebhookPayloads.Alert(WebhookFormat.Teams, Guid.NewGuid(), "rmm.example.com", opened, opened.OpenedAt));
        var attachment = teams.RootElement.GetProperty("attachments")[0];
        Assert.Equal("message", teams.RootElement.GetProperty("type").GetString());
        Assert.Equal("application/vnd.microsoft.card.adaptive", attachment.GetProperty("contentType").GetString());
        var card = attachment.GetProperty("content");
        Assert.Equal("AdaptiveCard", card.GetProperty("type").GetString());
        Assert.Equal("Attention", card.GetProperty("body")[0].GetProperty("color").GetString());
        Assert.Equal("https://rmm.example.com/endpoints/1", card.GetProperty("actions")[0].GetProperty("url").GetString());

        foreach (var format in Enum.GetValues<WebhookFormat>())
        {
            using var test = JsonDocument.Parse(WebhookPayloads.Test(format, Guid.NewGuid(), "rmm.example.com", "https://rmm.example.com/settings", alert.OpenedAt));
            Assert.Contains("rmm.example.com", test.RootElement.GetRawText());
        }
    }
}

/// <summary>Webhook targets are encrypted and bound to their channel: a ciphertext moved to another channel does not decrypt.</summary>
[Collection(DatabaseCollection.Name)]
public class WebhookTargetProtectionTests
{
    private readonly TestDatabase _db;

    public WebhookTargetProtectionTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public void Targets_round_trip_only_for_their_own_channel()
    {
        var channel = Guid.NewGuid();
        var target = new WebhookTarget("https://hooks.slack.com/services/T0/B0/secret-part", null);

        var stored = WebhookTargets.Protect(_db.SecretProtector, channel, target);

        Assert.DoesNotContain("secret-part", stored);
        Assert.Equal(target, WebhookTargets.Unprotect(_db.SecretProtector, channel, stored));
        Assert.ThrowsAny<CryptographicException>(() => WebhookTargets.Unprotect(_db.SecretProtector, Guid.NewGuid(), stored));
    }
}
