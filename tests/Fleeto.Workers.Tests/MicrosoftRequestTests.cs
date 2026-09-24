using System.Net;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Identity;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.SignIn;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees of the questions Settings asks Microsoft (0.5.0): the workers answer them with the credential from the
/// settings, a test says per line what works and what to do about the rest, a search returns members of the tenant and never
/// guests, a token is reused while typing, and no answer lingers in the database.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class MicrosoftRequestTests : IAsyncLifetime
{
    private const string Tenant = "8fa1c6b2-91d2-4b4e-9a0e-2f3c4d5e6a7b";
    private const string ClientId = "0b6f1c9e-2f6a-4d2b-9a55-4f1c2a3b4c5d";
    private const string Secret = "super-secret-value";

    private readonly WorkersFixture _fixture;

    public MicrosoftRequestTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task A_test_of_the_sign_in_reports_the_credential_and_the_permission_to_read_users()
    {
        await ConfigureSignInAsync();
        var microsoft = new FakeMicrosoft { Roles = [MicrosoftGraphClient.UserReadAll] };
        var id = await AskAsync(MicrosoftRequestKind.TestSignIn);

        await Service(microsoft).AnswerWaitingAsync(CancellationToken.None);

        var request = await ReadAsync(id);
        Assert.Equal(MicrosoftRequestState.Completed, request!.State);
        var checks = MicrosoftAnswers.Checks(request.ResultJson);
        Assert.Equal([MicrosoftCheckStatus.Ok, MicrosoftCheckStatus.Ok, MicrosoftCheckStatus.Info], checks.Select(c => c.Status));
        Assert.Contains("client secret are correct", checks[0].Text);
        Assert.Contains(MicrosoftGraphClient.UserReadAll, checks[1].Text);
        // The answer holds no secret and no token.
        Assert.DoesNotContain(Secret, request.ResultJson);
        Assert.DoesNotContain("eyJ", request.ResultJson);
        Assert.Contains($"client_secret={Secret}", microsoft.TokenBodies.Single());
    }

    [Fact]
    public async Task A_registration_without_User_Read_All_or_with_more_than_it_needs_is_a_warning()
    {
        await ConfigureSignInAsync();
        var microsoft = new FakeMicrosoft { Roles = [MicrosoftGraphClient.MailSend], CanReadUsers = false };
        var id = await AskAsync(MicrosoftRequestKind.TestSignIn);

        await Service(microsoft).AnswerWaitingAsync(CancellationToken.None);

        var checks = MicrosoftAnswers.Checks((await ReadAsync(id))!.ResultJson);
        Assert.Equal(MicrosoftCheckStatus.Ok, checks[0].Status);
        Assert.Equal(MicrosoftCheckStatus.Warning, checks[1].Status);
        Assert.Contains("object ID", checks[1].Text);
        Assert.Contains(checks, c => c.Status == MicrosoftCheckStatus.Warning && c.Text.Contains("also holds Mail.Send"));
    }

    [Fact]
    public async Task A_refused_secret_names_the_cause_and_the_page()
    {
        await ConfigureSignInAsync();
        var microsoft = new FakeMicrosoft { TokenError = 7000215 };
        var id = await AskAsync(MicrosoftRequestKind.TestSignIn);

        await Service(microsoft).AnswerWaitingAsync(CancellationToken.None);

        var check = Assert.Single(MicrosoftAnswers.Checks((await ReadAsync(id))!.ResultJson));
        Assert.Equal(MicrosoftCheckStatus.Failed, check.Status);
        Assert.Contains("secret value (not its ID)", check.Text);
        Assert.Contains("Settings, Sign-in", check.Text);
    }

    [Fact]
    public async Task A_test_of_Graph_email_checks_Mail_Send()
    {
        await _fixture.Settings().SetAsync(SettingKeys.Graph, new GraphMailSettings
        {
            TenantId = Tenant,
            ClientId = ClientId,
            SenderAddress = "fleeto@contoso.com",
            CredentialType = GraphCredentialType.ClientSecret,
            ClientSecret = Secret,
            ClientSecretExpiresAt = _fixture.Now.AddDays(90)
        }, encrypted: true, userId: null);
        await ClearAsync();

        var granted = await AskAsync(MicrosoftRequestKind.TestEmail);
        await Service(new FakeMicrosoft { Roles = [MicrosoftGraphClient.MailSend] }).AnswerWaitingAsync(CancellationToken.None);
        var checks = MicrosoftAnswers.Checks((await ReadAsync(granted))!.ResultJson);
        Assert.Equal([MicrosoftCheckStatus.Ok, MicrosoftCheckStatus.Ok, MicrosoftCheckStatus.Info], checks.Select(c => c.Status));
        Assert.Contains("fleeto@contoso.com", checks[2].Text);

        var missing = await AskAsync(MicrosoftRequestKind.TestEmail);
        await Service(new FakeMicrosoft { Roles = [] }).AnswerWaitingAsync(CancellationToken.None);
        Assert.Contains(MicrosoftAnswers.Checks((await ReadAsync(missing))!.ResultJson),
            c => c.Status == MicrosoftCheckStatus.Failed && c.Text.Contains("Mail.Send is not granted"));
    }

    [Fact]
    public async Task A_search_returns_members_only_and_reuses_the_token()
    {
        await ConfigureSignInAsync();
        var microsoft = new FakeMicrosoft
        {
            Roles = [MicrosoftGraphClient.UserReadAll],
            Users = """
                    [{"id":"a1b2c3d4-e5f6-4789-9abc-def012345678","displayName":"Jan Peeters","userPrincipalName":"jan@contoso.com","mail":"jan@contoso.com"},
                     {"id":"b1b2c3d4-e5f6-4789-9abc-def012345678","displayName":"A Guest","userPrincipalName":"guest_fabrikam.com#EXT#@contoso.onmicrosoft.com","mail":null},
                     {"id":"not-a-guid","displayName":"Broken"}]
                    """
        };
        var service = Service(microsoft);

        var first = await AskAsync(MicrosoftRequestKind.SearchUsers, "jan");
        await service.AnswerWaitingAsync(CancellationToken.None);
        var second = await AskAsync(MicrosoftRequestKind.SearchUsers, "pee");
        await service.AnswerWaitingAsync(CancellationToken.None);

        var users = MicrosoftAnswers.Users((await ReadAsync(first))!.ResultJson);
        var user = Assert.Single(users);
        Assert.Equal(Guid.Parse("a1b2c3d4-e5f6-4789-9abc-def012345678"), user.ObjectId);
        Assert.Equal("jan@contoso.com", user.UserPrincipalName);
        Assert.Equal(MicrosoftRequestState.Completed, (await ReadAsync(second))!.State);

        // Members with an enabled account, found by name, account or address, as an advanced query.
        var search = microsoft.SearchUris[0];
        Assert.Contains(Uri.EscapeDataString("userType eq 'Member' and accountEnabled eq true"), search);
        Assert.Contains(Uri.EscapeDataString("\"displayName:jan\""), search);
        Assert.True(microsoft.ConsistencyLevelEventual);
        Assert.Single(microsoft.TokenBodies);
    }

    [Fact]
    public async Task A_search_without_a_configured_sign_in_fails_with_a_reason_and_old_rows_go()
    {
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Settings.Where(s => s.Key == SettingKeys.EntraSignIn).ExecuteDeleteAsync();
        }

        await ClearAsync();
        var old = await AskAsync(MicrosoftRequestKind.SearchUsers, "x", _fixture.Now - MicrosoftAnswers.Lifetime - TimeSpan.FromMinutes(1));
        var id = await AskAsync(MicrosoftRequestKind.SearchUsers, "jan");
        var microsoft = new FakeMicrosoft();

        await Service(microsoft).AnswerWaitingAsync(CancellationToken.None);

        var request = await ReadAsync(id);
        Assert.Equal(MicrosoftRequestState.Failed, request!.State);
        Assert.Contains("Settings, Sign-in", request.FailureReason);
        Assert.Empty(microsoft.TokenBodies);
        Assert.Null(await ReadAsync(old));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    // The credentials of these tests would otherwise raise expiry warnings in tests that move the clock by months.
    public async Task DisposeAsync()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Settings.Where(s => s.Key == SettingKeys.EntraSignIn || s.Key == SettingKeys.Graph).ExecuteDeleteAsync();
        await db.MicrosoftRequests.ExecuteDeleteAsync();
    }

    private MicrosoftRequestService Service(FakeMicrosoft microsoft) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus, _fixture.Settings(), new MicrosoftGraphClient(_fixture.Db.Time, handler: microsoft),
            _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<MicrosoftRequestService>.Instance);

    private async Task ConfigureSignInAsync()
    {
        await _fixture.Settings().SetAsync(SettingKeys.EntraSignIn, new EntraSignInSettings
        {
            Enabled = true,
            TenantId = Tenant,
            ClientId = ClientId,
            ClientSecret = Secret,
            ClientSecretExpiresAt = _fixture.Now.AddDays(90)
        }, encrypted: true, userId: null);
        await ClearAsync();
    }

    private async Task ClearAsync()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.MicrosoftRequests.ExecuteDeleteAsync();
    }

    private async Task<Guid> AskAsync(MicrosoftRequestKind kind, string? query = null, DateTime? createdAt = null)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var request = new MicrosoftRequest
        {
            Id = Guid.CreateVersion7(),
            CreatedAt = createdAt ?? _fixture.Now,
            RequestedBy = Guid.NewGuid(),
            Kind = kind,
            Query = query
        };
        db.MicrosoftRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task<MicrosoftRequest?> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.MicrosoftRequests.AsNoTracking().SingleOrDefaultAsync(r => r.Id == id);
    }

    /// <summary>Answers the token endpoint of Entra ID and the users of Microsoft Graph.</summary>
    private sealed class FakeMicrosoft : HttpMessageHandler
    {
        public IReadOnlyList<string> Roles { get; init; } = [];
        public bool CanReadUsers { get; init; } = true;
        public int? TokenError { get; init; }
        public string Users { get; init; } = "[]";

        public List<string> TokenBodies { get; } = [];
        public List<string> SearchUris { get; } = [];
        public bool ConsistencyLevelEventual { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.AbsoluteUri;
            if (request.RequestUri.Host == "login.microsoftonline.com")
            {
                TokenBodies.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
                return TokenError is { } code
                    ? Json(HttpStatusCode.Unauthorized, $$"""{"error":"invalid_client","error_codes":[{{code}}]}""")
                    : Json(HttpStatusCode.OK, $$"""{"token_type":"Bearer","expires_in":3599,"access_token":"{{AccessToken()}}"}""");
            }

            if (request.Headers.Authorization?.Parameter != AccessToken())
            {
                return Json(HttpStatusCode.Unauthorized, "{}");
            }

            if (uri.Contains("$top=1&", StringComparison.Ordinal))
            {
                return CanReadUsers ? Json(HttpStatusCode.OK, """{"value":[]}""") : Json(HttpStatusCode.Forbidden, """{"error":{"code":"Authorization_RequestDenied"}}""");
            }

            SearchUris.Add(uri);
            ConsistencyLevelEventual = request.Headers.TryGetValues("ConsistencyLevel", out var values) && values.Single() == "eventual";
            return Json(HttpStatusCode.OK, $$"""{"value":{{Users}}}""");
        }

        private string AccessToken()
        {
            static string Encode(string json) => Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var roles = string.Join(",", Roles.Select(r => $"\"{r}\""));
            return Encode("""{"alg":"RS256","typ":"JWT"}""") + "." + Encode($$"""{"tid":"{{Tenant}}","roles":[{{roles}}]}""") + ".signature";
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
