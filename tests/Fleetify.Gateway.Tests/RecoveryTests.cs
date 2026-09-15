using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Fleetify.Core.Entities;
using Fleetify.Gateway.Enrollment;
using Fleetify.Gateway.Sessions;
using Fleetify.Gateway.Tls;
using Fleetify.Infrastructure.Security;
using Fleetify.Protocol;
using Fleetify.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Endpoint = Fleetify.Core.Entities.Endpoint;

namespace Fleetify.Gateway.Tests;

/// <summary>
/// Guarantees certificate recovery in the gateway (0.2.0): the TLS handshake lets through an agent certificate whose only problem
/// is its own validity period, never one from another CA; an expired, never revoked certificate within a year is accepted for
/// recovery only and never for a session, which answers with the recovery hint instead; recovery needs the same key and returns
/// the signer's certificate.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class RecoveryTests
{
    private readonly GatewayFixture _fixture;

    public RecoveryTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    private DateTime Now => _fixture.Database.Time.GetUtcNow().UtcDateTime;

    /// <summary>A CA created long ago (stored as an instance CA) and an agent certificate from it that expired <paramref name="expiredDaysAgo"/> days ago.</summary>
    private async Task<(Endpoint Endpoint, AgentCredential Credential, InternalCertificateAuthority.CaMaterial Ca, Guid CaRowId)> ExpiredAsync(
        int expiredDaysAgo, DateTime? revokedAt = null)
    {
        var endpoint = await _fixture.CreateEndpointAsync();
        var ca = InternalCertificateAuthority.CreateCa(TestDatabaseFqdn, Now.AddDays(-expiredDaysAgo - 200));
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        var issued = InternalCertificateAuthority.IssueAgentCertificate(ca.CertificateDer, ca.PrivateKeyPkcs8, csr, endpoint.Id, _fixture.Database.InstanceId,
            Now.AddDays(-expiredDaysAgo - 90));
        var caRowId = Guid.NewGuid();
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.CertificateAuthorities.Add(new CertificateAuthority
        {
            Id = caRowId, CertificateDer = ca.CertificateDer, Fingerprint = ca.Fingerprint, EncryptedPrivateKey = [0], CreatedAt = Now.AddDays(-expiredDaysAgo - 200),
            ExpiresAt = ca.ExpiresAt
        });
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Fingerprint = issued.Fingerprint,
            PublicKeyFingerprint = issued.PublicKeyFingerprint, SerialNumber = issued.SerialNumber, IssuedAt = issued.NotBefore, ExpiresAt = issued.NotAfter,
            RevokedAt = revokedAt
        });
        await db.SaveChangesAsync();
        return (endpoint, new AgentCredential(issued, key), ca, caRowId);
    }

    private const string TestDatabaseFqdn = "rmm.test.example";

    private async Task RemoveCaAsync(Guid caRowId)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.CertificateAuthorities.Where(c => c.Id == caRowId).ExecuteDeleteAsync();
    }

    [Fact]
    public void The_handshake_accepts_an_expired_agent_certificate_but_never_one_from_another_ca()
    {
        var now = DateTime.UtcNow;
        var ca = InternalCertificateAuthority.CreateCa(TestDatabaseFqdn, now.AddDays(-300));
        var otherCa = InternalCertificateAuthority.CreateCa(TestDatabaseFqdn, now.AddDays(-300));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new CertificateRequest("CN=agent", key, HashAlgorithmName.SHA256).CreateSigningRequest();
        var expired = InternalCertificateAuthority.IssueAgentCertificate(ca.CertificateDer, ca.PrivateKeyPkcs8, csr, Guid.NewGuid(), Guid.NewGuid(), now.AddDays(-120));
        var valid = InternalCertificateAuthority.IssueAgentCertificate(ca.CertificateDer, ca.PrivateKeyPkcs8, csr, Guid.NewGuid(), Guid.NewGuid(), now);

        bool Validate(byte[] certificateDer, byte[] trustedCaDer)
        {
            using var certificate = X509CertificateLoader.LoadCertificate(certificateDer);
            using var trusted = X509CertificateLoader.LoadCertificate(trustedCaDer);
            using var chain = new X509Chain { ChainPolicy = CertificateChains.CreatePolicy([trusted], CertificateChains.ClientAuthOid) };
            var errors = chain.Build(certificate) ? SslPolicyErrors.None : SslPolicyErrors.RemoteCertificateChainErrors;
            return AgentTlsOptions.ValidateClientCertificate(this, certificate, chain, errors);
        }

        Assert.True(Validate(valid.CertificateDer, ca.CertificateDer));
        Assert.True(Validate(expired.CertificateDer, ca.CertificateDer));
        Assert.False(Validate(expired.CertificateDer, otherCa.CertificateDer));
        Assert.False(Validate(valid.CertificateDer, otherCa.CertificateDer));
    }

    [Fact]
    public async Task An_expired_certificate_is_accepted_for_recovery_only_within_a_year_and_never_when_revoked()
    {
        var recoverable = await ExpiredAsync(30);
        var revoked = await ExpiredAsync(30, revokedAt: Now.AddDays(-40));
        var tooOld = await ExpiredAsync(400);
        using var harness = _fixture.CreateHarness();
        try
        {
            Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));

            using (var certificate = recoverable.Credential.PublicCertificate())
            {
                Assert.Equal(AllowListDecision.Accepted, harness.AllowList.AuthorizeRecovery(certificate, out var identity));
                Assert.Equal(recoverable.Endpoint.Id, identity!.EndpointId);
                Assert.Equal(recoverable.Endpoint.ClientId, identity.ClientId);
                Assert.Equal(AllowListDecision.Refused, harness.AllowList.Authorize(certificate, out _));
            }

            foreach (var refused in new[] { revoked, tooOld })
            {
                using var certificate = refused.Credential.PublicCertificate();
                Assert.Equal(AllowListDecision.Refused, harness.AllowList.AuthorizeRecovery(certificate, out _));
            }

            // A valid certificate is not a recovery.
            var endpoint = await _fixture.CreateEndpointAsync();
            var current = await _fixture.IssueAsync(endpoint);
            Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
            using (var certificate = current.PublicCertificate())
            {
                Assert.Equal(AllowListDecision.Refused, harness.AllowList.AuthorizeRecovery(certificate, out _));
            }
        }
        finally
        {
            await RemoveCaAsync(recoverable.CaRowId);
            await RemoveCaAsync(revoked.CaRowId);
            await RemoveCaAsync(tooOld.CaRowId);
        }
    }

    [Fact]
    public async Task Recovery_needs_the_same_key_and_returns_the_signed_certificate_and_a_session_gets_the_hint()
    {
        var expired = await ExpiredAsync(30);
        await using var signer = new FakeSigner(_fixture);
        using var harness = _fixture.CreateHarness();
        try
        {
            Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
            var handler = new RecoveryHandler(harness.AllowList, harness.Signing, NullLogger<RecoveryHandler>.Instance);
            using var certificate = expired.Credential.PublicCertificate();

            using (var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var wrongKey = await PostAsync(handler, certificate, new CertificateRequest("CN=agent", stranger, HashAlgorithmName.SHA256).CreateSigningRequest());
                Assert.Equal(StatusCodes.Status400BadRequest, wrongKey.Status);
            }

            var recovered = await PostAsync(handler, certificate, expired.Credential.Csr());
            Assert.Equal(StatusCodes.Status200OK, recovered.Status);
            var response = RecoverResponse.Parser.ParseFrom(recovered.Bytes);
            using (var issued = X509CertificateLoader.LoadCertificate(response.CertificateDer.ToByteArray()))
            {
                Assert.Equal(expired.Endpoint.Id, InternalCertificateAuthority.EndpointIdFromCertificate(issued));
                Assert.Equal(expired.Credential.Issued.PublicKeyFingerprint, InternalCertificateAuthority.PublicKeyFingerprint(issued));
            }

            var endpoint = await _fixture.CreateEndpointAsync();
            var unknown = InternalCertificateAuthority.IssueAgentCertificate(_fixture.Ca.CertificateDer, _fixture.Ca.PrivateKeyPkcs8, expired.Credential.Csr(),
                endpoint.Id, _fixture.Database.InstanceId, Now);
            using (var notRecorded = X509CertificateLoader.LoadCertificate(unknown.CertificateDer))
            {
                var refused = await PostAsync(handler, notRecorded, expired.Credential.Csr());
                Assert.Equal(StatusCodes.Status401Unauthorized, refused.Status);
                Assert.Contains("Enroll the agent again", refused.Body);
            }

            // The connect path refuses the expired certificate and tells the agent to recover.
            await harness.Manager.ResetOnlineStateAsync(CancellationToken.None);
            var connect = new AgentConnectionHandler(harness.Manager, harness.AllowList, harness.Metrics, _fixture.Database.Time,
                Options.Create(harness.Options), NullLogger<AgentConnectionHandler>.Instance);
            var context = NewContext([], null, certificate);
            context.Features.Set<IHttpWebSocketFeature>(new WebSocketRequestFeature());
            await connect.HandleAsync(context);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            Assert.Equal(ProtocolLimits.CertificateExpiredValue, context.Response.Headers[ProtocolLimits.CertificateStateHeader].ToString());
        }
        finally
        {
            await RemoveCaAsync(expired.CaRowId);
        }
    }

    private static async Task<(int Status, string Body, byte[] Bytes)> PostAsync(RecoveryHandler handler, X509Certificate2 certificate, byte[] csr)
    {
        var body = new RecoverRequest { CsrDer = ByteString.CopyFrom(csr), Hostname = "WS-DRAWER", AgentVersion = "0.2.0" }.ToByteArray();
        var context = NewContext(body, ProtocolLimits.ProtobufContentType, certificate);
        await handler.HandleAsync(context);
        var bytes = ((MemoryStream)context.Response.Body).ToArray();
        return (context.Response.StatusCode, Encoding.UTF8.GetString(bytes), bytes);
    }

    private static DefaultHttpContext NewContext(byte[] body, string? contentType, X509Certificate2 certificate)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().BuildServiceProvider() };
        context.Request.Method = "POST";
        context.Request.ContentType = contentType;
        context.Request.ContentLength = body.Length;
        context.Request.Body = new MemoryStream(body);
        context.Response.Body = new MemoryStream();
        context.Connection.RemoteIpAddress = IPAddress.Parse("198.51.100.30");
        context.Connection.ClientCertificate = certificate;
        return context;
    }

    private sealed class WebSocketRequestFeature : IHttpWebSocketFeature
    {
        public bool IsWebSocketRequest => true;

        public Task<System.Net.WebSockets.WebSocket> AcceptAsync(WebSocketAcceptContext context) =>
            throw new InvalidOperationException("The expired certificate must be refused before the upgrade.");
    }
}
