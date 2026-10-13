using System.Net;
using System.Text;
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
/// What a technician asks about the patch management of one endpoint (0.6.0): a sync reads the endpoint, its missing
/// updates and the automation of its patch policy from Action1 at once; a removal deletes the endpoint in Action1, which
/// uninstalls its agent, and forgets its patch state; every outcome lands on the request. And the scheduled patching check
/// counts the days since a scheduled deployment last succeeded.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PatchRequestTests
{
    private const string Tenant = "org-request";
    private readonly WorkersFixture _fixture;

    public PatchRequestTests(WorkersFixture fixture) => _fixture = fixture;

    private DateTime Now => _fixture.Now;

    private async Task<(Client Client, Integration Integration)> SeedAsync()
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var client = await _fixture.Db.CreateClientAsync();
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
        await db.SaveChangesAsync();

        var integration = new Integration
        {
            Id = Guid.NewGuid(), Type = IntegrationType.Action1, Region = Action1Region.Europe, CredentialName = "api-key-request@action1.com",
            Enabled = true, CreatedAt = Now, UpdatedAt = Now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Db.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "request-secret"));
        db.Integrations.Add(integration);
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = client.Id, ExternalTenantId = Tenant, ExternalTenantName = "Request org",
            CreatedAt = Now
        });
        await db.SaveChangesAsync();
        return (client, integration);
    }

    private async Task<Endpoint> EndpointAsync(Client client, string action1Id)
    {
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Site " + Guid.NewGuid().ToString("N")[..6]);
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "REQ-" + Guid.NewGuid().ToString("N")[..6]);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.InventorySnapshots.Add(new InventorySnapshot
        {
            EndpointId = endpoint.Id, ClientId = client.Id, ReceivedAt = Now, Hash = Guid.NewGuid().ToString("N"), Action1AgentId = action1Id
        });
        await db.SaveChangesAsync();
        return endpoint;
    }

    private async Task<PatchRequest> RequestAsync(Endpoint endpoint, PatchRequestKind kind, string tenant = "", string action1Id = "")
    {
        var request = new PatchRequest
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Kind = kind, ExternalTenantId = tenant,
            ExternalEndpointId = action1Id, RequestedByUserId = Guid.NewGuid(), RequestedByName = "Tech", RequestedAt = Now
        };
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.PatchRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    private PatchRequestService Service(FakeAction1 action1) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus,
            new Action1ClientFactory(_fixture.Db.SecretProtector, _fixture.Db.Time, NullLoggerFactory.Instance,
                IntegrationBudgets.WorkerRequestsPerMinute, () => action1),
            new AlertNotificationService(_fixture.Db.Time), _fixture.Db.Licenses, _fixture.Heartbeat(), _fixture.Db.Time,
            NullLogger<PatchRequestService>.Instance);

    private async Task<PatchRequest> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.PatchRequests.AsNoTracking().SingleAsync(r => r.Id == id);
    }

    [Fact]
    public async Task A_sync_reads_the_endpoint_and_takes_the_counts_from_its_missing_updates_when_Action1_leaves_them_out()
    {
        var (client, _) = await SeedAsync();
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await EndpointAsync(client, action1Id);
        var request = await RequestAsync(endpoint, PatchRequestKind.Sync);
        var action1 = new FakeAction1 { Endpoints = { action1Id } };

        await Service(action1).RunAsync(CancellationToken.None);

        var done = await ReadAsync(request.Id);
        Assert.NotNull(done.CompletedAt);
        Assert.Null(done.Failure);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var state = await db.EndpointPatchStates.AsNoTracking().SingleAsync(s => s.EndpointId == endpoint.Id);
        Assert.Equal((Tenant, action1Id, 1, 1), (state.ExternalTenantId, state.ExternalEndpointId, state.MissingCritical, state.MissingOther));
        Assert.Equal(2, await db.EndpointMissingUpdates.CountAsync(u => u.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task A_sync_of_an_endpoint_Action1_does_not_hold_forgets_its_patch_state_and_says_why()
    {
        var (client, _) = await SeedAsync();
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await EndpointAsync(client, action1Id);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = client.Id, ExternalEndpointId = action1Id, ExternalTenantId = Tenant, UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var request = await RequestAsync(endpoint, PatchRequestKind.Sync);

        await Service(new FakeAction1()).RunAsync(CancellationToken.None);

        var done = await ReadAsync(request.Id);
        Assert.NotNull(done.CompletedAt);
        Assert.Contains("does not hold this endpoint", done.Failure);
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await read.EndpointPatchStates.AnyAsync(s => s.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task A_sync_finds_the_run_of_the_patch_policy_that_just_happened()
    {
        var (client, integration) = await SeedAsync();
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await EndpointAsync(client, action1Id);
        var policy = new PatchPolicy { Id = Guid.NewGuid(), Name = "Monday " + Guid.NewGuid().ToString("N")[..6], CreatedAt = Now, UpdatedAt = Now };
        var row = new IntegrationAutomation
        {
            Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = client.Id, ExternalTenantId = Tenant, PatchPolicyId = policy.Id,
            ExternalAutomationId = "automation-1", CreatedAt = Now, UpdatedAt = Now
        };
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.PatchPolicies.Add(policy);
            db.IntegrationAutomations.Add(row);
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = client.Id, ExternalEndpointId = action1Id, ExternalTenantId = Tenant, UpdatedAt = Now,
                AutomationId = row.Id, ScheduledPolicyId = policy.Id, ScheduledSince = Now.AddDays(-7)
            });
            await db.SaveChangesAsync();
        }

        await RequestAsync(endpoint, PatchRequestKind.Sync);
        var action1 = new FakeAction1 { Endpoints = { action1Id }, CountsIncluded = true, LastInstance = "instance-7", NextRun = "2026-12-07_20-00-00" };

        await Service(action1).RunAsync(CancellationToken.None);

        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.Equal(new DateTime(2026, 12, 7, 20, 0, 0), (await read.IntegrationAutomations.AsNoTracking().SingleAsync(a => a.Id == row.Id)).NextRunAt);
        var deployment = await read.PatchDeployments.AsNoTracking().Include(d => d.Targets).SingleAsync(d => d.ClientId == client.Id);
        Assert.Equal((PatchDeploymentScope.PatchPolicy, "instance-7", policy.Id, policy.Name, PatchDeploymentState.Running),
            (deployment.Scope, deployment.ExternalDeploymentId, deployment.PatchPolicyId, deployment.RequestedByName, deployment.State));
        Assert.Equal(endpoint.Id, Assert.Single(deployment.Targets).EndpointId);
    }

    [Fact]
    public async Task A_removal_deletes_the_endpoint_in_Action1_forgets_its_patch_state_and_is_audited()
    {
        var (client, _) = await SeedAsync();
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await EndpointAsync(client, action1Id);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = client.Id, ExternalEndpointId = action1Id, ExternalTenantId = Tenant, UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var request = await RequestAsync(endpoint, PatchRequestKind.RemoveAgent, Tenant, action1Id);
        var action1 = new FakeAction1 { Endpoints = { action1Id } };

        await Service(action1).RunAsync(CancellationToken.None);

        Assert.Equal([$"{Tenant}/{action1Id}"], action1.Deleted);
        Assert.Null((await ReadAsync(request.Id)).Failure);
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await read.EndpointPatchStates.AnyAsync(s => s.EndpointId == endpoint.Id));
        Assert.True(await read.AuditEntries.AnyAsync(a => a.Action == AuditActions.PatchAgentRemovalEnded && a.TargetId == endpoint.Id.ToString()));
    }

    [Fact]
    public async Task A_removal_Action1_refuses_keeps_the_patch_state_and_names_the_permission()
    {
        var (client, _) = await SeedAsync();
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await EndpointAsync(client, action1Id);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = client.Id, ExternalEndpointId = action1Id, ExternalTenantId = Tenant, UpdatedAt = Now
            });
            await db.SaveChangesAsync();
        }

        var request = await RequestAsync(endpoint, PatchRequestKind.RemoveAgent, Tenant, action1Id);

        await Service(new FakeAction1 { Endpoints = { action1Id }, RefuseDelete = true }).RunAsync(CancellationToken.None);

        Assert.Contains("may manage endpoints", (await ReadAsync(request.Id)).Failure);
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.True(await read.EndpointPatchStates.AnyAsync(s => s.EndpointId == endpoint.Id));
    }

    [Fact]
    public async Task The_scheduled_patching_check_counts_the_days_since_a_scheduled_deployment_last_succeeded()
    {
        var (client, integration) = await SeedAsync();
        var endpoint = await EndpointAsync(client, Guid.NewGuid().ToString());
        var check = await _fixture.CreateCheckAsync(new Site { Id = endpoint.SiteId, ClientId = client.Id }, CheckType.ScheduledPatching,
            warning: 8, critical: 15);
        var automationId = Guid.NewGuid();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = client.Id, ExternalEndpointId = "x", ExternalTenantId = Tenant, UpdatedAt = Now,
                AutomationId = automationId, ScheduledPolicyId = Guid.NewGuid(), ScheduledSince = Now.AddDays(-10)
            });
            await db.SaveChangesAsync();
        }

        // Nothing succeeded yet: counted from when the policy started on the endpoint.
        Assert.Equal(10, await EvaluateAsync(endpoint, check));

        var deployment = new PatchDeployment
        {
            Id = Guid.NewGuid(), ClientId = client.Id, BatchId = Guid.NewGuid(), ExternalTenantId = Tenant, ExternalDeploymentId = "run-1",
            ExternalAutomationId = "automation-1", Scope = PatchDeploymentScope.PatchPolicy, State = PatchDeploymentState.Completed,
            RequestedByName = "Weekly", RequestedAt = Now.AddDays(-3)
        };
        deployment.Targets.Add(new PatchDeploymentTarget
        {
            Id = Guid.NewGuid(), DeploymentId = deployment.Id, ClientId = client.Id, EndpointId = endpoint.Id, ExternalEndpointId = "x",
            Hostname = endpoint.Hostname, State = PatchDeploymentTargetState.Succeeded, UpdatedAt = Now.AddDays(-3)
        });
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.PatchDeployments.Add(deployment);
            await db.SaveChangesAsync();
        }

        Assert.Equal(3, await EvaluateAsync(endpoint, check));

        // No automation targets the endpoint any more: its check has nothing to say and loses its state.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.EndpointPatchStates.Where(s => s.EndpointId == endpoint.Id)
                .ExecuteUpdateAsync(u => u.SetProperty(s => s.AutomationId, (Guid?)null).SetProperty(s => s.ScheduledSince, (DateTime?)null));
            var (written, _) = await ScheduledPatchingChecks.EvaluateAsync(db, [endpoint.Id], Now, CancellationToken.None);
            Assert.Empty(written);
        }
    }

    private async Task<double> EvaluateAsync(Endpoint endpoint, CheckDefinition check)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.CheckResults.Where(r => r.EndpointId == endpoint.Id).ExecuteDeleteAsync();
        var (written, _) = await ScheduledPatchingChecks.EvaluateAsync(db, [endpoint.Id], Now, CancellationToken.None);
        await db.SaveChangesAsync();
        Assert.Equal([endpoint.Id], written);
        return (await db.CheckResults.AsNoTracking().SingleAsync(r => r.EndpointId == endpoint.Id && r.CheckDefinitionId == check.Id)).Value;
    }

    /// <summary>Answers like Action1 for one endpoint, its missing updates, its removal and one automation.</summary>
    private sealed class FakeAction1 : HttpMessageHandler
    {
        public HashSet<string> Endpoints { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>False leaves the counts of missing updates out of a single endpoint, as Action1 may.</summary>
        public bool CountsIncluded { get; init; }

        public bool RefuseDelete { get; init; }
        public string LastInstance { get; init; } = string.Empty;
        public string NextRun { get; init; } = string.Empty;
        public List<string> Deleted { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            var parts = path[(path.IndexOf("/api/3.0/", StringComparison.Ordinal) + "/api/3.0/".Length)..].Split('/');
            if (parts[0] == "oauth2")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}""");
            }

            if (parts[0] == "automations")
            {
                return Json(HttpStatusCode.OK, $$"""
                    {"id":"{{parts[3]}}","name":"Fleeto: policy","settings":"ENABLED WEEKLY:Mon AT:20-00-00","next_run":"{{NextRun}}",
                     "last_run":"2026-10-12_20-00-00","last_instance":"{{(LastInstance.Length == 0 ? "" : "/API/automations/instances/" + LastInstance)}}"}
                    """);
            }

            // endpoints/managed/{org}/{id}[/missing-updates]
            var id = parts[3];
            if (request.Method == HttpMethod.Delete)
            {
                if (RefuseDelete)
                {
                    return Json(HttpStatusCode.Forbidden, """{"user_message":"Permission denied."}""");
                }

                Deleted.Add($"{parts[2]}/{id}");
                return Json(HttpStatusCode.OK, "{}");
            }

            if (!Endpoints.Contains(id))
            {
                return Json(HttpStatusCode.NotFound, """{"user_message":"Not found"}""");
            }

            if (parts.Length > 4 && parts[4] == "missing-updates")
            {
                return Json(HttpStatusCode.OK, """
                    {"items":[
                      {"id":"upd-1","name":"2026-10 Cumulative Update","vendor":"Microsoft","kb_number":"KB5040001",
                       "versions":[{"version":"10.0.1","security_severity":"Critical","release_date":"2026-10-08"}]},
                      {"id":"upd-2","name":"7-Zip","vendor":"Igor Pavlov",
                       "versions":[{"version":"24.08","security_severity":"Moderate","version_outdated":"23.01"}]}
                    ],"total_items":2}
                    """);
            }

            var counts = CountsIncluded ? ""","missing_updates":{"critical":0,"other":0}""" : "";
            return Json(HttpStatusCode.OK, $$"""
                {"id":"{{id}}","organization_id":"{{parts[2]}}","name":"reported","device_name":"reported","platform":"Windows",
                 "agent_version":"2.0.33","last_seen":"2026-10-12_08-00-00","subscription_status":"Active","reboot_required":false{{counts}}}
                """);
        }

        private static Task<HttpResponseMessage> Json(HttpStatusCode status, string body) =>
            Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
