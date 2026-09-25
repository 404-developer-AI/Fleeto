using System.Net;
using System.Text;
using System.Text.Json;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees of the deployment worker (0.4.0 step 3): a request travels to the product with the endpoints and packages
/// the technician chose, what an endpoint did comes from the product and nowhere else, a deployment the product refuses
/// fails with its own message, one it never finishes is abandoned instead of staying open, and a finished deployment asks
/// for fresh patch state.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PatchDeploymentTests
{
    private const string Tenant = "org-deploy";
    private readonly WorkersFixture _fixture;

    public PatchDeploymentTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<(Client Client, Integration Integration)> SeedAsync(string code, bool enabled = true)
    {
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
            CredentialName = "api-key-deploy@action1.com",
            Enabled = enabled,
            PatchSyncedAt = _fixture.Now,
            CreatedAt = _fixture.Now,
            UpdatedAt = _fixture.Now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Db.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "deploy-secret"));
        db.Integrations.Add(integration);
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(),
            IntegrationId = integration.Id,
            ClientId = client.Id,
            ExternalTenantId = Tenant,
            ExternalTenantName = "Deploy org",
            CreatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
        return (client, integration);
    }

    /// <summary>A deployment row as web writes it, with one endpoint and optionally the updates that were chosen.</summary>
    private async Task<PatchDeployment> RequestAsync(Client client, Endpoint endpoint, string action1Id,
        IReadOnlyList<(string Id, string Version)>? updates = null, DateTime? requestedAt = null, PatchDeploymentState state = PatchDeploymentState.Requested,
        string externalId = "")
    {
        var at = requestedAt ?? _fixture.Now;
        var deployment = new PatchDeployment
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            BatchId = Guid.NewGuid(),
            ExternalTenantId = Tenant,
            ExternalDeploymentId = externalId,
            Scope = updates is null ? PatchDeploymentScope.AllMissing : PatchDeploymentScope.Specified,
            State = state,
            RequestedByUserId = Guid.NewGuid(),
            RequestedByName = "Tester",
            RequestedAt = at,
            StartedAt = state == PatchDeploymentState.Running ? at : null
        };
        deployment.Targets.Add(new PatchDeploymentTarget
        {
            Id = Guid.NewGuid(),
            ClientId = client.Id,
            EndpointId = endpoint.Id,
            ExternalEndpointId = action1Id,
            Hostname = endpoint.Hostname,
            State = PatchDeploymentTargetState.Pending,
            UpdatedAt = at
        });
        foreach (var (id, version) in updates ?? [])
        {
            deployment.Updates.Add(new PatchDeploymentUpdate
            {
                Id = Guid.NewGuid(), DeploymentId = deployment.Id, ClientId = client.Id,
                ExternalUpdateId = id, Name = "Update " + id, Version = version
            });
        }

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.PatchDeployments.Add(deployment);
        await db.SaveChangesAsync();
        return deployment;
    }

    private PatchDeploymentService Service(StubHandler handler) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus,
            new Action1ClientFactory(_fixture.Db.SecretProtector, _fixture.Db.Time, NullLoggerFactory.Instance,
                IntegrationBudgets.WorkerRequestsPerMinute, () => handler),
            _fixture.Db.Licenses, _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<PatchDeploymentService>.Instance);

    private async Task<PatchDeployment> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.PatchDeployments.AsNoTracking().Include(d => d.Targets).SingleAsync(d => d.Id == id);
    }

    [Fact]
    public async Task A_requested_deployment_is_handed_to_the_product_with_its_endpoints_and_chosen_packages()
    {
        var (client, _) = await SeedAsync("PD1" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, "DEPLOY-01");
        var deployment = await RequestAsync(client, endpoint, action1Id, [("chrome", "126.0.6478.115")]);
        var handler = new StubHandler();

        await Service(handler).RunAsync(CancellationToken.None);

        var started = Assert.Single(handler.Started);
        Assert.Contains(Tenant, started.Path);
        var body = JsonDocument.Parse(started.Body).RootElement;
        Assert.Equal(action1Id, body.GetProperty("endpoints")[0].GetProperty("id").GetString());
        Assert.Equal("Endpoint", body.GetProperty("endpoints")[0].GetProperty("type").GetString());
        var parameters = body.GetProperty("actions")[0].GetProperty("params");
        Assert.Equal("deploy_update", body.GetProperty("actions")[0].GetProperty("template_id").GetString());
        Assert.Equal("Specified", parameters.GetProperty("scope").GetString());
        Assert.Equal("126.0.6478.115", parameters.GetProperty("packages")[0].GetProperty("chrome").GetString());
        Assert.Equal("no", parameters.GetProperty("reboot_options").GetProperty("auto_reboot").GetString());

        var stored = await ReadAsync(deployment.Id);
        Assert.Equal(PatchDeploymentState.Running, stored.State);
        Assert.Equal(StubHandler.DeploymentId, stored.ExternalDeploymentId);
        Assert.Equal(PatchDeploymentTargetState.Pending, stored.Targets.Single().State);
    }

    [Fact]
    public async Task A_deployment_of_every_missing_update_names_no_package_and_allows_a_restart_when_asked()
    {
        var (client, _) = await SeedAsync("PD2" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var endpoint = await CreateEndpointAsync(client, "DEPLOY-02");
        var deployment = await RequestAsync(client, endpoint, Guid.NewGuid().ToString());
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var row = await db.PatchDeployments.SingleAsync(d => d.Id == deployment.Id);
            row.AutoReboot = true;
            await db.SaveChangesAsync();
        }

        var handler = new StubHandler();
        await Service(handler).RunAsync(CancellationToken.None);

        var parameters = JsonDocument.Parse(Assert.Single(handler.Started).Body).RootElement
            .GetProperty("actions")[0].GetProperty("params");
        Assert.Equal("All", parameters.GetProperty("scope").GetString());
        // Every missing update, approved in Action1 or not: otherwise Action1 answers "No updates are applicable" (0.6.0).
        Assert.Equal("no", parameters.GetProperty("require_update_approval").GetString());
        Assert.False(parameters.TryGetProperty("packages", out _));
        var reboot = parameters.GetProperty("reboot_options");
        Assert.Equal("yes", reboot.GetProperty("auto_reboot").GetString());
        Assert.Equal(PatchRules.RebootTimeoutMinutes, reboot.GetProperty("timeout").GetInt32());
    }

    [Fact]
    public async Task A_deployment_the_product_refuses_fails_with_the_reason_and_is_not_retried()
    {
        var (client, _) = await SeedAsync("PD3" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var endpoint = await CreateEndpointAsync(client, "DEPLOY-03");
        var deployment = await RequestAsync(client, endpoint, Guid.NewGuid().ToString());
        var handler = new StubHandler { StartStatus = HttpStatusCode.Forbidden };

        await Service(handler).RunAsync(CancellationToken.None);
        await Service(handler).RunAsync(CancellationToken.None);

        Assert.Single(handler.Started);
        var stored = await ReadAsync(deployment.Id);
        Assert.Equal(PatchDeploymentState.Failed, stored.State);
        Assert.Contains("may not read this", stored.StatusMessage);
        // Nothing was installed anywhere, and the endpoint says so instead of waiting forever.
        Assert.Equal(PatchDeploymentTargetState.Failed, stored.Targets.Single().State);
    }

    [Fact]
    public async Task What_an_endpoint_did_comes_from_the_product_and_finishing_asks_for_fresh_patch_state()
    {
        var (client, integration) = await SeedAsync("PD4" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, "DEPLOY-04");
        var deployment = await RequestAsync(client, endpoint, action1Id, state: PatchDeploymentState.Running,
            externalId: StubHandler.DeploymentId);
        var handler = new StubHandler { Results = [(action1Id, "In progress")] };

        await Service(handler).RunAsync(CancellationToken.None);

        var running = await ReadAsync(deployment.Id);
        Assert.Equal(PatchDeploymentState.Running, running.State);
        Assert.Equal(PatchDeploymentTargetState.Running, running.Targets.Single().State);

        handler.Results = [(action1Id, "Succeeded")];
        // The poll interval has passed for this row, which the service checks on PolledAt.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var row = await db.PatchDeployments.SingleAsync(d => d.Id == deployment.Id);
            row.PolledAt = _fixture.Now - PatchRules.PollInterval - TimeSpan.FromSeconds(1);
            await db.SaveChangesAsync();
        }

        await Service(handler).RunAsync(CancellationToken.None);

        var done = await ReadAsync(deployment.Id);
        Assert.Equal(PatchDeploymentState.Completed, done.State);
        Assert.Equal(PatchDeploymentTargetState.Succeeded, done.Targets.Single().State);
        Assert.NotNull(done.CompletedAt);

        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.Null((await read.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id)).PatchSyncedAt);
        // The end of a deployment is in the audit log, like the job it resembles.
        Assert.Contains(await read.AuditEntries.AsNoTracking().Where(a => a.TargetId == deployment.Id.ToString()).ToListAsync(),
            a => a.Action == AuditActions.PatchDeploymentEnded);
    }

    [Fact]
    public async Task A_status_the_product_words_differently_is_shown_as_unknown_instead_of_guessed()
    {
        var (client, _) = await SeedAsync("PD5" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, "DEPLOY-05");
        var deployment = await RequestAsync(client, endpoint, action1Id, state: PatchDeploymentState.Running,
            externalId: StubHandler.DeploymentId);
        var handler = new StubHandler { Results = [(action1Id, "Deferred by maintenance window")] };

        await Service(handler).RunAsync(CancellationToken.None);

        var stored = await ReadAsync(deployment.Id);
        Assert.Equal(PatchDeploymentTargetState.Unknown, stored.Targets.Single().State);
        Assert.Equal("Deferred by maintenance window", stored.Targets.Single().Message);
        // Unknown is not an end state: the deployment keeps being followed.
        Assert.Equal(PatchDeploymentState.Running, stored.State);
    }

    [Fact]
    public async Task A_deployment_the_product_never_finishes_is_abandoned_rather_than_followed_forever()
    {
        var (client, _) = await SeedAsync("PD6" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, "DEPLOY-06");
        var old = _fixture.Now - PatchRules.FollowFor - TimeSpan.FromHours(1);
        var deployment = await RequestAsync(client, endpoint, action1Id, requestedAt: old, state: PatchDeploymentState.Running,
            externalId: StubHandler.DeploymentId);
        var handler = new StubHandler { Results = [(action1Id, "Pending")] };

        await Service(handler).RunAsync(CancellationToken.None);

        var stored = await ReadAsync(deployment.Id);
        Assert.Equal(PatchDeploymentState.Abandoned, stored.State);
        Assert.Contains("Action1 console", stored.StatusMessage);
        Assert.Equal(PatchDeploymentTargetState.Unknown, stored.Targets.Single().State);
    }

    [Fact]
    public async Task The_history_Action1_keeps_per_endpoint_is_read_when_the_state_changes_and_replaced_as_a_whole()
    {
        var (client, _) = await SeedAsync("PD8" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var action1Id = Guid.NewGuid().ToString();
        var endpoint = await CreateEndpointAsync(client, "DEPLOY-08");
        var deployment = await RequestAsync(client, endpoint, action1Id, state: PatchDeploymentState.Running,
            externalId: StubHandler.DeploymentId);
        var targetId = deployment.Targets.Single().Id;
        var handler = new StubHandler
        {
            Results = [(action1Id, "Running")],
            Steps =
            [
                ("2026-09-24_13-27-00", "", "Pending", "Waiting for the endpoint to run the automation."),
                ("2026-09-24_13-29-10", "Deploy Update", "Success", "Starting the action.")
            ]
        };

        await Service(handler).RunAsync(CancellationToken.None);

        var read = Assert.Single(handler.StepReads);
        Assert.Contains($"/automations/instances/{Tenant}/{StubHandler.DeploymentId}/endpoint-results/{action1Id}/details", read);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var steps = await db.PatchDeploymentSteps.AsNoTracking().Where(s => s.TargetId == targetId).OrderBy(s => s.Position).ToListAsync();
            Assert.Equal(2, steps.Count);
            Assert.Equal(new DateTime(2026, 9, 24, 13, 29, 10, DateTimeKind.Utc), steps[1].Time);
            Assert.Equal(("Deploy Update", "Success", "Starting the action."), (steps[1].Operation, steps[1].Status, steps[1].Details));
            Assert.Equal(client.Id, steps[1].ClientId);
        }

        // Nothing changed and the refresh time has not passed: no second read.
        await MakePollDueAsync(deployment.Id);
        await Service(handler).RunAsync(CancellationToken.None);
        Assert.Single(handler.StepReads);

        // It finished: the history is read again and replaces the old lines.
        handler.Results = [(action1Id, "Success")];
        handler.Steps = [("2026-09-24_13-35-40", "Completed", "Success", "Automatic reboot was skipped due to configuration.")];
        await MakePollDueAsync(deployment.Id);
        await Service(handler).RunAsync(CancellationToken.None);

        Assert.Equal(2, handler.StepReads.Count);
        await using var check = _fixture.Db.DbFactory.CreateSystem();
        var final = await check.PatchDeploymentSteps.AsNoTracking().Where(s => s.TargetId == targetId).ToListAsync();
        Assert.Equal("Completed", Assert.Single(final).Operation);
        Assert.Equal(PatchDeploymentState.Completed, (await ReadAsync(deployment.Id)).State);
    }

    private async Task MakePollDueAsync(Guid deploymentId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var row = await db.PatchDeployments.SingleAsync(d => d.Id == deploymentId);
        row.PolledAt = _fixture.Now - PatchRules.PollInterval - TimeSpan.FromSeconds(1);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_deployment_is_not_left_running_when_the_integration_is_switched_off()
    {
        var (client, _) = await SeedAsync("PD7" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(), enabled: false);
        var endpoint = await CreateEndpointAsync(client, "DEPLOY-07");
        var deployment = await RequestAsync(client, endpoint, Guid.NewGuid().ToString());
        var handler = new StubHandler();

        await Service(handler).RunAsync(CancellationToken.None);

        Assert.Empty(handler.Started);
        var stored = await ReadAsync(deployment.Id);
        Assert.Equal(PatchDeploymentState.Failed, stored.State);
        Assert.Contains("Settings, Integrations", stored.StatusMessage);
    }

    private async Task<Endpoint> CreateEndpointAsync(Client client, string hostname)
    {
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Site " + Guid.NewGuid().ToString("N")[..6]);
        return await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, hostname);
    }

    /// <summary>Answers like Action1 does when a deployment is started and when its endpoint results are read.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        public const string DeploymentId = "instance-1";

        public List<(string Path, string Body)> Started { get; } = [];
        public IReadOnlyList<(string EndpointId, string Status)> Results { get; set; } = [];
        public IReadOnlyList<(string Time, string Action, string Status, string Description)> Steps { get; set; } = [];
        public List<string> StepReads { get; } = [];
        public HttpStatusCode StartStatus { get; set; } = HttpStatusCode.OK;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                return Json("""{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""");
            }

            if (request.Method == HttpMethod.Post)
            {
                Started.Add((path, await request.Content!.ReadAsStringAsync(cancellationToken)));
                return StartStatus == HttpStatusCode.OK
                    ? Json($$"""{"id":"{{DeploymentId}}","status":"Running"}""")
                    : new HttpResponseMessage(StartStatus)
                    {
                        Content = new StringContent("""{"user_message":"No access to this organization."}""", Encoding.UTF8, "application/json")
                    };
            }

            if (path.EndsWith("/details", StringComparison.Ordinal))
            {
                StepReads.Add(path);
                var steps = Steps.Select(s =>
                    $$"""{"time":"{{s.Time}}","action_name":"{{s.Action}}","status":"{{s.Status}}","description":"{{s.Description}}"}""");
                return Json($$"""{"items":[{{string.Join(",", steps)}}],"total_items":{{Steps.Count}}}""");
            }

            var items = Results.Select(r => $$"""{"endpoint_id":"{{r.EndpointId}}","status":"{{r.Status}}"}""");
            return Json($$"""{"items":[{{string.Join(",", items)}}],"total_items":{{Results.Count}}}""");
        }

        private static HttpResponseMessage Json(string body) =>
            new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
