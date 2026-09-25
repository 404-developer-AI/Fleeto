using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees of following clients and sites in Action1 (0.6.0): a new client gets an organization or the unmapped one with
/// its name, never a second one; sites become endpoint groups whose members are the endpoints of the site; renames and
/// deletions go along; a refusal is kept with its reason instead of being dropped; and nothing is asked of Action1 while the
/// integration does not follow clients.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class IntegrationFollowTests
{
    private readonly WorkersFixture _fixture;

    public IntegrationFollowTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Integration> SeedAsync(bool follow = true)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
        await db.SaveChangesAsync();

        var integration = new Integration
        {
            Id = Guid.NewGuid(),
            Type = IntegrationType.Action1,
            Region = Action1Region.Europe,
            CredentialName = "api-key-follow@action1.com",
            Enabled = true,
            FollowClients = follow,
            CreatedAt = _fixture.Now,
            UpdatedAt = _fixture.Now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Db.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "follow-secret"));
        db.Integrations.Add(integration);
        await db.SaveChangesAsync();
        return integration;
    }

    private IntegrationFollowService Service(FakeAction1 action1) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus,
            new Action1ClientFactory(_fixture.Db.SecretProtector, _fixture.Db.Time, NullLoggerFactory.Instance,
                IntegrationBudgets.WorkerRequestsPerMinute, () => action1),
            _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<IntegrationFollowService>.Instance);

    private async Task<Client> CreateClientAsync(string name)
    {
        var client = await _fixture.Db.CreateClientAsync();
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var row = await db.Clients.SingleAsync(c => c.Id == client.Id);
        row.Name = name;
        await db.SaveChangesAsync();
        client.Name = name;
        return client;
    }

    private async Task AddOperationAsync(Integration integration, IntegrationOperationKind kind, Guid? clientId, string tenantId = "",
        string groupId = "", string name = "Name")
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.IntegrationOperations.Add(new IntegrationOperation
        {
            Id = Guid.NewGuid(),
            IntegrationId = integration.Id,
            Kind = kind,
            TargetClientId = clientId,
            ExternalTenantId = tenantId,
            ExternalGroupId = groupId,
            Name = name,
            NextAttemptAt = _fixture.Now,
            CreatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
    }

    private async Task MapAsync(Integration integration, Client client, string tenantId, string name)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(),
            IntegrationId = integration.Id,
            ClientId = client.Id,
            ExternalTenantId = tenantId,
            ExternalTenantName = name,
            SyncedName = client.Name,
            CreatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
    }

    /// <summary>A managed endpoint on the site that Action1 reports as <paramref name="action1Id"/> in <paramref name="tenantId"/>.</summary>
    private async Task<Endpoint> ReportedEndpointAsync(Site site, string tenantId, string action1Id)
    {
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "FOLLOW-" + action1Id);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.EndpointPatchStates.Add(new EndpointPatchState
        {
            EndpointId = endpoint.Id,
            ClientId = site.ClientId,
            ExternalEndpointId = action1Id,
            ExternalTenantId = tenantId,
            UpdatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
        return endpoint;
    }

    [Fact]
    public async Task A_new_client_gets_an_organization_and_its_sites_get_groups_with_their_endpoints()
    {
        var integration = await SeedAsync();
        var client = await CreateClientAsync("Northwind Traders");
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Head office");
        await AddOperationAsync(integration, IntegrationOperationKind.CreateTenant, client.Id, name: client.Name);
        var action1 = new FakeAction1();

        await Service(action1).FollowAsync(CancellationToken.None);

        var organization = Assert.Single(action1.Organizations, o => o.Value == $"[{client.Code}] Northwind Traders");
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var mapping = await db.IntegrationMappings.AsNoTracking().SingleAsync(m => m.ClientId == client.Id);
            Assert.Equal(organization.Key, mapping.ExternalTenantId);
            Assert.Equal($"[{client.Code}] Northwind Traders", mapping.SyncedName);
            Assert.False(await db.IntegrationOperations.AnyAsync(o => o.IntegrationId == integration.Id));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.IntegrationTenantCreated && a.ClientId == client.Id));
            Assert.Contains($"[{client.Code}] Northwind Traders", (await db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id)).TenantsJson);

            var group = await db.IntegrationSiteGroups.AsNoTracking().SingleAsync(g => g.SiteId == site.Id);
            Assert.Equal(organization.Key, group.ExternalTenantId);
            Assert.Equal("Head office", action1.Groups[group.ExternalGroupId].Name);
        }

        // Action1 now reports an endpoint of the site: the next pass puts it in the group.
        await ReportedEndpointAsync(site, organization.Key, "a1-endpoint-1");
        await Service(action1).FollowAsync(CancellationToken.None);

        await using var read = _fixture.Db.DbFactory.CreateSystem();
        var followed = await read.IntegrationSiteGroups.AsNoTracking().SingleAsync(g => g.SiteId == site.Id);
        Assert.Equal(["a1-endpoint-1"], action1.Groups[followed.ExternalGroupId].Members.Keys);
    }

    [Fact]
    public async Task An_unmapped_organization_with_the_client_name_is_mapped_and_gets_the_client_code()
    {
        var integration = await SeedAsync();
        var client = await CreateClientAsync("Fabrikam");
        await AddOperationAsync(integration, IntegrationOperationKind.CreateTenant, client.Id, name: client.Name);
        var action1 = new FakeAction1();
        action1.Organizations["org-existing"] = "fabrikam";

        await Service(action1).FollowAsync(CancellationToken.None);

        Assert.Equal(0, action1.OrganizationsCreated);
        Assert.Equal($"[{client.Code}] Fabrikam", action1.Organizations["org-existing"]);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Equal("org-existing", (await db.IntegrationMappings.AsNoTracking().SingleAsync(m => m.ClientId == client.Id)).ExternalTenantId);
    }

    [Fact]
    public async Task An_organization_that_already_carries_the_client_code_is_preferred_and_not_renamed()
    {
        var integration = await SeedAsync();
        var client = await CreateClientAsync("Tailspin");
        await AddOperationAsync(integration, IntegrationOperationKind.CreateTenant, client.Id, name: client.Name);
        var action1 = new FakeAction1();
        action1.Organizations["org-plain"] = "Tailspin";
        action1.Organizations["org-coded"] = $"[{client.Code}] Tailspin";

        await Service(action1).FollowAsync(CancellationToken.None);
        var calls = action1.Calls;
        await Service(action1).FollowAsync(CancellationToken.None);

        Assert.Equal(0, action1.OrganizationsCreated);
        Assert.Equal("Tailspin", action1.Organizations["org-plain"]);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.Equal("org-coded", (await db.IntegrationMappings.AsNoTracking().SingleAsync(m => m.ClientId == client.Id)).ExternalTenantId);
        Assert.True(action1.Calls - calls <= 1, "A second pass renames nothing.");
    }

    [Fact]
    public async Task Renaming_a_client_or_a_site_renames_its_organization_and_group()
    {
        var integration = await SeedAsync();
        var client = await CreateClientAsync("Contoso");
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Branch");
        var action1 = new FakeAction1();
        action1.Organizations["org-contoso"] = "Contoso";
        await MapAsync(integration, client, "org-contoso", "Contoso");
        await Service(action1).FollowAsync(CancellationToken.None);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            (await db.Clients.SingleAsync(c => c.Id == client.Id)).Name = "Contoso Ltd";
            (await db.Sites.SingleAsync(s => s.Id == site.Id)).Name = "Branch Gent";
            await db.SaveChangesAsync();
        }

        await Service(action1).FollowAsync(CancellationToken.None);

        Assert.Equal($"[{client.Code}] Contoso Ltd", action1.Organizations["org-contoso"]);
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        var group = await read.IntegrationSiteGroups.AsNoTracking().SingleAsync(g => g.SiteId == site.Id);
        Assert.Equal("Branch Gent", action1.Groups[group.ExternalGroupId].Name);
        Assert.Equal($"[{client.Code}] Contoso Ltd", (await read.IntegrationMappings.AsNoTracking().SingleAsync(m => m.ClientId == client.Id)).SyncedName);
    }

    [Fact]
    public async Task Members_follow_the_site_and_members_brought_in_by_filters_are_left_alone()
    {
        var integration = await SeedAsync();
        var client = await CreateClientAsync("Members");
        var site = await _fixture.Db.CreateSiteAsync(client.Id, "Office");
        var other = await _fixture.Db.CreateSiteAsync(client.Id, "Warehouse");
        var action1 = new FakeAction1();
        action1.Organizations["org-members"] = "Members";
        await MapAsync(integration, client, "org-members", "Members");
        var moving = await ReportedEndpointAsync(site, "org-members", "a1-moving");
        await ReportedEndpointAsync(site, "org-members", "a1-staying");
        await Service(action1).FollowAsync(CancellationToken.None);

        int officeGroup;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var group = await db.IntegrationSiteGroups.AsNoTracking().SingleAsync(g => g.SiteId == site.Id);
            officeGroup = int.Parse(group.ExternalGroupId.Split('-')[1]);
            Assert.Equal(["a1-moving", "a1-staying"], action1.Groups[group.ExternalGroupId].Members.Keys.Order());
            action1.Groups[group.ExternalGroupId].Members["a1-by-filter"] = false;

            // The endpoint moves to the other site.
            (await db.Endpoints.SingleAsync(e => e.Id == moving.Id)).SiteId = other.Id;
            await db.SaveChangesAsync();
        }

        await Service(action1).FollowAsync(CancellationToken.None);

        var office = action1.Groups[$"group-{officeGroup}"];
        Assert.Equal(["a1-by-filter", "a1-staying"], office.Members.Keys.Order());
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        var warehouse = await read.IntegrationSiteGroups.AsNoTracking().SingleAsync(g => g.SiteId == other.Id);
        Assert.Equal(["a1-moving"], action1.Groups[warehouse.ExternalGroupId].Members.Keys);
    }

    [Fact]
    public async Task A_refused_deletion_is_kept_with_its_reason_and_tried_again_later()
    {
        var integration = await SeedAsync();
        var action1 = new FakeAction1 { RefuseDeletion = true };
        action1.Organizations["org-gone"] = "Old client";
        await AddOperationAsync(integration, IntegrationOperationKind.DeleteTenant, null, "org-gone", name: "Old client");

        await Service(action1).FollowAsync(CancellationToken.None);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var operation = await db.IntegrationOperations.AsNoTracking().SingleAsync(o => o.IntegrationId == integration.Id);
            Assert.Equal(1, operation.Attempts);
            Assert.Contains("still contains endpoints", operation.LastError);
            Assert.Contains("6 hours", operation.LastError);
            Assert.True(operation.NextAttemptAt >= _fixture.Now + IntegrationFollowService.RefusedRetry - TimeSpan.FromSeconds(1));
        }

        Assert.True(action1.Organizations.ContainsKey("org-gone"));

        // Once the endpoints are gone, Action1 removes it.
        action1.RefuseDeletion = false;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            (await db.IntegrationOperations.SingleAsync(o => o.IntegrationId == integration.Id)).NextAttemptAt = _fixture.Now;
            await db.SaveChangesAsync();
        }

        await Service(action1).FollowAsync(CancellationToken.None);

        Assert.False(action1.Organizations.ContainsKey("org-gone"));
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await read.IntegrationOperations.AnyAsync(o => o.IntegrationId == integration.Id));
        Assert.True(await read.AuditEntries.AnyAsync(a => a.Action == AuditActions.IntegrationTenantDeleted && a.TargetId == integration.Id.ToString()));
    }

    [Fact]
    public async Task An_organization_mapped_to_another_client_since_is_never_deleted()
    {
        var integration = await SeedAsync();
        var client = await CreateClientAsync("Took it over");
        var action1 = new FakeAction1();
        action1.Organizations["org-reused"] = "Reused";
        await MapAsync(integration, client, "org-reused", "Reused");
        await AddOperationAsync(integration, IntegrationOperationKind.DeleteTenant, null, "org-reused", name: "Reused");

        await Service(action1).FollowAsync(CancellationToken.None);

        Assert.True(action1.Organizations.ContainsKey("org-reused"));
        Assert.Equal(0, action1.Deletions);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await db.IntegrationOperations.AnyAsync(o => o.IntegrationId == integration.Id));
    }

    [Fact]
    public async Task Nothing_is_asked_of_Action1_while_the_integration_does_not_follow_clients()
    {
        var integration = await SeedAsync(follow: false);
        var client = await CreateClientAsync("Not followed");
        await AddOperationAsync(integration, IntegrationOperationKind.CreateTenant, client.Id);
        var action1 = new FakeAction1();

        await Service(action1).FollowAsync(CancellationToken.None);

        Assert.Equal(0, action1.Calls);
    }

    [Fact]
    public async Task Credentials_that_may_not_manage_organizations_leave_a_message_for_the_admin()
    {
        var integration = await SeedAsync();
        var client = await CreateClientAsync("Forbidden");
        await _fixture.Db.CreateSiteAsync(client.Id, "Office");
        var action1 = new FakeAction1 { ForbidChanges = true };
        action1.Organizations["org-forbidden"] = "Forbidden";
        await MapAsync(integration, client, "org-forbidden", "Forbidden");

        await Service(action1).FollowAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var stored = await db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id);
        Assert.Contains("manage organizations and endpoints", stored.FollowMessage);
    }

    [Fact]
    public async Task An_endpoint_is_moved_to_the_organization_of_its_client_and_the_patch_state_is_read_again()
    {
        var integration = await SeedAsync();
        var client = await CreateClientAsync("Moved to");
        var action1 = new FakeAction1();
        action1.Organizations["org-old"] = "Old";
        action1.Organizations["org-target"] = "Moved to";
        action1.Endpoints["a1-moving"] = "org-old";
        await MapAsync(integration, client, "org-target", "Moved to");
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            (await db.Integrations.SingleAsync(i => i.Id == integration.Id)).PatchSyncedAt = _fixture.Now;
            db.IntegrationOperations.Add(new IntegrationOperation
            {
                Id = Guid.NewGuid(), IntegrationId = integration.Id, Kind = IntegrationOperationKind.MoveEndpoint, TargetClientId = client.Id,
                ExternalTenantId = "org-old", ExternalEndpointId = "a1-moving", Name = "MOVING-01", NextAttemptAt = _fixture.Now,
                CreatedAt = _fixture.Now
            });
            await db.SaveChangesAsync();
        }

        await Service(action1).FollowAsync(CancellationToken.None);

        Assert.Equal("org-target", action1.Endpoints["a1-moving"]);
        await using var read = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await read.IntegrationOperations.AnyAsync(o => o.IntegrationId == integration.Id));
        Assert.Null((await read.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id)).PatchSyncedAt);
        Assert.True(await read.AuditEntries.AnyAsync(a => a.Action == AuditActions.IntegrationEndpointMoved && a.ClientId == client.Id));
    }

    /// <summary>An Action1 enterprise in memory: organizations, endpoint groups and their members, as its API answers.</summary>
    private sealed class FakeAction1 : HttpMessageHandler
    {
        private int _next;

        public Dictionary<string, string> Organizations { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, FakeGroup> Groups { get; } = new(StringComparer.Ordinal);

        /// <summary>Endpoint id and the organization that holds it.</summary>
        public Dictionary<string, string> Endpoints { get; } = new(StringComparer.Ordinal);
        public int Calls { get; private set; }
        public int OrganizationsCreated { get; private set; }
        public int Deletions { get; private set; }
        public bool RefuseDeletion { get; set; }
        public bool ForbidChanges { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var path = request.RequestUri!.AbsolutePath;
            var parts = path[(path.IndexOf("/api/3.0/", StringComparison.Ordinal) + "/api/3.0/".Length)..].Split('/');
            if (parts[0] == "oauth2")
            {
                return Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600}""");
            }

            var body = request.Content is null ? null : JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken));

            if (ForbidChanges && request.Method != HttpMethod.Get)
            {
                return Json(HttpStatusCode.Forbidden, """{"user_message":"Forbidden"}""");
            }

            if (parts[0] == "organizations")
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Page(Organizations.Select(o => new { id = o.Key, name = o.Value }));
                }

                if (request.Method == HttpMethod.Post)
                {
                    OrganizationsCreated++;
                    var id = $"org-{++_next}";
                    Organizations[id] = (string)body!["name"]!;
                    return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id, name = Organizations[id] }));
                }

                if (request.Method == HttpMethod.Patch)
                {
                    Organizations[parts[1]] = (string)body!["name"]!;
                    return Json(HttpStatusCode.OK, "{}");
                }

                Deletions++;
                if (RefuseDeletion)
                {
                    return Json(HttpStatusCode.BadRequest, """{"user_message":"The organization still contains endpoints."}""");
                }

                return Organizations.Remove(parts[1]) ? new HttpResponseMessage(HttpStatusCode.OK) : Json(HttpStatusCode.NotFound, "{}");
            }

            // endpoints/managed/{org}/{endpoint}/move
            if (parts[1] == "managed")
            {
                if (!Endpoints.TryGetValue(parts[3], out var holder) || holder != parts[2])
                {
                    return Json(HttpStatusCode.NotFound, """{"user_message":"Not found"}""");
                }

                Endpoints[parts[3]] = (string)body!["target_organization_id"]!;
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id = parts[3] }));
            }

            // endpoints/groups/{org}[/{group}[/contents]]
            var org = parts[2];
            if (parts.Length == 3)
            {
                if (request.Method == HttpMethod.Get)
                {
                    return Page(Groups.Where(g => g.Value.Organization == org).Select(g => new { id = g.Key, name = g.Value.Name }));
                }

                var id = $"group-{++_next}";
                Groups[id] = new FakeGroup(org, (string)body!["name"]!);
                return Json(HttpStatusCode.OK, JsonSerializer.Serialize(new { id }));
            }

            if (!Groups.TryGetValue(parts[3], out var group))
            {
                return Json(HttpStatusCode.NotFound, """{"user_message":"Not found"}""");
            }

            if (parts.Length == 4)
            {
                if (request.Method == HttpMethod.Patch)
                {
                    group.Name = (string)body!["name"]!;
                }
                else if (request.Method == HttpMethod.Delete)
                {
                    Groups.Remove(parts[3]);
                }

                return Json(HttpStatusCode.OK, "{}");
            }

            if (request.Method == HttpMethod.Get)
            {
                return Page(group.Members.Select(m => new { id = m.Key, added_via = m.Value ? "manual" : "criteria" }));
            }

            foreach (var change in body!.AsArray())
            {
                if ((string)change!["method"]! == "POST")
                {
                    group.Members[(string)change["data"]!["endpoint_id"]!] = true;
                }
                else
                {
                    group.Members.Remove((string)change["endpoint_id"]!);
                }
            }

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

    private sealed class FakeGroup(string organization, string name)
    {
        public string Organization { get; } = organization;
        public string Name { get; set; } = name;

        /// <summary>Endpoint id and whether it was added by hand (true) or by the filters of the group (false).</summary>
        public Dictionary<string, bool> Members { get; } = new(StringComparer.Ordinal);
    }
}
