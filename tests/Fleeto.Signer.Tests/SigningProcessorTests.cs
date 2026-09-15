using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Fleeto.Core.Entities;
using Fleeto.Signer.Handlers;
using Fleeto.Signer.Keys;
using Fleeto.Signer.Processing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees that concurrent signer processes sign each request once, that rate limits leave requests pending or
/// refuse stale enrollments, and that the gateway certificate is only issued to the gateway for the agent host name.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class SigningProcessorTests
{
    private readonly SignerFixture _fixture;

    public SigningProcessorTests(SignerFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Two_signers_processing_the_same_enrollment_sign_it_once()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync(maxUses: null);
        using var secondRing = new SignerKeyRing();
        await _fixture.CreateBootstrapper(_fixture.Database.SignerKey, secondRing).RunAsync();
        var first = _fixture.CreateProcessor();
        var second = _fixture.CreateProcessor(keyRing: secondRing);

        for (var round = 0; round < 5; round++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = await _fixture.InsertRequestAsync(SigningRequestKind.AgentEnrollment, site.ClientId, null,
                SignerFixture.EnrollPayload(token, SignerFixture.Csr(key), $"WS-RACE-{round}"), "gateway");

            var results = await Task.WhenAll(
                Task.Run(() => first.ProcessRequestAsync(request.Id)),
                Task.Run(() => second.ProcessRequestAsync(request.Id)));

            Assert.Single(results, r => r.Status == ProcessStatus.Completed);
            Assert.Single(results, r => r.Status == ProcessStatus.NothingClaimed);
        }

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(5, await db.Endpoints.CountAsync(e => e.SiteId == site.Id));
        Assert.Equal(5, await db.AgentCertificates.CountAsync(c => c.ClientId == site.ClientId));
        Assert.Equal(5, await db.EnrollmentTokens.Where(t => t.Id == tokenRow.Id).Select(t => t.UseCount).SingleAsync());
    }

    [Fact]
    public async Task Two_signers_draining_a_backlog_together_sign_every_request_once()
    {
        var (site, token, tokenRow) = await _fixture.CreateSiteWithTokenAsync(maxUses: null);
        var ids = new List<Guid>();
        for (var i = 0; i < 12; i++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = await _fixture.InsertRequestAsync(SigningRequestKind.AgentEnrollment, site.ClientId, null,
                SignerFixture.EnrollPayload(token, SignerFixture.Csr(key), $"WS-BACKLOG-{i}"), "gateway");
            ids.Add(request.Id);
        }

        var first = _fixture.CreateProcessor();
        var second = _fixture.CreateProcessor();
        await Task.WhenAll(Task.Run(() => first.ProcessBatchAsync()), Task.Run(() => second.ProcessBatchAsync()));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var states = await db.SigningRequests.Where(r => ids.Contains(r.Id)).Select(r => r.State).ToListAsync();
        Assert.All(states, s => Assert.Equal(SigningRequestState.Completed, s));
        Assert.Equal(12, await db.Endpoints.CountAsync(e => e.SiteId == site.Id));
        Assert.Equal(12, await db.EnrollmentTokens.Where(t => t.Id == tokenRow.Id).Select(t => t.UseCount).SingleAsync());
    }

    [Fact]
    public async Task Rate_limited_configuration_request_stays_pending_until_capacity_returns()
    {
        var agent = await _fixture.EnrollAsync("WS-RATE-CONFIG");
        var limited = _fixture.CreateProcessor(new SigningRateLimiter(new Dictionary<SigningRequestKind, int> { [SigningRequestKind.AgentConfig] = 0 }));
        var request = await _fixture.InsertRequestAsync(SigningRequestKind.AgentConfig, agent.ClientId, agent.EndpointId, [], "workers");

        var result = await limited.ProcessRequestAsync(request.Id);

        Assert.Equal(ProcessStatus.RateLimited, result.Status);
        Assert.Equal(SigningRequestState.Pending, (await _fixture.LoadRequestAsync(request.Id)).State);

        Assert.Equal(ProcessStatus.Completed, (await _fixture.CreateProcessor().ProcessRequestAsync(request.Id)).Status);
    }

    [Fact]
    public async Task Rate_limited_enrollment_older_than_a_minute_is_refused()
    {
        var (site, token, _) = await _fixture.CreateSiteWithTokenAsync();
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var limits = new Dictionary<SigningRequestKind, int> { [SigningRequestKind.AgentEnrollment] = 0 };
        var limited = _fixture.CreateProcessor(new SigningRateLimiter(limits));
        var payload = SignerFixture.EnrollPayload(token, SignerFixture.Csr(key), "WS-RATE-ENROLL");

        var fresh = await _fixture.InsertRequestAsync(SigningRequestKind.AgentEnrollment, site.ClientId, null, payload, "gateway");
        Assert.Equal(ProcessStatus.RateLimited, (await limited.ProcessRequestAsync(fresh.Id)).Status);

        var stale = await _fixture.InsertRequestAsync(SigningRequestKind.AgentEnrollment, site.ClientId, null, payload, "gateway",
            _fixture.Now.AddSeconds(-90));
        Assert.Equal(ProcessStatus.Refused, (await limited.ProcessRequestAsync(stale.Id)).Status);
        Assert.Equal(SigningRequestProcessor.TooManyRequestsReason, (await _fixture.LoadRequestAsync(stale.Id)).RefusalReason);

        // Leave nothing pending for other tests.
        Assert.Equal(ProcessStatus.Completed, (await _fixture.CreateProcessor().ProcessRequestAsync(fresh.Id)).Status);
    }

    [Fact]
    public void Rate_limiter_allows_the_limit_per_minute_and_frees_slots_as_the_window_slides()
    {
        var limiter = new SigningRateLimiter(new Dictionary<SigningRequestKind, int> { [SigningRequestKind.GatewayCertificate] = 2 });
        var start = new DateTime(2026, 9, 14, 12, 0, 0, DateTimeKind.Utc);

        Assert.True(limiter.TryAcquire(SigningRequestKind.GatewayCertificate, start));
        Assert.True(limiter.TryAcquire(SigningRequestKind.GatewayCertificate, start.AddSeconds(30)));
        Assert.False(limiter.TryAcquire(SigningRequestKind.GatewayCertificate, start.AddSeconds(59)));
        Assert.True(limiter.TryAcquire(SigningRequestKind.GatewayCertificate, start.AddSeconds(60)));
        Assert.False(limiter.TryAcquire(SigningRequestKind.AgentConfig, start));
    }

    [Fact]
    public async Task Gateway_certificate_is_issued_for_the_agent_host_name_with_a_24_hour_lifetime()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = await _fixture.ProcessAsync(SigningRequestKind.GatewayCertificate, null, null, SignerFixture.Csr(key), "gateway");

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.NotNull(request.Result);
        Assert.True(_fixture.ChainsToCa(request.Result, _fixture.KeyRing.CaCertificateDer));
        using var certificate = X509CertificateLoader.LoadCertificate(request.Result);
        var san = certificate.Extensions.OfType<X509SubjectAlternativeNameExtension>().Single();
        Assert.Equal(["agents.rmm.test.example"], san.EnumerateDnsNames());
        Assert.True(certificate.NotAfter.ToUniversalTime() - certificate.NotBefore.ToUniversalTime() <= TimeSpan.FromHours(24).Add(TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public async Task Gateway_certificate_requested_by_another_component_is_refused()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var request = await _fixture.ProcessAsync(SigningRequestKind.GatewayCertificate, null, null, SignerFixture.Csr(key), "workers");

        Assert.Equal(SigningRequestState.Refused, request.State);
        Assert.Equal(GatewayCertificateHandler.WrongRequesterReason, request.RefusalReason);
        Assert.Null(request.Result);
    }

    [Fact]
    public async Task Tests_run_the_signer_with_the_least_privilege_signer_role()
    {
        await using var db = _fixture.SignerDbFactory.CreateSystem();

        // Data keys are wrapped by the root key the signer must never use; audit entries are write-only for it.
        var dataKeys = await Assert.ThrowsAsync<PostgresException>(() => db.DataKeys.CountAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, dataKeys.SqlState);
        var audit = await Assert.ThrowsAsync<PostgresException>(() => db.AuditEntries.CountAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, audit.SqlState);
    }

    [Fact]
    public void Gateway_remote_address_is_taken_from_requested_by_only_when_it_is_an_ip_address()
    {
        Assert.Equal("203.0.113.7", SigningRequestProcessor.IpAddressFrom("gateway:203.0.113.7"));
        Assert.Equal("2001:db8::1", SigningRequestProcessor.IpAddressFrom("gateway:2001:db8::1"));
        Assert.Null(SigningRequestProcessor.IpAddressFrom("gateway:<script>"));
        Assert.Null(SigningRequestProcessor.IpAddressFrom("workers"));
    }
}
