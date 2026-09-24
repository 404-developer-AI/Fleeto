using Fleeto.Workers.Email;
using System.Net;
using System.Text;
using System.Text.Json;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Email;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Licensing;
using Fleeto.Workers.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees Microsoft Graph email and expiring credentials (0.2.0): one token per session sent as bearer to sendMail for the
/// configured mailbox, a refused recipient is permanent, an expired secret gives its cause and next step, a refused token is
/// renewed once; the transport falls back to SMTP only while the Graph credential has expired; admins are warned at each stage
/// once, and a renewed credential starts over.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class GraphEmailTests
{
    private readonly WorkersFixture _fixture;

    public GraphEmailTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string? Authorization, string Body)> Requests { get; } = [];
        public Func<HttpRequestMessage, string, HttpResponseMessage> Respond { get; set; } = (_, _) => new HttpResponseMessage(HttpStatusCode.Accepted);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), body));
            return Respond(request, body);
        }
    }

    private MicrosoftGraphClient Microsoft(FakeHandler handler) => new(_fixture.Db.Time, handler: handler);

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Graph(HttpRequestMessage request, Func<HttpResponseMessage> send, int[] tokenCounter)
    {
        if (request.RequestUri!.Host == "login.microsoftonline.com")
        {
            tokenCounter[0]++;
            return Json(HttpStatusCode.OK, $$"""{"token_type":"Bearer","expires_in":3599,"access_token":"token-{{tokenCounter[0]}}"}""");
        }

        return send();
    }

    private GraphMailSettings SecretSettings(DateTime expiresAt) => new()
    {
        TenantId = "contoso.onmicrosoft.com",
        ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d",
        SenderAddress = "fleeto@contoso.com",
        ClientSecret = "secret-value",
        ClientSecretExpiresAt = expiresAt
    };

    private OutboxEmail Email(string to = "ops@contoso.com") =>
        OutboxEmails.Create(to, new EmailContent("Subject line", "<p>Hello</p>", "Hello"), "test", _fixture.Now);

    [Fact]
    public async Task One_token_is_used_for_every_email_of_a_session()
    {
        var tokens = new int[1];
        var handler = new FakeHandler();
        handler.Respond = (request, _) => Graph(request, () => new HttpResponseMessage(HttpStatusCode.Accepted), tokens);
        await using var session = new GraphEmailSession(SecretSettings(_fixture.Now.AddDays(90)), Microsoft(handler), new HttpClient(handler), _fixture.Db.Time);

        await session.SendAsync(Email(), CancellationToken.None);
        await session.SendAsync(Email("second@contoso.com"), CancellationToken.None);

        Assert.Equal(1, tokens[0]);
        var token = Assert.Single(handler.Requests, r => r.Uri.Host == "login.microsoftonline.com");
        Assert.Equal("/contoso.onmicrosoft.com/oauth2/v2.0/token", token.Uri.AbsolutePath);
        Assert.Contains("client_secret=secret-value", token.Body);
        Assert.Contains("grant_type=client_credentials", token.Body);

        var sends = handler.Requests.Where(r => r.Uri.Host == "graph.microsoft.com").ToList();
        Assert.Equal(2, sends.Count);
        Assert.All(sends, s => Assert.Equal("Bearer token-1", s.Authorization));
        Assert.Equal("/v1.0/users/fleeto%40contoso.com/sendMail", sends[0].Uri.AbsolutePath);
        using var json = JsonDocument.Parse(sends[0].Body);
        var message = json.RootElement.GetProperty("message");
        Assert.Equal("Subject line", message.GetProperty("subject").GetString());
        Assert.Equal("ops@contoso.com", message.GetProperty("toRecipients")[0].GetProperty("emailAddress").GetProperty("address").GetString());
        Assert.False(json.RootElement.GetProperty("saveToSentItems").GetBoolean());
    }

    [Fact]
    public async Task Refusals_are_classified_with_cause_and_next_step()
    {
        var tokens = new int[1];
        var handler = new FakeHandler();
        handler.Respond = (request, _) => Graph(request,
            () => Json(HttpStatusCode.BadRequest, """{"error":{"code":"ErrorInvalidRecipients","message":"x"}}"""), tokens);
        await using (var session = new GraphEmailSession(SecretSettings(_fixture.Now.AddDays(90)), Microsoft(handler), new HttpClient(handler), _fixture.Db.Time))
        {
            var refused = await Assert.ThrowsAsync<EmailDeliveryException>(() => session.SendAsync(Email(), CancellationToken.None));
            Assert.True(refused.IsPermanent);
        }

        handler.Respond = (_, _) => Json(HttpStatusCode.Unauthorized,
            """{"error":"invalid_client","error_description":"AADSTS7000222: The provided client secret keys are expired. Trace ID: abc","error_codes":[7000222]}""");
        await using (var session = new GraphEmailSession(SecretSettings(_fixture.Now.AddDays(90)), Microsoft(handler), new HttpClient(handler), _fixture.Db.Time))
        {
            var expired = await Assert.ThrowsAsync<EmailDeliveryException>(() => session.SendAsync(Email(), CancellationToken.None));
            Assert.False(expired.IsPermanent);
            Assert.Contains("client secret has expired", expired.Message);
            Assert.DoesNotContain("Trace ID", expired.Message);
        }

        // A refused token is renewed once, then the email goes out.
        var sendCalls = 0;
        handler.Respond = (request, _) => Graph(request, () => ++sendCalls == 1
            ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
            : new HttpResponseMessage(HttpStatusCode.Accepted), tokens);
        tokens[0] = 0;
        await using (var session = new GraphEmailSession(SecretSettings(_fixture.Now.AddDays(90)), Microsoft(handler), new HttpClient(handler), _fixture.Db.Time))
        {
            await session.SendAsync(Email(), CancellationToken.None);
            Assert.Equal(2, tokens[0]);
        }
    }

    [Fact]
    public async Task A_certificate_signs_in_with_a_client_assertion()
    {
        var certificate = GraphMail.CreateCertificate("rmm.test.example", _fixture.Now);
        var settings = SecretSettings(_fixture.Now) with
        {
            CredentialType = GraphCredentialType.Certificate, ClientSecret = null, CertificatePfx = certificate.PfxBase64,
            CertificateThumbprint = certificate.Thumbprint, CertificateExpiresAt = certificate.ExpiresAt
        };
        var tokens = new int[1];
        var handler = new FakeHandler();
        handler.Respond = (request, _) => Graph(request, () => new HttpResponseMessage(HttpStatusCode.Accepted), tokens);
        await using var session = new GraphEmailSession(settings, Microsoft(handler), new HttpClient(handler), _fixture.Db.Time);

        await session.SendAsync(Email(), CancellationToken.None);

        var token = Assert.Single(handler.Requests, r => r.Uri.Host == "login.microsoftonline.com");
        Assert.Contains("client_assertion_type=urn%3Aietf%3Aparams%3Aoauth%3Aclient-assertion-type%3Ajwt-bearer", token.Body);
        Assert.Contains("client_assertion=ey", token.Body);
        Assert.DoesNotContain("client_secret", token.Body);
    }

    private async Task WithEmailSettingsAsync(GraphMailSettings? graph, SmtpSettings? smtp, Func<Task> test)
    {
        var settings = _fixture.Settings();
        await settings.SetStringAsync(SettingKeys.EmailProvider, nameof(EmailProvider.MicrosoftGraph), encrypted: false, null);
        if (graph is not null)
        {
            await settings.SetAsync(SettingKeys.Graph, graph, encrypted: true, null);
        }

        if (smtp is not null)
        {
            await settings.SetAsync(SettingKeys.Smtp, smtp, encrypted: true, null);
        }

        try
        {
            await test();
        }
        finally
        {
            await using var db = _fixture.Db.DbFactory.CreateSystem();
            await db.Settings.Where(s => s.Key.StartsWith("email.") || s.Key.StartsWith(SettingKeys.CredentialWarningPrefix)).ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task The_transport_falls_back_to_smtp_only_while_the_graph_credential_has_expired()
    {
        var smtp = new SmtpSettings { Host = "smtp.test.example", FromAddress = "fleeto@test.example" };
        using var factory = new EmailTransportFactory(_fixture.Settings(), MsOptions.Create(new EmailOptions()), NullLoggerFactory.Instance,
            _fixture.Db.Time, new MicrosoftGraphClient(_fixture.Db.Time, handler: new FakeHandler()), new FakeHandler());

        await WithEmailSettingsAsync(SecretSettings(_fixture.Now.AddDays(10)), smtp, async () =>
        {
            await using (var session = await factory.CreateSessionAsync(CancellationToken.None))
            {
                Assert.IsType<GraphEmailSession>(session);
            }

            await _fixture.Settings().SetAsync(SettingKeys.Graph, SecretSettings(_fixture.Now.AddDays(-1)), encrypted: true, null);
            await using (var session = await factory.CreateSessionAsync(CancellationToken.None))
            {
                Assert.IsType<SmtpEmailSession>(session);
            }

            await using (var db = _fixture.Db.DbFactory.CreateSystem())
            {
                await db.Settings.Where(s => s.Key == SettingKeys.Smtp).ExecuteDeleteAsync();
            }

            // Without SMTP, Graph keeps trying, so the error states the cause.
            await using (var session = await factory.CreateSessionAsync(CancellationToken.None))
            {
                Assert.IsType<GraphEmailSession>(session);
            }
        });
    }

    [Fact]
    public async Task Admins_are_warned_once_per_stage_and_a_renewed_credential_starts_over()
    {
        await _fixture.ClearOutboxAsync();
        var admin = await _fixture.CreateAdminAsync();
        var service = new CredentialExpiryService(_fixture.Db.DbFactory, _fixture.Settings(), _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<CredentialExpiryService>.Instance);
        var expiresAt = _fixture.Now.AddDays(40);

        async Task<List<OutboxEmail>> WarningsAsync()
        {
            await using var db = _fixture.Db.DbFactory.CreateSystem();
            return await db.OutboxEmails.AsNoTracking().Where(e => e.ToAddress == admin.Email && e.Category == OutboxEmails.CategoryCredential)
                .OrderBy(e => e.CreatedAt).ToListAsync();
        }

        await WithEmailSettingsAsync(SecretSettings(expiresAt), null, async () =>
        {
            await service.CheckAsync(CancellationToken.None);
            Assert.Empty(await WarningsAsync());

            _fixture.Db.Time.Advance(TimeSpan.FromDays(11));
            await service.CheckAsync(CancellationToken.None);
            await service.CheckAsync(CancellationToken.None);
            var first = Assert.Single(await WarningsAsync());
            Assert.Contains("The Microsoft Graph client secret for email", first.Subject);
            Assert.Contains("expires on", first.Subject);

            // Skipping from 29 days straight to 3 days sends one warning, not two.
            _fixture.Db.Time.Advance(TimeSpan.FromDays(26));
            await service.CheckAsync(CancellationToken.None);
            Assert.Equal(2, (await WarningsAsync()).Count);

            _fixture.Db.Time.Advance(TimeSpan.FromDays(4));
            await service.CheckAsync(CancellationToken.None);
            var expired = (await WarningsAsync()).Last();
            Assert.Contains("has expired", expired.Subject);
            Assert.Contains("Create a new client secret", expired.TextBody);
            Assert.Equal(3, (await WarningsAsync()).Count);

            // Renewed: a new end date far away sends nothing, and its own 30-day warning later.
            await _fixture.Settings().SetAsync(SettingKeys.Graph, SecretSettings(_fixture.Now.AddDays(365)), encrypted: true, null);
            await service.CheckAsync(CancellationToken.None);
            Assert.Equal(3, (await WarningsAsync()).Count);
            _fixture.Db.Time.Advance(TimeSpan.FromDays(340));
            await service.CheckAsync(CancellationToken.None);
            Assert.Equal(4, (await WarningsAsync()).Count);
        });
    }
}
