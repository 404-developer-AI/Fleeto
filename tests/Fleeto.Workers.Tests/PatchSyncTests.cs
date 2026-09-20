using System.Net;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees of the patch sync (0.4.0 step 2): state is stored for the endpoint it belongs to and for no other, only
/// managed endpoints get it, detail is read for endpoints that miss something, an endpoint the product does not patch or
/// has not seen opens an alert, maintenance keeps quiet, and an endpoint the product forgets loses its state.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PatchSyncTests
{
    private const string Tenant = "org-patch";
    private readonly WorkersFixture _fixture;

    public PatchSyncTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Client Client, Integration Integration)> SeedAsync(string code)
    {
        // Patching is a managed feature, so the instance needs a license that allows the managed tier.
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var client = await _fixture.Db.CreateClientAsync(code);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
        await db.SaveChangesAsync();

        var integration = new Integration
        {
            Id = Guid.NewGuid(),
            Type = IntegrationType.Action1,
            Region = Action1Region.Europe,
            CredentialName = "api-key-patch@action1.com",
            Enabled = true,
            CreatedAt = _fixture.Now,
            UpdatedAt = _fixture.Now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Db.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "patch-secret"));
        db.Integrations.Add(integration);
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(),
            IntegrationId = integration.Id,
            ClientId = client.Id,
            ExternalTenantId = Tenant,
            ExternalTenantName = "Patch org",
            CreatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
        return (client, integration);
    }

    /// <summary>An endpoint with an inventory that reports the Action1 agent id.</summary>
    private async Task<Endpoint> CreateEndpointAsync(Client client, string action1Id, EndpointTier tier = EndpointTier.Managed,
        string hostname = "PATCH-01")
    {
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Site " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, tier, hostname);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.InventorySnapshots.Add(new InventorySnapshot
        {
            EndpointId = endpoint.Id,
            ClientId = client.Id,
            ReceivedAt = _fixture.Now,
            Hash = Guid.NewGuid().ToString("N"),
            Action1AgentId = action1Id
        });
        await db.SaveChangesAsync();
        return endpoint;
    }

    private PatchSyncService Service(StubHandler handler) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus,
            new Action1ClientFactory(_fixture.Db.SecretProtector, _fixture.Db.Time, NullLoggerFactory.Instance,
                IntegrationBudgets.WorkerRequestsPerMinute, () => handler),
            new AlertNotificationService(_fixture.Db.Time), _fixture.Db.Licenses, _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<PatchSyncService>.Instance);

    [Fact]
    public async Task The_counts_of_an_endpoint_are_stored_and_its_missing_updates_are_read()
    {
        var (client, _) = await SeedAsync("PS1" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, action1Id);
        var handler = new StubHandler { Endpoints = [Reported(action1Id, critical: 2, other: 3)] };

        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var state = await db.EndpointPatchStates.AsNoTracking().SingleAsync(p => p.EndpointId == endpoint.Id);
        Assert.Equal(2, state.MissingCritical);
        Assert.Equal(3, state.MissingOther);
        Assert.False(state.IsCompliant);
        Assert.Equal(Tenant, state.ExternalTenantId);
        Assert.Equal(PatchCoverage.Active, state.Coverage);

        var updates = await db.EndpointMissingUpdates.AsNoTracking().Where(u => u.EndpointId == endpoint.Id).ToListAsync();
        Assert.Equal(2, updates.Count);
        Assert.Contains(updates, u => u.Severity == PatchSeverity.Critical && u.KbNumber == "KB5034123");
        Assert.Contains(updates, u => u.Name == "Google Chrome" && u.Severity == PatchSeverity.Important);
    }

    [Fact]
    public async Task An_endpoint_that_misses_nothing_keeps_no_update_rows_and_costs_no_extra_call()
    {
        var (client, _) = await SeedAsync("PS2" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, action1Id);
        var handler = new StubHandler { Endpoints = [Reported(action1Id, critical: 0, other: 0)] };

        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        Assert.Equal(0, handler.DetailCalls);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.True((await db.EndpointPatchStates.AsNoTracking().SingleAsync(p => p.EndpointId == endpoint.Id)).IsCompliant);
        Assert.Empty(await db.EndpointMissingUpdates.AsNoTracking().Where(u => u.EndpointId == endpoint.Id).ToListAsync());
    }

    [Fact]
    public async Task Patch_state_never_lands_on_an_endpoint_of_another_client()
    {
        var (client, _) = await SeedAsync("PS3" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        // Another client whose endpoint reports the same Action1 id: only the mapped client may receive the state.
        var other = await _fixture.Db.CreateClientAsync("OTH" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var strangerEndpoint = await CreateEndpointAsync(other, action1Id, hostname: "STRANGER");
        var handler = new StubHandler { Endpoints = [Reported(action1Id, critical: 1, other: 0)] };

        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Empty(await db.EndpointPatchStates.AsNoTracking().Where(p => p.EndpointId == strangerEndpoint.Id).ToListAsync());
        Assert.Empty(await db.EndpointPatchStates.AsNoTracking().Where(p => p.ClientId == other.Id).ToListAsync());
        Assert.Empty(await db.EndpointPatchStates.AsNoTracking().Where(p => p.ClientId == client.Id).ToListAsync());
    }

    [Fact]
    public async Task An_agent_only_endpoint_gets_no_patch_state()
    {
        var (client, _) = await SeedAsync("PS4" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, action1Id, EndpointTier.AgentOnly);
        var handler = new StubHandler { Endpoints = [Reported(action1Id, critical: 5, other: 0)] };

        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Empty(await db.EndpointPatchStates.AsNoTracking().Where(p => p.EndpointId == endpoint.Id).ToListAsync());
    }

    [Fact]
    public async Task An_endpoint_the_product_no_longer_patches_opens_an_alert_that_resolves_when_it_does_again()
    {
        var (client, _) = await SeedAsync("PS5" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, action1Id, hostname: "INACTIVE-01");
        var handler = new StubHandler { Endpoints = [Reported(action1Id, critical: 0, other: 0, active: false)] };

        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var alert = await db.Alerts.AsNoTracking()
                .SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState);
            Assert.Equal(AlertState.Open, alert.State);
            Assert.Equal("coverage", alert.Target);
            Assert.Contains("not patched by Action1", alert.Title);
            Assert.Contains("INACTIVE-01", alert.Title);
        }

        handler.Endpoints = [Reported(action1Id, critical: 0, other: 0)];
        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        await using var read = _fixture.Db.DbFactory.CreateSystem();
        var resolved = await read.Alerts.AsNoTracking().SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState);
        Assert.Equal(AlertState.Resolved, resolved.State);
        Assert.Equal("Action1 patches this endpoint again.", resolved.ResolvedReason);
    }

    [Fact]
    public async Task A_state_the_product_has_not_refreshed_for_a_week_opens_an_alert()
    {
        var (client, _) = await SeedAsync("PS6" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, action1Id, hostname: "STALE-01");
        var handler = new StubHandler
        {
            Endpoints = [Reported(action1Id, critical: 0, other: 0, lastSeen: _fixture.Now - TimeSpan.FromDays(9))]
        };

        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState);
        Assert.Equal("stale", alert.Target);
        Assert.Contains("has not seen STALE-01", alert.Title);
    }

    [Fact]
    public async Task An_endpoint_in_maintenance_gets_its_state_but_no_alert()
    {
        var (client, _) = await SeedAsync("PS7" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, action1Id, hostname: "QUIET-01");
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var tracked = await db.Endpoints.SingleAsync(e => e.Id == endpoint.Id);
            tracked.MaintenanceStartedAt = _fixture.Now - TimeSpan.FromHours(1);
            tracked.MaintenanceEndsAt = _fixture.Now + TimeSpan.FromHours(1);
            await db.SaveChangesAsync();
        }

        var handler = new StubHandler { Endpoints = [Reported(action1Id, critical: 1, other: 0, active: false)] };
        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.NotNull(await read.EndpointPatchStates.AsNoTracking().SingleOrDefaultAsync(p => p.EndpointId == endpoint.Id));
        Assert.Empty(await read.Alerts.AsNoTracking().Where(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState).ToListAsync());
    }

    [Fact]
    public async Task Nothing_is_read_and_open_alerts_are_resolved_when_the_license_no_longer_allows_the_managed_tier()
    {
        var (client, _) = await SeedAsync("PS9" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, action1Id, hostname: "EXPIRED-01");
        var handler = new StubHandler { Endpoints = [Reported(action1Id, critical: 0, other: 0, active: false)] };
        await Service(handler).SyncAsync(force: true, CancellationToken.None);
        await using (var check = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(AlertState.Open,
                (await check.Alerts.AsNoTracking().SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState)).State);
        }

        // Past the grace period every endpoint behaves as agent-only.
        await _fixture.Db.LoadTestLicenseAsync(1000, expiresAt: _fixture.Now.AddDays(-20));
        var second = new StubHandler { Endpoints = [Reported(action1Id, critical: 9, other: 9)] };
        await Service(second).SyncAsync(force: true, CancellationToken.None);

        Assert.Equal(0, second.DetailCalls);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState);
        Assert.Equal(AlertState.Resolved, alert.State);
        var state = await db.EndpointPatchStates.AsNoTracking().SingleAsync(p => p.EndpointId == endpoint.Id);
        Assert.Equal(0, state.MissingCritical);
    }

    [Fact]
    public async Task An_endpoint_the_product_forgets_loses_its_state_instead_of_keeping_an_old_one()
    {
        var (client, _) = await SeedAsync("PS8" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, action1Id);
        var handler = new StubHandler { Endpoints = [Reported(action1Id, critical: 1, other: 1)] };
        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        handler.Endpoints = [];
        await Service(handler).SyncAsync(force: true, CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Empty(await db.EndpointPatchStates.AsNoTracking().Where(p => p.EndpointId == endpoint.Id).ToListAsync());
        Assert.Empty(await db.EndpointMissingUpdates.AsNoTracking().Where(u => u.EndpointId == endpoint.Id).ToListAsync());
    }

    private ReportedEndpoint Reported(string id, int critical, int other, bool active = true, DateTime? lastSeen = null) =>
        new(id, critical, other, active, lastSeen ?? _fixture.Now - TimeSpan.FromMinutes(5));

    private sealed record ReportedEndpoint(string Id, int Critical, int Other, bool Active, DateTime LastSeen);

    /// <summary>Answers like Action1 does for the endpoint listing and the missing updates of one endpoint.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public IReadOnlyList<ReportedEndpoint> Endpoints { get; set; } = [];
        public int DetailCalls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                return Task.FromResult(Json("""{"access_token":"token","expires_in":3600,"token_type":"Bearer"}"""));
            }

            if (path.EndsWith("/missing-updates", StringComparison.Ordinal))
            {
                DetailCalls++;
                return Task.FromResult(Json("""
                    {"items":[
                      {"id":"upd-1","name":"2026-09 Cumulative Update","vendor":"Microsoft","version":"10.0.20348.2700",
                       "kb_number":"KB5034123","security_severity":"Critical","reboot_needed":"Possibly"},
                      {"id":"upd-2","name":"Google Chrome","vendor":"Google","version":"126.0.6478.115",
                       "security_severity":"Important","reboot_needed":"No"}
                    ],"total_items":2}
                    """));
            }

            var items = Endpoints.Select(e => $$$"""
                {"id":"{{{e.Id}}}","organization_id":"{{{Tenant}}}","name":"reported","device_name":"reported",
                 "platform":"Windows","agent_version":"2.0.33","last_seen":"{{{e.LastSeen:yyyy-MM-dd_HH-mm-ss}}}",
                 "subscription_status":"{{{(e.Active ? "Active" : "Inactive")}}}","reboot_required":false,
                 "missing_updates":{"critical":{{{e.Critical}}},"other":{{{e.Other}}}}}
                """);
            return Task.FromResult(Json($$"""{"items":[{{string.Join(",", items)}}],"total_items":{{Endpoints.Count}}}"""));
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
