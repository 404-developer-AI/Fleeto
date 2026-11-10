using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Alerts;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Moving an Action1 endpoint to the organization of another client while Action1 follows the clients (0.6.0). The Action1
/// id an agent reports is only its word: a managed endpoint of client A that reports the id of an endpoint Action1 holds in
/// client B's organization never pulls it into A's organization by itself. The move is queued waiting for an admin, with
/// the cause, and the follow pass judges it again before it asks Action1. An endpoint enrolled again under another client,
/// whose old record is offline and was last seen before the new enrollment, still moves.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class Action1EndpointMoveTests
{
    private const string TenantA = "org-move-a", TenantB = "org-move-b";
    private readonly WorkersFixture _fixture;

    public Action1EndpointMoveTests(WorkersFixture fixture) => _fixture = fixture;

    private DateTime Now => _fixture.Now;

    private sealed record Setup(Client ClientA, Client ClientB, Integration Integration, Enterprise Action1, string Action1Id);

    private async Task<Setup> SeedAsync()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var clientA = await _fixture.Db.CreateClientAsync();
        var clientB = await _fixture.Db.CreateClientAsync();
        var integration = new Integration
        {
            Id = Guid.NewGuid(), Type = IntegrationType.Action1, Region = Action1Region.Europe, CredentialName = "move@action1.com",
            Enabled = true, FollowClients = true, CreatedAt = Now, UpdatedAt = Now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Db.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "move-secret"));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
            await db.SaveChangesAsync();
            db.Integrations.Add(integration);
            foreach (var (client, tenant) in new[] { (clientA, TenantA), (clientB, TenantB) })
            {
                db.IntegrationMappings.Add(new IntegrationMapping
                {
                    Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = client.Id, ExternalTenantId = tenant,
                    ExternalTenantName = tenant, SyncedName = Action1Client.OrganizationName(client.Code, client.Name), CreatedAt = Now
                });
            }

            await db.SaveChangesAsync();
        }

        var x = Guid.NewGuid().ToString();
        var action1 = new Enterprise();
        action1.Organizations[TenantA] = "A";
        action1.Organizations[TenantB] = "B";
        action1.Endpoints[x] = TenantB;
        return new Setup(clientA, clientB, integration, action1, x);
    }

    /// <summary>A managed endpoint that reported <paramref name="action1Id"/>, with its connection state.</summary>
    private async Task<Endpoint> ReporterAsync(Client client, string hostname, string action1Id, DateTime reportedAt, bool online,
        DateTime? lastSeen, DateTime enrolled)
    {
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Site " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, hostname);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var row = await db.Endpoints.SingleAsync(e => e.Id == endpoint.Id);
        row.IsOnline = online;
        row.LastSeenAt = lastSeen;
        row.EnrolledAt = enrolled;
        db.InventorySnapshots.Add(new InventorySnapshot
        {
            EndpointId = endpoint.Id, ClientId = client.Id, ReceivedAt = reportedAt, Hash = Guid.NewGuid().ToString("N"), Action1AgentId = action1Id
        });
        await db.SaveChangesAsync();
        return endpoint;
    }

    private Action1ClientFactory Factory(Enterprise action1) =>
        new(_fixture.Db.SecretProtector, _fixture.Db.Time, NullLoggerFactory.Instance, IntegrationBudgets.WorkerRequestsPerMinute, () => action1);

    private PatchSyncService Sync(Enterprise action1) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus, Factory(action1), new AlertNotificationService(_fixture.Db.Time), _fixture.Db.Licenses,
            _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<PatchSyncService>.Instance);

    private IntegrationFollowService Follow(Enterprise action1) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus, Factory(action1), _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<IntegrationFollowService>.Instance);

    private async Task<IntegrationOperation> MoveAsync(Integration integration)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.IntegrationOperations.AsNoTracking()
            .SingleAsync(o => o.IntegrationId == integration.Id && o.Kind == IntegrationOperationKind.MoveEndpoint);
    }

    private static bool Moved(Enterprise action1) => action1.Calls.Any(c => c.StartsWith("POST move", StringComparison.Ordinal));

    [Fact]
    public async Task A_live_endpoint_of_another_client_reporting_an_Action1_id_never_pulls_it_into_its_own_organization()
    {
        var setup = await SeedAsync();
        // B's endpoint runs the Action1 agent and is online; A's endpoint reports the same id now.
        var victim = await ReporterAsync(setup.ClientB, "VICTIM-B", setup.Action1Id, Now.AddDays(-1), online: true, lastSeen: Now,
            enrolled: Now.AddDays(-60));
        var reporter = await ReporterAsync(setup.ClientA, "OTHER-A", setup.Action1Id, Now, online: true, lastSeen: Now,
            enrolled: Now.AddDays(-30));

        await Sync(setup.Action1).SyncAsync(force: true, CancellationToken.None);
        var move = await MoveAsync(setup.Integration);
        Assert.Equal((setup.ClientA.Id, TenantB, setup.Action1Id), (move.TargetClientId!.Value, move.ExternalTenantId, move.ExternalEndpointId));
        Assert.Contains("VICTIM-B", move.LastError);
        Assert.Contains("dismiss this here", move.LastError);
        Assert.True(move.NextAttemptAt > Now);

        await Follow(setup.Action1).FollowAsync(CancellationToken.None);
        Assert.False(Moved(setup.Action1));
        Assert.Equal(TenantB, setup.Action1.Endpoints[setup.Action1Id]);

        // Waiting with its cause; not queued twice, and B's endpoint keeps the patch state Action1 holds for it.
        await Sync(setup.Action1).SyncAsync(force: true, CancellationToken.None);
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.Single(await read.IntegrationOperations.Where(o => o.IntegrationId == setup.Integration.Id).ToListAsync());
        var state = await read.EndpointPatchStates.AsNoTracking().SingleAsync(s => s.ExternalEndpointId == setup.Action1Id);
        Assert.Equal((victim.Id, setup.ClientB.Id, TenantB), (state.EndpointId, state.ClientId, state.ExternalTenantId));
        Assert.False(await read.EndpointPatchStates.AnyAsync(s => s.EndpointId == reporter.Id));
        Assert.False(await read.AuditEntries.AnyAsync(a => a.Action == AuditActions.IntegrationEndpointMoved && a.ClientId == setup.ClientA.Id));
    }

    [Fact]
    public async Task An_offline_endpoint_seen_after_the_other_endpoint_enrolled_is_not_a_record_left_behind()
    {
        var setup = await SeedAsync();
        // B's endpoint is off for the night; A's endpoint enrolled long before that, so the machine did not move to A.
        await ReporterAsync(setup.ClientB, "VICTIM-B", setup.Action1Id, Now.AddDays(-1), online: false, lastSeen: Now.AddHours(-8),
            enrolled: Now.AddDays(-60));
        await ReporterAsync(setup.ClientA, "OTHER-A", setup.Action1Id, Now, online: true, lastSeen: Now, enrolled: Now.AddDays(-30));

        await Sync(setup.Action1).SyncAsync(force: true, CancellationToken.None);
        await Follow(setup.Action1).FollowAsync(CancellationToken.None);

        Assert.NotNull((await MoveAsync(setup.Integration)).LastError);
        Assert.False(Moved(setup.Action1));
    }

    [Fact]
    public async Task An_Action1_id_no_endpoint_of_the_holding_client_reported_is_not_moved_on_the_word_of_one_agent()
    {
        var setup = await SeedAsync();
        await ReporterAsync(setup.ClientA, "OTHER-A", setup.Action1Id, Now, online: true, lastSeen: Now, enrolled: Now);

        await Sync(setup.Action1).SyncAsync(force: true, CancellationToken.None);
        await Follow(setup.Action1).FollowAsync(CancellationToken.None);

        var move = await MoveAsync(setup.Integration);
        Assert.Contains("on the word of one agent", move.LastError);
        Assert.False(Moved(setup.Action1));
    }

    [Fact]
    public async Task A_move_queued_without_a_cause_is_judged_again_before_Action1_is_asked()
    {
        var setup = await SeedAsync();
        await ReporterAsync(setup.ClientB, "VICTIM-B", setup.Action1Id, Now.AddDays(-1), online: true, lastSeen: Now, enrolled: Now.AddDays(-60));
        var reporter = await ReporterAsync(setup.ClientA, "OTHER-A", setup.Action1Id, Now, online: true, lastSeen: Now, enrolled: Now);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            // As an earlier release queued it.
            db.IntegrationOperations.Add(new IntegrationOperation
            {
                Id = Guid.NewGuid(), IntegrationId = setup.Integration.Id, Kind = IntegrationOperationKind.MoveEndpoint,
                TargetClientId = setup.ClientA.Id, ExternalTenantId = TenantB, ExternalEndpointId = setup.Action1Id, Name = reporter.Hostname,
                NextAttemptAt = Now, CreatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        await Follow(setup.Action1).FollowAsync(CancellationToken.None);

        Assert.False(Moved(setup.Action1));
        var move = await MoveAsync(setup.Integration);
        Assert.Contains("VICTIM-B", move.LastError);
        Assert.True(move.NextAttemptAt > Now);
    }

    [Fact]
    public async Task An_endpoint_enrolled_again_under_another_client_still_moves_to_that_organization()
    {
        var setup = await SeedAsync();
        // The record left behind under B went offline a day ago; the machine enrolled under A afterwards.
        await ReporterAsync(setup.ClientB, "MOVED-OLD", setup.Action1Id, Now.AddDays(-1), online: false, lastSeen: Now.AddDays(-1),
            enrolled: Now.AddDays(-60));
        var moved = await ReporterAsync(setup.ClientA, "MOVED-NEW", setup.Action1Id, Now, online: true, lastSeen: Now, enrolled: Now.AddHours(-23));

        await Sync(setup.Action1).SyncAsync(force: true, CancellationToken.None);
        Assert.Null((await MoveAsync(setup.Integration)).LastError);

        await Follow(setup.Action1).FollowAsync(CancellationToken.None);
        Assert.Contains($"POST move {TenantB}/{setup.Action1Id} -> {TenantA}", setup.Action1.Calls);
        Assert.Equal(TenantA, setup.Action1.Endpoints[setup.Action1Id]);

        await Sync(setup.Action1).SyncAsync(force: true, CancellationToken.None);
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.True(await read.AuditEntries.AnyAsync(a => a.Action == AuditActions.IntegrationEndpointMoved && a.ClientId == setup.ClientA.Id));
        var state = await read.EndpointPatchStates.AsNoTracking().SingleAsync(s => s.ExternalEndpointId == setup.Action1Id);
        Assert.Equal((moved.Id, setup.ClientA.Id, TenantA), (state.EndpointId, state.ClientId, state.ExternalTenantId));
    }

    /// <summary>An Action1 enterprise in memory: organizations and which one holds each endpoint; moves change the holder.</summary>
    private sealed class Enterprise : HttpMessageHandler
    {
        public Dictionary<string, string> Organizations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Endpoints { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var parts = path[(path.IndexOf("/api/3.0/", StringComparison.Ordinal) + "/api/3.0/".Length)..].Split('/');
            if (parts[0] == "oauth2")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}""");
            }

            if (parts[0] == "organizations" && request.Method == HttpMethod.Get)
            {
                return Page(Organizations.Select(o => new { id = o.Key, name = o.Value }));
            }

            if (parts[0] == "endpoints" && parts[1] == "managed")
            {
                var organization = parts[2];
                if (parts.Length == 3 && request.Method == HttpMethod.Get)
                {
                    var items = Endpoints.Where(e => e.Value == organization).Select(e => $$$"""
                        {"id":"{{{e.Key}}}","organization_id":"{{{organization}}}","name":"reported","device_name":"reported","platform":"Windows",
                         "agent_version":"2.0.33","last_seen":"{{{DateTime.UtcNow.AddMinutes(-5):yyyy-MM-dd_HH-mm-ss}}}","subscription_status":"Active",
                         "reboot_required":false,"missing_updates":{"critical":0,"other":0}}
                        """).ToList();
                    return Json(HttpStatusCode.OK, $$"""{"items":[{{string.Join(",", items)}}],"total_items":{{items.Count}}}""");
                }

                if (parts.Length == 5 && parts[4] == "move" && request.Method == HttpMethod.Post)
                {
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken))!;
                    var target = (string)body["target_organization_id"]!;
                    Calls.Add($"POST move {organization}/{parts[3]} -> {target}");
                    if (!Endpoints.TryGetValue(parts[3], out var holder) || holder != organization)
                    {
                        return Json(HttpStatusCode.NotFound, """{"user_message":"Not found"}""");
                    }

                    Endpoints[parts[3]] = target;
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = parts[3] }));
                }

                if (parts.Length == 5 && parts[4] == "missing-updates")
                {
                    return Json(HttpStatusCode.OK, """{"items":[],"total_items":0}""");
                }
            }

            if (parts[0] == "endpoints" && parts[1] == "groups" && request.Method == HttpMethod.Get)
            {
                return Page(Array.Empty<object>());
            }

            if (parts[0] == "endpoints" && parts[1] == "groups" && request.Method == HttpMethod.Post && parts.Length == 3)
            {
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = "group-" + Guid.NewGuid().ToString("N")[..6] }));
            }

            Calls.Add($"{request.Method} {path}");
            return Json(HttpStatusCode.OK, """{"user_message":"Success"}""");
        }

        private static HttpResponseMessage Page<T>(IEnumerable<T> items)
        {
            var list = items.ToList();
            return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = list, total_items = list.Count }));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
