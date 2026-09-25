using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Patch policies as automations in Action1 (0.6.0): one automation per client and patch policy, aimed at exactly the
/// managed endpoints the policy applies to; changes follow, unused automations go, one removed in the console comes back,
/// a refusal is kept with its reason, and automations Fleeto did not make are only recorded, never touched.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PatchAutomationTests
{
    private readonly WorkersFixture _fixture;

    public PatchAutomationTests(WorkersFixture fixture) => _fixture = fixture;

    private DateTime Now => _fixture.Now;

    private async Task<Integration> SeedAsync()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
        await db.SaveChangesAsync();

        var integration = new Integration
        {
            Id = Guid.NewGuid(), Type = IntegrationType.Action1, Region = Action1Region.Europe, CredentialName = "api-key-patch@action1.com",
            Enabled = true, CreatedAt = Now, UpdatedAt = Now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Db.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "patch-secret"));
        db.Integrations.Add(integration);
        await db.SaveChangesAsync();
        return integration;
    }

    private PatchAutomationService Service(FakeAction1 action1) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus,
            new Action1ClientFactory(_fixture.Db.SecretProtector, _fixture.Db.Time, NullLoggerFactory.Instance,
                IntegrationBudgets.WorkerRequestsPerMinute, () => action1),
            _fixture.Db.Licenses, _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<PatchAutomationService>.Instance);

    private async Task<(Client Client, Site First, Site Second)> MappedClientAsync(Integration integration, string tenantId)
    {
        var client = await _fixture.Db.CreateClientAsync();
        var first = await _fixture.Db.CreateSiteAsync(client.Id, "Servers");
        var second = await _fixture.Db.CreateSiteAsync(client.Id, "Workstations");
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = client.Id, ExternalTenantId = tenantId, ExternalTenantName = client.Name,
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
        return (client, first, second);
    }

    private async Task<Endpoint> ReportedEndpointAsync(Site site, string tenantId, string action1Id, EndpointTier tier = EndpointTier.Managed)
    {
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, tier, "PATCH-" + action1Id);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.EndpointPatchStates.Add(new EndpointPatchState
        {
            EndpointId = endpoint.Id, ClientId = site.ClientId, ExternalEndpointId = action1Id, ExternalTenantId = tenantId, UpdatedAt = Now
        });
        await db.SaveChangesAsync();
        return endpoint;
    }

    private async Task<PatchPolicy> PatchPolicyAsync(string name)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var policy = new PatchPolicy { Id = Guid.NewGuid(), Name = name + " " + Guid.NewGuid().ToString("N")[..6], CreatedAt = Now, UpdatedAt = Now };
        db.PatchPolicies.Add(policy);
        await db.SaveChangesAsync();
        return policy;
    }

    private async Task AddAsync(params object[] rows)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.AddRange(rows);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Every_patch_policy_in_use_becomes_one_automation_aimed_at_exactly_its_endpoints()
    {
        var integration = await SeedAsync();
        var (client, servers, workstations) = await MappedClientAsync(integration, "org-a");
        var server = await ReportedEndpointAsync(servers, "org-a", "a1");
        var workstation = await ReportedEndpointAsync(workstations, "org-a", "a2");
        await ReportedEndpointAsync(servers, "org-a", "a3", EndpointTier.AgentOnly);
        await ReportedEndpointAsync(servers, "org-elsewhere", "a4");
        await _fixture.Db.CreateEndpointAsync(servers, EndpointTier.Managed, "PATCH-UNKNOWN");
        var weekly = await PatchPolicyAsync("Weekly");
        var urgent = await PatchPolicyAsync("Urgent");
        await AddAsync(
            new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = weekly.Id, CreatedAt = Now },
            new EndpointPatchPolicy { EndpointId = workstation.Id, ClientId = client.Id, PatchPolicyId = urgent.Id, CreatedAt = Now });
        var action1 = new FakeAction1();
        action1.Automations["theirs"] = new FakeAutomation("org-a", new JsonObject { ["name"] = "Monthly patching", ["settings"] = "ENABLED MONTHLY:1 AT:03-00-00" });

        await Service(action1).SyncAsync(CancellationToken.None);

        var ours = action1.Automations.Values.Where(a => a.Name.StartsWith(Action1Automation.NamePrefix, StringComparison.Ordinal)).ToList();
        Assert.Equal(2, ours.Count);
        Assert.All(ours, a => Assert.Equal("org-a", a.Organization));
        Assert.Equal(["a1"], ours.Single(a => a.Name.EndsWith(weekly.Name, StringComparison.Ordinal)).EndpointIds);
        Assert.Equal(["a2"], ours.Single(a => a.Name.EndsWith(urgent.Name, StringComparison.Ordinal)).EndpointIds);
        Assert.Equal("Monthly patching", action1.Automations["theirs"].Name);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var mapping = await db.IntegrationMappings.SingleAsync(m => m.ClientId == client.Id);
            var others = OtherAutomation.Parse(mapping.OtherAutomationsJson);
            Assert.Equal("Monthly patching", Assert.Single(others).Name);
            Assert.Equal(2, await db.IntegrationAutomations.CountAsync(a => a.ClientId == client.Id && a.LastError == null));
        }

        // Nothing changed: nothing is written.
        var writes = action1.Writes;
        await Service(action1).SyncAsync(CancellationToken.None);
        Assert.Equal(writes, action1.Writes);

        // A change to the policy rewrites its automation in place.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.PatchPolicies.Where(p => p.Id == weekly.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.StartMinute, 23 * 60));
        }

        await Service(action1).SyncAsync(CancellationToken.None);
        var rewritten = action1.Automations.Values.Single(a => a.Name.EndsWith(weekly.Name, StringComparison.Ordinal));
        Assert.EndsWith("AT:23-00-00", rewritten.Settings);

        // The endpoint follows its client again: the urgent automation has no targets left and goes.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.EndpointPatchPolicies.Where(l => l.EndpointId == workstation.Id).ExecuteDeleteAsync();
        }

        await Service(action1).SyncAsync(CancellationToken.None);
        ours = action1.Automations.Values.Where(a => a.Name.StartsWith(Action1Automation.NamePrefix, StringComparison.Ordinal)).ToList();
        var remaining = Assert.Single(ours);
        Assert.Equal(["a1", "a2"], remaining.EndpointIds);
        Assert.True(action1.Automations.ContainsKey("theirs"));
    }

    [Fact]
    public async Task An_automation_removed_in_the_console_comes_back_a_refusal_is_kept_and_a_deleted_policy_takes_its_automation_along()
    {
        var integration = await SeedAsync();
        var (client, servers, _) = await MappedClientAsync(integration, "org-b");
        await ReportedEndpointAsync(servers, "org-b", "b1");
        var policy = await PatchPolicyAsync("Nightly");
        await AddAsync(new SitePatchPolicy { SiteId = servers.Id, ClientId = client.Id, PatchPolicyId = policy.Id, CreatedAt = Now });
        var action1 = new FakeAction1();

        await Service(action1).SyncAsync(CancellationToken.None);
        var first = Assert.Single(action1.Automations);

        // Removed in the console, then changed in Fleeto: the update finds nothing and the automation is created again.
        action1.Automations.Clear();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.PatchPolicies.Where(p => p.Id == policy.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.RetryHours, 6));
        }

        await Service(action1).SyncAsync(CancellationToken.None);
        var again = Assert.Single(action1.Automations);
        Assert.NotEqual(first.Key, again.Key);

        // Action1 refuses a change: the reason is kept on the automation, and nothing is retried at once.
        action1.Refuse = true;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.PatchPolicies.Where(p => p.Id == policy.Id).ExecuteUpdateAsync(s => s.SetProperty(p => p.RetryHours, 8));
        }

        await Service(action1).SyncAsync(CancellationToken.None);
        var writes = action1.Writes;
        await Service(action1).SyncAsync(CancellationToken.None);
        Assert.Equal(writes, action1.Writes);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var row = await db.IntegrationAutomations.SingleAsync(a => a.PatchPolicyId == policy.Id);
            Assert.Contains("manage automations", row.LastError);
        }

        // The policy is deleted: its automation goes, and so does the row that remembered it.
        action1.Refuse = false;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.PatchPolicies.Where(p => p.Id == policy.Id).ExecuteDeleteAsync();
        }

        await Service(action1).SyncAsync(CancellationToken.None);
        Assert.Empty(action1.Automations);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.False(await db.IntegrationAutomations.AnyAsync(a => a.PatchPolicyId == policy.Id));
        }
    }

    private sealed class FakeAction1 : HttpMessageHandler
    {
        private int _next;

        public Dictionary<string, FakeAutomation> Automations { get; } = new(StringComparer.Ordinal);
        public int Writes { get; private set; }
        public bool Refuse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var parts = path[(path.IndexOf("/api/3.0/", StringComparison.Ordinal) + "/api/3.0/".Length)..].Split('/');
            if (parts[0] == "oauth2")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}""");
            }

            // automations/schedules/{org}[/{id}]
            var organization = parts[2];
            if (request.Method == HttpMethod.Get)
            {
                var items = Automations.Where(a => a.Value.Organization == organization)
                    .Select(a => new { id = a.Key, name = a.Value.Name, settings = a.Value.Settings }).ToList();
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items, total_items = items.Count }));
            }

            Writes++;
            if (Refuse)
            {
                return Json(HttpStatusCode.BadRequest, """{"user_message":"Not allowed."}""");
            }

            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
            if (request.Method == HttpMethod.Post)
            {
                var id = $"automation-{++_next}";
                Automations[id] = new FakeAutomation(organization, body!);
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id }));
            }

            if (!Automations.ContainsKey(parts[3]))
            {
                return Json(HttpStatusCode.NotFound, """{"user_message":"Not found"}""");
            }

            if (request.Method == HttpMethod.Patch)
            {
                Automations[parts[3]] = new FakeAutomation(organization, body!);
            }
            else
            {
                Automations.Remove(parts[3]);
            }

            return Json(HttpStatusCode.OK, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private sealed record FakeAutomation(string Organization, JsonObject Body)
    {
        public string Name => (string)Body["name"]!;
        public string Settings => (string)Body["settings"]!;

        public IReadOnlyList<string> EndpointIds =>
            Body["endpoints"]?.AsArray().Select(e => (string)e!["id"]!).Order(StringComparer.Ordinal).ToList() ?? [];
    }
}
