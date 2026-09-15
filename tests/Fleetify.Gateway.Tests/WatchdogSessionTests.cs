using System.Security.Cryptography;
using System.Text;
using Fleetify.Core.Entities;
using Fleetify.Gateway.Releases;
using Fleetify.Gateway.Tls;
using Fleetify.Infrastructure.Security;
using Fleetify.Protocol.Agent.V1;
using Google.Protobuf;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Endpoint = Fleetify.Core.Entities.Endpoint;

namespace Fleetify.Gateway.Tests;

/// <summary>
/// Guarantees of the watchdog and agent updates in the gateway (0.2.1): a watchdog certificate opens only a watchdog session and an agent
/// certificate only an agent session; both run side by side; a watchdog session marks the watchdog online, stores the agent service state it
/// reports and never handles agent data; the agent gets a watchdog certificate only for another key; a release is offered only with a valid
/// release signature, following the update ring, pause and release-to-all; binaries are served only to valid certificates and only when
/// they are part of the verified release.
/// </summary>
[Collection(GatewayCollection.Name)]
public sealed class WatchdogSessionTests
{
    private readonly GatewayFixture _fixture;

    public WatchdogSessionTests(GatewayFixture fixture)
    {
        _fixture = fixture;
    }

    private static Hello WatchdogHello() => new() { AgentVersion = "0.2.1", Hostname = "WS-TEST", Component = Component.Watchdog };

