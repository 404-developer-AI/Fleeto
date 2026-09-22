using System.Net;
using System.Text;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Infrastructure.Integrations.Action1;
using Fleeto.Workers.Hosting;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// Guarantees of the integration sync (0.4.0): the workers are the only containers that can reach an external product,
/// so a connection test an admin asked for is run here, its result and the organizations are written back, and a failure
/// leaves a message the admin can act on instead of an empty screen.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class IntegrationSyncTests
{
    private readonly WorkersFixture _fixture;

    public IntegrationSyncTests(WorkersFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Integration> SeedAsync(bool requested = true, bool enabled = true)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
        await db.SaveChangesAsync();

        var integration = new Integration
        {
            Id = Guid.NewGuid(),
            Type = IntegrationType.Action1,
            Region = Action1Region.Europe,
            CredentialName = "api-key-sync@action1.com",
            Enabled = enabled,
            SyncRequestedAt = requested ? _fixture.Now : null,
            CreatedAt = _fixture.Now,
            UpdatedAt = _fixture.Now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Db.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "sync-secret"));
        db.Integrations.Add(integration);
        await db.SaveChangesAsync();
        return integration;
    }

    private IntegrationSyncService Service(StubHandler handler) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus,
            new Action1ClientFactory(_fixture.Db.SecretProtector, _fixture.Db.Time, NullLoggerFactory.Instance,
                IntegrationBudgets.WorkerRequestsPerMinute, () => handler),
            _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<IntegrationSyncService>.Instance);

    private async Task<Integration> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.Integrations.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    [Fact]
    public async Task A_requested_test_is_run_and_its_organizations_are_stored()
    {
        var integration = await SeedAsync();
        var handler = new StubHandler();

        await Service(handler).SyncAsync(CancellationToken.None);

        var stored = await ReadAsync(integration.Id);
        Assert.Equal(IntegrationStatus.Ok, stored.Status);
        Assert.Null(stored.StatusMessage);
        Assert.Null(stored.SyncRequestedAt);
        // PostgreSQL keeps microseconds, so compare the moment, not the tick.
        Assert.NotNull(stored.LastSuccessAt);
        Assert.True((stored.LastSuccessAt!.Value - _fixture.Now).Duration() < TimeSpan.FromSeconds(1));
        Assert.Contains("Contoso", stored.TenantsJson);
        Assert.NotNull(stored.TenantsUpdatedAt);
    }

    [Fact]
    public async Task A_failure_leaves_the_message_of_the_product_and_marks_the_integration_failing()
    {
        var integration = await SeedAsync();
        var handler = new StubHandler { TokenStatus = HttpStatusCode.Unauthorized };

        await Service(handler).SyncAsync(CancellationToken.None);

        var stored = await ReadAsync(integration.Id);
        Assert.Equal(IntegrationStatus.Failing, stored.Status);
        Assert.Contains("Settings, Integrations", stored.StatusMessage);
        Assert.Null(stored.SyncRequestedAt);
        Assert.Null(stored.LastSuccessAt);
        Assert.NotNull(stored.LastAttemptAt);
    }

    [Fact]
    public async Task The_name_of_a_mapped_organization_follows_what_the_product_calls_it()
    {
        var integration = await SeedAsync();
        var client = await _fixture.Db.CreateClientAsync("SYN" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant());
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                Id = Guid.NewGuid(),
                IntegrationId = integration.Id,
                ClientId = client.Id,
                ExternalTenantId = "org-1",
                ExternalTenantName = "The old name",
                CreatedAt = _fixture.Now
            });
            await db.SaveChangesAsync();
        }

        await Service(new StubHandler()).SyncAsync(CancellationToken.None);

        await using var read = _fixture.Db.DbFactory.CreateSystem();
        var mapping = await read.IntegrationMappings.AsNoTracking().SingleAsync(m => m.IntegrationId == integration.Id);
        Assert.Equal("Contoso", mapping.ExternalTenantName);
    }

    [Fact]
    public async Task An_integration_that_is_switched_off_is_left_alone_unless_an_admin_asks()
    {
        var integration = await SeedAsync(requested: false, enabled: false);
        var handler = new StubHandler();

        await Service(handler).SyncAsync(CancellationToken.None);

        Assert.Equal(0, handler.Calls);
        var stored = await ReadAsync(integration.Id);
        Assert.Equal(IntegrationStatus.Unknown, stored.Status);
        Assert.Null(stored.TenantsUpdatedAt);
    }

    [Fact]
    public async Task Nothing_is_asked_of_the_product_when_the_organizations_are_still_fresh()
    {
        var integration = await SeedAsync(requested: false);
        await Service(new StubHandler()).SyncAsync(CancellationToken.None);
        var handler = new StubHandler();

        // The first pass read them; a second pass right after does not ask again.
        await Service(handler).SyncAsync(CancellationToken.None);

        Assert.Equal(0, handler.Calls);
        Assert.Equal(IntegrationStatus.Ok, (await ReadAsync(integration.Id)).Status);
    }

    /// <summary>Answers like Action1 does, and counts what was asked.</summary>
    [Fact]
    public async Task The_agent_installer_link_is_read_where_the_product_offers_it_and_a_pasted_one_is_kept()
    {
        var integration = await SeedAsync();
        const string url = "https://app.eu.action1.com/agent/9f2c/Windows/agent(Contoso).msi";
        var client = await _fixture.Db.CreateClientAsync("AIL" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        var pasted = await _fixture.Db.CreateClientAsync("AIP" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant());
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = client.Id, ExternalTenantId = "org-1",
                ExternalTenantName = "Contoso", CreatedAt = _fixture.Now
            });
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                Id = Guid.NewGuid(), IntegrationId = integration.Id, ClientId = pasted.Id, ExternalTenantId = "org-2",
                ExternalTenantName = "Fabrikam", AgentInstallerUrl = "https://app.eu.action1.com/agent/typed/Windows/agent.msi",
                CreatedAt = _fixture.Now
            });
            await db.SaveChangesAsync();
        }

        await Service(new StubHandler { AgentInstaller = url }).SyncAsync(CancellationToken.None);

        await using (var read = _fixture.Db.DbFactory.CreateSystem())
        {
            var mappings = await read.IntegrationMappings.AsNoTracking().Where(m => m.IntegrationId == integration.Id).ToListAsync();
            Assert.Equal(url, mappings.Single(m => m.ClientId == client.Id).AgentInstallerUrl);
            Assert.All(mappings, m => Assert.NotNull(m.AgentInstallerReadAt));
        }

        // The product stops offering a link: what is stored stays, so an admin's own link is never thrown away.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            foreach (var mapping in await db.IntegrationMappings.Where(m => m.IntegrationId == integration.Id).ToListAsync())
            {
                mapping.AgentInstallerReadAt = _fixture.Now - IntegrationSyncService.AgentInstallerInterval - TimeSpan.FromMinutes(1);
            }

            var row = await db.Integrations.SingleAsync(i => i.Id == integration.Id);
            row.SyncRequestedAt = _fixture.Now;
            await db.SaveChangesAsync();
        }

        await Service(new StubHandler()).SyncAsync(CancellationToken.None);

        await using var after = _fixture.Db.DbFactory.CreateSystem();
        var kept = await after.IntegrationMappings.AsNoTracking().Where(m => m.IntegrationId == integration.Id).ToListAsync();
        Assert.Equal(url, kept.Single(m => m.ClientId == client.Id).AgentInstallerUrl);
        Assert.Equal("https://app.eu.action1.com/agent/typed/Windows/agent.msi", kept.Single(m => m.ClientId == pasted.Id).AgentInstallerUrl);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public HttpStatusCode TokenStatus { get; init; } = HttpStatusCode.OK;

        /// <summary>The Windows installer link Action1 hands out, or null when it does not offer one.</summary>
        public string? AgentInstaller { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            if (request.RequestUri!.AbsolutePath.EndsWith("/oauth2/token", StringComparison.Ordinal))
            {
                return Task.FromResult(TokenStatus == HttpStatusCode.OK
                    ? Json(HttpStatusCode.OK, """{"access_token":"token","expires_in":3600,"token_type":"Bearer"}""")
                    : Json(TokenStatus, """{"user_message":"refused"}"""));
            }

            if (request.RequestUri.AbsolutePath.Contains("/agent-installation/", StringComparison.Ordinal))
            {
                // Action1 offers a link for the first organization only, as it does for an organization whose agent
                // deployment has not been set up.
                return Task.FromResult(AgentInstaller is null || !request.RequestUri.AbsolutePath.EndsWith("org-1", StringComparison.Ordinal)
                    ? Json(HttpStatusCode.NotFound, """{"user_message":"unknown"}""")
                    : Json(HttpStatusCode.OK, $$"""{"platforms":[{"platform":"Windows","url":"{{AgentInstaller}}"}]}"""));
            }

            return Task.FromResult(Json(HttpStatusCode.OK,
                """{"items":[{"id":"org-1","name":"Contoso"},{"id":"org-2","name":"Fabrikam"}],"total_items":2}"""));
        }

        private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
            new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }
}
