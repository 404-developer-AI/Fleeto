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
    public async Task Endpoints_know_their_automation_its_next_run_is_read_and_each_run_becomes_one_scheduled_deployment()
    {
        var integration = await SeedAsync();
        var (client, servers, _) = await MappedClientAsync(integration, "org-c");
        var server = await ReportedEndpointAsync(servers, "org-c", "c1");
        var policy = await PatchPolicyAsync("Weekly");
        await AddAsync(new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = policy.Id, CreatedAt = Now });
        var action1 = new FakeAction1();

        // The automation is written and read back in the same pass, so its next run is known at once.
        await Service(action1).SyncAsync(CancellationToken.None);

        IntegrationAutomation row;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            row = await db.IntegrationAutomations.AsNoTracking().SingleAsync(a => a.ClientId == client.Id);
            Assert.Equal(new DateTime(2026, 12, 2, 20, 0, 0), row.NextRunAt);
            var state = await db.EndpointPatchStates.AsNoTracking().SingleAsync(s => s.EndpointId == server.Id);
            Assert.Equal((row.Id, policy.Id), (state.AutomationId, state.ScheduledPolicyId));
            // PostgreSQL keeps microseconds, the clock of the test has ticks.
            Assert.Equal(Now, state.ScheduledSince!.Value, TimeSpan.FromMilliseconds(1));
            Assert.False(await db.PatchDeployments.AnyAsync(d => d.ClientId == client.Id));
        }

        // Action1 ran it: the next read turns the run into a scheduled deployment of the endpoints Fleeto put in it.
        action1.LastInstances[row.ExternalAutomationId!] = "run-1";
        await ReadAgainAsync(client.Id);
        await Service(action1).SyncAsync(CancellationToken.None);
        await ReadAgainAsync(client.Id);
        await Service(action1).SyncAsync(CancellationToken.None);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var deployment = await db.PatchDeployments.AsNoTracking().Include(d => d.Targets).SingleAsync(d => d.ClientId == client.Id);
            Assert.Equal((PatchDeploymentScope.PatchPolicy, "run-1", row.ExternalAutomationId, policy.Id, policy.Name),
                (deployment.Scope, deployment.ExternalDeploymentId, deployment.ExternalAutomationId, deployment.PatchPolicyId, deployment.RequestedByName));
            Assert.Equal(PatchDeploymentState.Running, deployment.State);
            // Followed as long as Action1 retries endpoints for this policy, plus an hour.
            Assert.Equal(deployment.StartedAt + TimeSpan.FromHours(policy.RetryHours + 1), deployment.FollowUntil);
            var target = Assert.Single(deployment.Targets);
            Assert.Equal((server.Id, "c1", PatchDeploymentTargetState.Pending), (target.EndpointId, target.ExternalEndpointId, target.State));
        }

        // The policy no longer applies: the endpoint forgets its automation.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.ClientPatchPolicies.Where(l => l.ClientId == client.Id).ExecuteDeleteAsync();
        }

        await Service(action1).SyncAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var state = await db.EndpointPatchStates.AsNoTracking().SingleAsync(s => s.EndpointId == server.Id);
            Assert.Equal((null, null, null), (state.AutomationId, state.ScheduledPolicyId, state.ScheduledSince));
        }
    }

    /// <summary>
    /// The scheduled rounds on the test VPS never named a <c>last_instance</c> (found 2026-10-19, 0.7.0): the round is then found
    /// among the runs of the organization, by its schedule link or by its id, and becomes one scheduled deployment like before.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Without_a_last_instance_the_round_is_found_among_the_runs_of_the_organization(bool scheduleLink)
    {
        var integration = await SeedAsync();
        var organization = "org-r" + (scheduleLink ? "1" : "2");
        var (client, servers, _) = await MappedClientAsync(integration, organization);
        var server = await ReportedEndpointAsync(servers, organization, "r1");
        var policy = await PatchPolicyAsync("Rounds " + scheduleLink);
        await AddAsync(new ClientPatchPolicy { ClientId = client.Id, PatchPolicyId = policy.Id, CreatedAt = Now });
        var action1 = new FakeAction1();
        await Service(action1).SyncAsync(CancellationToken.None);
        string automationId;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            automationId = (await db.IntegrationAutomations.AsNoTracking().SingleAsync(a => a.ClientId == client.Id)).ExternalAutomationId!;
        }

        string Time(DateTime t) => t.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
        var round = $"{automationId}_{Time(Now.AddHours(-3))}";
        action1.Runs.Add((organization, $"{automationId}_{Time(Now.AddDays(-20))}", scheduleLink ? automationId : null, Time(Now.AddDays(-20))));
        action1.Runs.Add((organization, round, scheduleLink ? automationId : null, Time(Now.AddHours(-3))));
        // A deployment a technician started is a run of another automation.
        action1.Runs.Add((organization, "Fleeto_2026_10_19_13_04_UTC_1_" + Time(Now.AddHours(-1)), scheduleLink ? "Fleeto_2026_10_19_13_04_UTC_1" : null,
            Time(Now.AddHours(-1))));

        await ReadAgainAsync(client.Id);
        await Service(action1).SyncAsync(CancellationToken.None);
        await ReadAgainAsync(client.Id);
        await Service(action1).SyncAsync(CancellationToken.None);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var deployment = await db.PatchDeployments.AsNoTracking().Include(d => d.Targets).SingleAsync(d => d.ClientId == client.Id);
            Assert.Equal((PatchDeploymentScope.PatchPolicy, round, policy.Id), (deployment.Scope, deployment.ExternalDeploymentId, deployment.PatchPolicyId));
            Assert.Equal(server.Id, Assert.Single(deployment.Targets).EndpointId);
        }

        Assert.True(action1.RunReads >= 2);
    }

    [Fact]
    public void A_last_instance_of_its_own_wins_over_the_runs_of_the_organization()
    {
        var info = new Action1AutomationInfo("auto-1", "Fleeto: Weekly", "ENABLED WEEKLY:Mon AT:10-00-00", LastInstanceId: "named");
        var runs = new[] { new Action1AutomationRun("auto-1_2026-10-19_08-00-00", "auto-1", Now) };

        Assert.Equal("named", ScheduledDeployments.WithRun(info, runs, Now).LastInstanceId);
        Assert.Equal("auto-1_2026-10-19_08-00-00", ScheduledDeployments.WithRun(info with { LastInstanceId = "" }, runs, Now).LastInstanceId);
        // Another automation whose id starts the same is not this one.
        Assert.False(new Action1AutomationRun("auto-10_2026-10-19_08-00-00", "", Now).IsRunOf("auto-1"));
    }

    /// <summary>Makes the automations of the client's organization due for their hourly read.</summary>
    private async Task ReadAgainAsync(Guid clientId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.IntegrationMappings.Where(m => m.ClientId == clientId)
            .ExecuteUpdateAsync(u => u.SetProperty(m => m.OtherAutomationsReadAt, (DateTime?)null));
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

        /// <summary>The run Action1 started last, per automation id (0.6.0).</summary>
        public Dictionary<string, string> LastInstances { get; } = new(StringComparer.Ordinal);

        /// <summary>The runs of every organization under automations/instances (0.7.0): id, schedule link or null, start time.</summary>
        public List<(string Organization, string Id, string? Schedule, string StartTime)> Runs { get; } = [];

        public int RunReads { get; private set; }

        /// <summary>The next run every automation reports, in Action1's notation (0.6.0).</summary>
        public string NextRun { get; set; } = "2026-12-02_20-00-00";

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

            // automations/instances/{org}: the runs (0.7.0).
            var organization = parts[2];
            if (parts[1] == "instances" && request.Method == HttpMethod.Get)
            {
                RunReads++;
                var runs = Runs.Where(r => r.Organization == organization)
                    .Select(r => r.Schedule is null
                        ? (object)new { id = r.Id, start_time = r.StartTime, status = "Running" }
                        : new { id = r.Id, schedule = $"/API/automations/schedules/{organization}/{r.Schedule}", start_time = r.StartTime, status = "Running" })
                    .ToList();
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { items = runs, total_items = runs.Count }));
            }

            // automations/schedules/{org}[/{id}]
            if (request.Method == HttpMethod.Get)
            {
                var items = Automations.Where(a => a.Value.Organization == organization)
                    .Select(a => new
                    {
                        id = a.Key, name = a.Value.Name, settings = a.Value.Settings, next_run = NextRun,
                        last_instance = LastInstances.TryGetValue(a.Key, out var instance) ? "/API/automations/instances/" + instance : ""
                    }).ToList();
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