    private async Task<Endpoint> ReadEndpointAsync(Guid id)
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == id);
    }

    [Fact]
    public async Task A_watchdog_session_runs_next_to_the_agent_and_never_handles_agent_data()
    {
        await _fixture.Database.LoadTestLicenseAsync(1000);
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync(EndpointTier.Managed);
        var agentSession = await harness.OpenAsync(endpoint);

        var watchdog = harness.NewSession(new AgentIdentity(endpoint.Id, Guid.NewGuid().ToString("N"), "watchdog-key", DateTime.UtcNow.AddDays(30),
            AgentComponent.Watchdog));
        Assert.True(await harness.Manager.OpenAsync(watchdog, WatchdogHello(), CancellationToken.None));
        Assert.False(agentSession.IsClosing);
        Assert.True(harness.Manager.TryGetWatchdogSession(endpoint.Id, out _));

        var stored = await ReadEndpointAsync(endpoint.Id);
        Assert.True(stored.WatchdogOnline);
        Assert.True(stored.IsOnline);
        Assert.Equal("0.2.1", stored.WatchdogVersion);

        // Agent data from a watchdog certificate is ignored: no acknowledgement, nothing stored.
        GatewayHarness.Drain(watchdog);
        await harness.Manager.HandleAsync(watchdog, new AgentMessage
        {
            CheckResults = new CheckResultBatch { Sequence = 1, Results = { new Fleetify.Protocol.Agent.V1.CheckResult { CheckId = Guid.NewGuid().ToString(), Value = 1 } } }
        }, CancellationToken.None);
        Assert.DoesNotContain(GatewayHarness.Drain(watchdog), m => m.BodyCase == ServerMessage.BodyOneofCase.BatchAck);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.False(await db.IngestBatches.AnyAsync(b => b.EndpointId == endpoint.Id));
        }

        await harness.Manager.HandleAsync(watchdog, new AgentMessage
        {
            Heartbeat = new Heartbeat { Peer = new PeerStatus { Version = "0.2.0", State = ServiceState.Stopped, Detail = "Access is denied." } }
        }, CancellationToken.None);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var reported = await db.EndpointComponentStates.AsNoTracking().SingleAsync(c => c.EndpointId == endpoint.Id && c.Component == AgentComponent.Agent);
            Assert.Equal(ComponentServiceState.Stopped, reported.ServiceState);
            Assert.Equal("Access is denied.", reported.ServiceDetail);
            Assert.Equal("0.2.0", reported.InstalledVersion);
        }

        await harness.Manager.HandleAsync(watchdog, new AgentMessage
        {
            UpdateStatus = new UpdateStatus { Component = Component.Agent, Version = "0.2.1", State = UpdateState.RolledBack, Detail = "did not connect" }
        }, CancellationToken.None);
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var reported = await db.EndpointComponentStates.AsNoTracking().SingleAsync(c => c.EndpointId == endpoint.Id && c.Component == AgentComponent.Agent);
            Assert.Equal(ComponentUpdateState.RolledBack, reported.UpdateState);
            Assert.Equal("0.2.1", reported.UpdateVersion);
        }

        watchdog.Abort("test");
        await harness.Manager.CloseAsync(watchdog);
        stored = await ReadEndpointAsync(endpoint.Id);
        Assert.False(stored.WatchdogOnline);
        Assert.True(stored.IsOnline);
    }

    [Fact]
    public async Task A_certificate_opens_only_the_session_of_its_own_role()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();

        var watchdogAsAgent = harness.NewSession(new AgentIdentity(endpoint.Id, Guid.NewGuid().ToString("N"), "k", DateTime.UtcNow.AddDays(30), AgentComponent.Watchdog));
        Assert.False(await harness.Manager.OpenAsync(watchdogAsAgent, GatewayHarness.Hello(), CancellationToken.None));
        Assert.True(watchdogAsAgent.IsClosing);

        var agentAsWatchdog = harness.NewSession(new AgentIdentity(endpoint.Id, Guid.NewGuid().ToString("N"), "k", DateTime.UtcNow.AddDays(30)));
        Assert.False(await harness.Manager.OpenAsync(agentAsWatchdog, WatchdogHello(), CancellationToken.None));
        Assert.True(agentAsWatchdog.IsClosing);

        Assert.False((await ReadEndpointAsync(endpoint.Id)).WatchdogOnline);
    }

    [Fact]
    public async Task The_agent_gets_a_watchdog_certificate_only_for_a_key_of_its_own()
    {
        using var harness = _fixture.CreateHarness();
        await using var signer = new FakeSigner(_fixture);
        var endpoint = await _fixture.CreateEndpointAsync();
        var agent = await _fixture.IssueAsync(endpoint);
        var session = harness.NewSession(agent.Identity(endpoint.Id));
        Assert.True(await harness.Manager.OpenAsync(session, GatewayHarness.Hello(), CancellationToken.None));

        var refused = await harness.Manager.IssueWatchdogCertificateAsync(session, agent.Csr(), CancellationToken.None);
        Assert.NotEmpty(refused.WatchdogCertificate.Error);

        using var watchdogKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var csr = new System.Security.Cryptography.X509Certificates.CertificateRequest("CN=watchdog", watchdogKey, HashAlgorithmName.SHA256).CreateSigningRequest();
        var issued = await harness.Manager.IssueWatchdogCertificateAsync(session, csr, CancellationToken.None);
        Assert.Empty(issued.WatchdogCertificate.Error);

        using var certificate = System.Security.Cryptography.X509Certificates.X509CertificateLoader.LoadCertificate(issued.WatchdogCertificate.CertificateDer.ToByteArray());
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
        Assert.Equal(AllowListDecision.Accepted, harness.AllowList.Authorize(certificate, out var identity));
        Assert.Equal(AgentComponent.Watchdog, identity!.Role);

        // A watchdog session may not ask for a watchdog certificate itself.
        var watchdog = harness.NewSession(new AgentIdentity(endpoint.Id, identity.Fingerprint, identity.PublicKeyFingerprint, identity.ExpiresAt, AgentComponent.Watchdog));
        Assert.True(await harness.Manager.OpenAsync(watchdog, WatchdogHello(), CancellationToken.None));
        Assert.NotEmpty((await harness.Manager.IssueWatchdogCertificateAsync(watchdog, csr, CancellationToken.None)).WatchdogCertificate.Error);
    }

    [Fact]
    public async Task A_release_is_offered_only_with_a_valid_signature_and_follows_the_ring_and_the_controls()
    {
        using var harness = _fixture.CreateHarness();
        var endpoint = await _fixture.CreateEndpointAsync();
        var version = "9.9." + RandomNumberGenerator.GetInt32(1000, 99999);
        var binary = RandomNumberGenerator.GetBytes(4096);
        Directory.CreateDirectory(Path.Combine(harness.BinariesDirectory, "windows-amd64"));
        await File.WriteAllBytesAsync(Path.Combine(harness.BinariesDirectory, "windows-amd64", "fleetify-agent.exe"), binary);
        await File.WriteAllBytesAsync(Path.Combine(harness.BinariesDirectory, "windows-amd64", "fleetify-watchdog.exe"), [1, 2, 3]);
        var manifest = Encoding.UTF8.GetBytes($$"""
            {"formatVersion":1,"version":"{{version}}","agentBinaries":[
              {"component":"agent","platform":"windows","architecture":"amd64","file":"windows-amd64/fleetify-agent.exe","sha256":"{{Convert.ToHexStringLower(SHA256.HashData(binary))}}","size":{{binary.Length}}},
              {"component":"watchdog","platform":"windows","architecture":"amd64","file":"windows-amd64/fleetify-watchdog.exe","sha256":"{{new string('0', 64)}}","size":3}]}
            """);
        await File.WriteAllBytesAsync(Path.Combine(harness.ReleaseDirectory, ReleaseCatalog.ManifestFileName), manifest);

        // A signature from another key: nothing is loaded, nothing is offered.
        var (otherPrivate, _) = Ed25519.GenerateKeyPair();
        await File.WriteAllBytesAsync(Path.Combine(harness.ReleaseDirectory, ReleaseCatalog.ManifestFileName + ".sig"), Ed25519.SignRaw(otherPrivate, manifest));
        Assert.False(await harness.Releases.LoadAsync(CancellationToken.None));
        Assert.Null(harness.Releases.Current);

        await File.WriteAllBytesAsync(Path.Combine(harness.ReleaseDirectory, ReleaseCatalog.ManifestFileName + ".sig"), Ed25519.SignRaw(GatewayHarness.ReleaseKey.Private, manifest));
        Assert.True(await harness.Releases.LoadAsync(CancellationToken.None));
        var release = harness.Releases.Current!;
        Assert.Equal(version, release.Version);
        Assert.True(release.Binaries.ContainsKey("windows-amd64/fleetify-agent.exe"));
        Assert.False(release.Binaries.ContainsKey("windows-amd64/fleetify-watchdog.exe"));
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.True((await db.AgentReleases.AsNoTracking().SingleAsync(r => r.Version == version)).IsCurrent);
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == "agent_release.installed" && a.TargetId == version));
        }

        // The default policy is on the Standard ring: the release was just installed, so it is offered but not allowed yet.
        var session = await harness.OpenAsync(endpoint);
        var offer = (await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.UpdateOffer)).UpdateOffer;
        Assert.False(offer.UpdateAllowed);
        Assert.Equal(manifest, offer.Manifest.ToByteArray());
        Assert.True(Ed25519.VerifyRaw(GatewayHarness.ReleaseKey.Public, offer.Manifest.ToByteArray(), offer.Signature.ToByteArray()));

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.AgentReleases.Where(r => r.Version == version).ExecuteUpdateAsync(s => s.SetProperty(r => r.ReleasedToAllAt, _fixture.Database.Time.GetUtcNow().UtcDateTime));
        }

        await harness.Releases.RefreshControlAsync(CancellationToken.None);
        Assert.True((await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.UpdateOffer)).UpdateOffer.UpdateAllowed);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.AgentReleases.Where(r => r.Version == version).ExecuteUpdateAsync(s => s.SetProperty(r => r.PausedAt, _fixture.Database.Time.GetUtcNow().UtcDateTime));
        }

        await harness.Releases.RefreshControlAsync(CancellationToken.None);
        Assert.False((await GatewayHarness.ReadUntilAsync(session, ServerMessage.BodyOneofCase.UpdateOffer)).UpdateOffer.UpdateAllowed);

        // Downloads: only with a valid certificate, only files of the verified release.
        var credential = await _fixture.IssueAsync(endpoint);
        Assert.True(await harness.AllowList.ReloadAsync(CancellationToken.None));
        var handler = new ReleaseDownloadHandler(harness.Releases, harness.AllowList, _fixture.Database.Time, Options.Create(harness.Options),
            NullLogger<ReleaseDownloadHandler>.Instance);

        Assert.Equal(StatusCodes.Status401Unauthorized, await DownloadAsync(handler, null, version, "windows-amd64/fleetify-agent.exe"));
        using var certificate = credential.PublicCertificate();
        Assert.Equal(StatusCodes.Status404NotFound, await DownloadAsync(handler, certificate, version, "windows-amd64/fleetify-watchdog.exe"));
        Assert.Equal(StatusCodes.Status404NotFound, await DownloadAsync(handler, certificate, "0.0.1", "windows-amd64/fleetify-agent.exe"));
        Assert.Equal(StatusCodes.Status404NotFound, await DownloadAsync(handler, certificate, version, "../manifest.json"));
        Assert.Equal(StatusCodes.Status200OK, await DownloadAsync(handler, certificate, version, "windows-amd64/fleetify-agent.exe"));
    }

    private static async Task<int> DownloadAsync(ReleaseDownloadHandler handler, System.Security.Cryptography.X509Certificates.X509Certificate2? certificate,
        string version, string file)
    {
        var context = new DefaultHttpContext();
        context.Features.Set<ITlsConnectionFeature>(new TlsConnectionFeature { ClientCertificate = certificate });
        context.Features.Set<IHttpResponseBodyFeature>(new StreamResponseBodyFeature(new MemoryStream()));
        await handler.HandleAsync(context, version, file);
        return context.Response.StatusCode;
    }
}
