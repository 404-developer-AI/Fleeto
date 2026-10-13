using Fleeto.Core.Entities;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Switching endpoints to managed through the web service returns the license problem of EndpointTierService verbatim
/// when the pool is exhausted, and changes nothing.
/// </summary>
[Collection(WebCollection.Name)]
public class TierSwitchTests
{
    private readonly WebFixture _fixture;

    public TierSwitchTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    [Fact]
    public async Task Switch_to_managed_shows_the_license_problem_when_the_pool_is_exhausted()
    {
        var db = _fixture.Database;
        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var first = await db.CreateEndpointAsync(site, hostname: "WS-ONE");
        var second = await db.CreateEndpointAsync(site, hostname: "WS-TWO");
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var caller = WebFixtureBase.Technician();

        int inUse;
        await using (var count = db.DbFactory.CreateSystem())
        {
            inUse = await count.Endpoints.CountAsync(e => e.Tier == EndpointTier.Managed);
        }

        // Exactly one free license.
        await db.LoadTestLicenseAsync(inUse + 1);

        var both = await endpoints.SetTierAsync(caller, [first.Id, second.Id], EndpointTier.Managed);
        Assert.False(both.Success);
        Assert.StartsWith("Not enough licenses:", both.Problem);
        Assert.Contains("Add 1 license", both.Problem);

        await using (var check = db.DbFactory.CreateSystem())
        {
            Assert.Equal(0, await check.Endpoints.CountAsync(e => (e.Id == first.Id || e.Id == second.Id) && e.Tier == EndpointTier.Managed));
        }

        var one = await endpoints.SetTierAsync(caller, [first.Id], EndpointTier.Managed);
        Assert.True(one.Success, one.Problem);

        var next = await endpoints.SetTierAsync(caller, [second.Id], EndpointTier.Managed);
        Assert.False(next.Success);
        Assert.StartsWith("Not enough licenses:", next.Problem);

        var back = await endpoints.SetTierAsync(caller, [first.Id], EndpointTier.AgentOnly);
        Assert.True(back.Success, back.Problem);
    }

    /// <summary>An endpoint of a client mapped to Action1, whose Fleeto agent reports an Action1 agent (0.6.0).</summary>
    private async Task<(Endpoint Endpoint, string Action1Id, string Tenant)> WithAction1AgentAsync(string hostname)
    {
        var db = _fixture.Database;
        await db.LoadTestLicenseAsync(1000);
        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var endpoint = await db.CreateEndpointAsync(site, EndpointTier.Managed, hostname);
        var action1Id = Guid.NewGuid().ToString();
        var tenant = "org-" + client.Code;
        var now = DateTime.UtcNow;
        await using var write = db.DbFactory.CreateSystem();
        var integration = await write.Integrations.FirstOrDefaultAsync(i => i.Type == IntegrationType.Action1);
        if (integration is null)
        {
            integration = new Integration
            {
                Id = Guid.NewGuid(), Type = IntegrationType.Action1, Region = Action1Region.Europe, EncryptedCredentials = "x",
                CredentialName = "api-key@action1.com", Enabled = true, CreatedAt = now, UpdatedAt = now
            };
            write.Integrations.Add(integration);
        }

        write.IntegrationMappings.Add(new IntegrationMapping
        {
            Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = client.Id, ExternalTenantId = tenant, ExternalTenantName = client.Code,
            CreatedAt = now
        });
        write.InventorySnapshots.Add(new InventorySnapshot
        {
            EndpointId = endpoint.Id, ClientId = client.Id, ReceivedAt = now, Hash = Guid.NewGuid().ToString("N"), Action1AgentId = action1Id
        });
        await write.SaveChangesAsync();
        return (endpoint, action1Id, tenant);
    }

