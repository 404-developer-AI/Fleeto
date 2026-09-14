using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fleetify.Gateway.Enrollment;
using Fleetify.Protocol.Agent.V1;
using Fleetify.Testing;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleetify.Gateway.Tests;

/// <summary>
/// Guarantees that enrollment refuses every unusable token with one identical answer (no hint which check failed), writes
/// no signing request for it, and returns the signer's response or a retryable 503.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class EnrollmentTests
{
    private readonly GatewayFixture _fixture;

    public EnrollmentTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Every_unusable_token_gets_the_same_401_and_no_signing_request()
    {
        var database = _fixture.Database;
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        var validator = new EnrollmentTokenValidator(database.DataSource, database.Time);

        var (valid, validRow) = await database.CreateEnrollmentTokenAsync(site);
        Assert.Equal(client.Id, await validator.ValidateAsync(valid, CancellationToken.None));

        var otherSecret = Infrastructure.Security.OpaqueTokens.Create(Infrastructure.Security.OpaqueTokens.EnrollmentPrefix).Token.Split('_', 3)[2];
        var wrongSecret = $"fet_{validRow.Id:N}_{otherSecret}";
        var (expired, _) = await database.CreateEnrollmentTokenAsync(site, lifetime: TimeSpan.FromMinutes(-1));
        var (revoked, revokedRow) = await database.CreateEnrollmentTokenAsync(site);
        var (usedUp, usedUpRow) = await database.CreateEnrollmentTokenAsync(site, maxUses: 1);
        await using (var db = database.DbFactory.CreateSystem())
        {
            await db.EnrollmentTokens.Where(t => t.Id == revokedRow.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, DateTime.UtcNow));
            await db.EnrollmentTokens.Where(t => t.Id == usedUpRow.Id).ExecuteUpdateAsync(s => s.SetProperty(t => t.UseCount, 1));
        }

        var unknown = $"fet_{Guid.NewGuid():N}_{valid.Split('_', 3)[2]}";

        using var harness = _fixture.CreateHarness(o => o.SigningTimeoutSeconds = 1);
        var handler = new EnrollmentHandler(validator, harness.Signing, harness.AllowList, harness.Metrics, NullLogger<EnrollmentHandler>.Instance);
        var requestsBefore = await CountSigningRequestsAsync();

        string? firstBody = null;
        foreach (var token in new[] { wrongSecret, expired, revoked, usedUp, unknown, "fet_garbage", "" })
        {
            Assert.Null(await validator.ValidateAsync(token, CancellationToken.None));
            var (status, body, _) = await PostAsync(handler, token);
            Assert.Equal(StatusCodes.Status401Unauthorized, status);
            Assert.Contains(EnrollmentHandler.TokenRefusedDetail, body);
            firstBody ??= body;
            Assert.Equal(firstBody, body);
        }

        Assert.Equal(requestsBefore, await CountSigningRequestsAsync());
    }

    [Fact]
    public async Task Valid_token_returns_the_enroll_response_from_the_signer()
    {
        var database = _fixture.Database;
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        var (token, _) = await database.CreateEnrollmentTokenAsync(site);
        await using var signer = new FakeSigner(_fixture);
        using var harness = _fixture.CreateHarness();
        var handler = new EnrollmentHandler(new EnrollmentTokenValidator(database.DataSource, database.Time), harness.Signing, harness.AllowList,
            harness.Metrics, NullLogger<EnrollmentHandler>.Instance);

        var (status, _, bytes) = await PostAsync(handler, token);

        Assert.Equal(StatusCodes.Status200OK, status);
        var response = EnrollResponse.Parser.ParseFrom(bytes);
        Assert.Equal(_fixture.Ca.CertificateDer, response.CaCertificateDer.ToByteArray());
        await using var db = database.DbFactory.CreateSystem();
        var request = await db.SigningRequests.SingleAsync(r => r.Kind == Core.Entities.SigningRequestKind.AgentEnrollment && r.ClientId == client.Id);
        Assert.StartsWith("gateway:", request.RequestedBy);
    }

    [Fact]
    public async Task Signer_that_does_not_answer_gives_a_retryable_503()
    {
        var database = _fixture.Database;
        var client = await database.CreateClientAsync();
        var site = await database.CreateSiteAsync(client.Id);
        var (token, _) = await database.CreateEnrollmentTokenAsync(site);
        using var harness = _fixture.CreateHarness(o => o.SigningTimeoutSeconds = 1);
        var handler = new EnrollmentHandler(new EnrollmentTokenValidator(database.DataSource, database.Time), harness.Signing, harness.AllowList,
            harness.Metrics, NullLogger<EnrollmentHandler>.Instance);

        var (status, body, _, headers) = await PostWithHeadersAsync(handler, token);

        Assert.Equal(StatusCodes.Status503ServiceUnavailable, status);
        Assert.Equal("10", headers.RetryAfter.ToString());
        Assert.Contains("Try again", body);
    }

    [Fact]
    public async Task Wrong_content_type_and_oversized_body_are_rejected_before_any_lookup()
    {
        using var harness = _fixture.CreateHarness();
        var handler = new EnrollmentHandler(new EnrollmentTokenValidator(_fixture.Database.DataSource, _fixture.Database.Time), harness.Signing, harness.AllowList,
            harness.Metrics, NullLogger<EnrollmentHandler>.Instance);

        var context = NewContext(Encoding.UTF8.GetBytes("{}"), "application/json");
        await handler.HandleAsync(context);
        Assert.Equal(StatusCodes.Status415UnsupportedMediaType, context.Response.StatusCode);

        context = NewContext(new byte[70 * 1024], "application/x-protobuf");
        await handler.HandleAsync(context);
        Assert.Equal(StatusCodes.Status413PayloadTooLarge, context.Response.StatusCode);
    }

    private async Task<long> CountSigningRequestsAsync()
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.SigningRequests.LongCountAsync();
    }

    private static async Task<(int Status, string Body, byte[] Bytes)> PostAsync(EnrollmentHandler handler, string token)
    {
        var (status, body, bytes, _) = await PostWithHeadersAsync(handler, token);
        return (status, body, bytes);
    }

    private static async Task<(int Status, string Body, byte[] Bytes, IHeaderDictionary Headers)> PostWithHeadersAsync(EnrollmentHandler handler, string token)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new EnrollRequest
        {
            Token = token,
            CsrDer = ByteString.CopyFrom(new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256).CreateSigningRequest()),
            Hostname = "WS-ENROLL",
            AgentVersion = "0.1.0",
            Os = new OsInfo { Platform = "windows", Name = "Windows 11 Pro", Version = "10.0.26200", Architecture = "amd64" }
        };

        var context = NewContext(request.ToByteArray(), "application/x-protobuf");
        await handler.HandleAsync(context);
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        return (context.Response.StatusCode, Encoding.UTF8.GetString(bytes), bytes, context.Response.Headers);
    }

    private static DefaultHttpContext NewContext(byte[] body, string contentType)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider()
        };
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.20");
        return context;
    }
}