    [Fact]
    public async Task Deleting_an_endpoint_can_ask_Action1_to_remove_its_agent_and_the_request_outlives_the_endpoint()
    {
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var caller = WebFixtureBase.Technician();
        var (endpoint, action1Id, tenant) = await WithAction1AgentAsync("WS-DELETE-A1");
        Assert.True(await endpoints.HasAction1AgentAsync(caller, endpoint.Id));
        var (plain, plainId, _) = await WithAction1AgentAsync("WS-DELETE-KEEP");

        Assert.True((await endpoints.DeleteAsync(caller, endpoint.Id, removeAction1Agent: true)).Success);
        Assert.True((await endpoints.DeleteAsync(caller, plain.Id)).Success);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await check.Endpoints.AnyAsync(e => e.Id == endpoint.Id));
        var operation = await check.IntegrationOperations.AsNoTracking()
            .SingleAsync(o => o.Kind == IntegrationOperationKind.RemoveEndpoint && o.ExternalEndpointId == action1Id);
        Assert.Equal((tenant, "WS-DELETE-A1"), (operation.ExternalTenantId, operation.Name));
        Assert.False(await check.IntegrationOperations.AnyAsync(o => o.ExternalEndpointId == plainId));
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == Fleeto.Core.Interfaces.AuditActions.PatchAgentRemovalRequested &&
                                                          a.TargetId == endpoint.Id.ToString()));
    }

    [Fact]
    public async Task Switching_to_agent_only_can_remove_the_Action1_agent_and_switching_to_managed_never_does()
    {
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var caller = WebFixtureBase.Technician();
        var (endpoint, action1Id, _) = await WithAction1AgentAsync("WS-AGENTONLY-A1");

        Assert.True((await endpoints.SetTierAsync(caller, [endpoint.Id], EndpointTier.Managed, removeAction1Agent: true)).Success);
        await using (var none = _fixture.Database.DbFactory.CreateSystem())
        {
            Assert.False(await none.IntegrationOperations.AnyAsync(o => o.ExternalEndpointId == action1Id));
        }

        Assert.True((await endpoints.SetTierAsync(caller, [endpoint.Id], EndpointTier.AgentOnly, removeAction1Agent: true)).Success);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.Equal(EndpointTier.AgentOnly, (await check.Endpoints.SingleAsync(e => e.Id == endpoint.Id)).Tier);
        Assert.True(await check.IntegrationOperations.AnyAsync(o => o.Kind == IntegrationOperationKind.RemoveEndpoint && o.ExternalEndpointId == action1Id));
    }

    private async Task<string> CertificateAsync(Endpoint endpoint, AgentComponent role = AgentComponent.Agent, DateTime? revokedAt = null)
    {
        var fingerprint = Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.AgentCertificates.Add(new AgentCertificate
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Fingerprint = fingerprint, PublicKeyFingerprint = fingerprint,
            SerialNumber = fingerprint[..16], IssuedAt = DateTime.UtcNow, ExpiresAt = DateTime.UtcNow.AddDays(90), RevokedAt = revokedAt, Role = role
        });
        await db.SaveChangesAsync();
        return fingerprint;
    }

    [Fact]
    public async Task Deleting_an_endpoint_can_leave_an_uninstall_for_its_agent_and_watchdog_that_outlives_it()
    {
        // 0.6.0: the endpoint goes at once; what stays are its certificates to recognise and the request to sign the command.
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var caller = WebFixtureBase.Technician();
        var (endpoint, _, _) = await WithAction1AgentAsync("WS-UNINSTALL");
        var agent = await CertificateAsync(endpoint);
        var watchdog = await CertificateAsync(endpoint, AgentComponent.Watchdog);
        var revoked = await CertificateAsync(endpoint, revokedAt: DateTime.UtcNow.AddDays(-1));

        Assert.True((await endpoints.DeleteAsync(caller, endpoint.Id, uninstallAgent: true)).Success);

        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.False(await check.Endpoints.AnyAsync(e => e.Id == endpoint.Id));
        var uninstall = await check.AgentUninstalls.AsNoTracking().SingleAsync(u => u.EndpointId == endpoint.Id);
        Assert.Equal(new[] { agent, watchdog }.Order(), uninstall.Fingerprints.Order());
        Assert.DoesNotContain(revoked, uninstall.Fingerprints);
        Assert.Equal(uninstall.RequestedAt + AgentUninstallRules.Lifetime, uninstall.ExpiresAt);
        var request = await check.SigningRequests.AsNoTracking().SingleAsync(r => r.SubjectId == uninstall.Id);
        Assert.Equal(SigningRequestKind.AgentUninstall, request.Kind);
        Assert.Equal(AgentUninstallRules.Binding(endpoint.Id, caller.UserId), request.Payload);
    }

    [Fact]
    public async Task Deleting_a_client_can_uninstall_the_agents_of_all_of_its_endpoints()
    {
        var db = _fixture.Database;
        var client = await db.CreateClientAsync();
        var site = await db.CreateSiteAsync(client.Id);
        var first = await db.CreateEndpointAsync(site, hostname: "WS-CLIENT-1");
        var second = await db.CreateEndpointAsync(site, hostname: "WS-CLIENT-2");
        await CertificateAsync(first);
        await CertificateAsync(second);

        var clients = _fixture.Services.GetRequiredService<ClientService>();
        Assert.True((await clients.DeleteAsync(WebFixtureBase.Technician(), client.Id, client.Code, uninstallAgents: true)).Success);

        await using var check = db.DbFactory.CreateSystem();
        Assert.Equal(2, await check.AgentUninstalls.CountAsync(u => u.ClientId == client.Id));
        Assert.False(await check.Clients.AnyAsync(c => c.Id == client.Id));
    }

    [Fact]
    public async Task An_admin_sees_the_agents_waiting_to_be_uninstalled_and_can_stop_waiting()
    {
        var endpoints = _fixture.Services.GetRequiredService<EndpointService>();
        var uninstalls = _fixture.Services.GetRequiredService<AgentUninstallService>();
        var (endpoint, _, _) = await WithAction1AgentAsync("WS-WAITING");
        await CertificateAsync(endpoint);
        Assert.True((await endpoints.DeleteAsync(WebFixtureBase.Admin(), endpoint.Id, uninstallAgent: true)).Success);

        var listed = (await uninstalls.ListAsync(WebFixtureBase.Admin())).Single(u => u.Hostname == "WS-WAITING");
        Assert.True(listed.IsWaiting);
        await Assert.ThrowsAnyAsync<Exception>(() => uninstalls.ListAsync(WebFixtureBase.Technician()));

        Assert.True((await uninstalls.DismissAsync(WebFixtureBase.Admin(), listed.Id)).Success);
        Assert.DoesNotContain(await uninstalls.ListAsync(WebFixtureBase.Admin()), u => u.Id == listed.Id);
        await using var check = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await check.AuditEntries.AnyAsync(a => a.Action == Fleeto.Core.Interfaces.AuditActions.AgentUninstallDismissed &&
                                                          a.TargetId == endpoint.Id.ToString()));
    }
}
