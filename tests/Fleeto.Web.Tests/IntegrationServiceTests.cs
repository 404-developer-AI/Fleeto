using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Integrations;
using Fleeto.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// Guarantees of the Action1 integration in web (0.4.0): only admins manage it; the client secret is write-only and a
/// blank one keeps what is stored; an organization belongs to one client and a client to one organization; and the audit
/// log records what changed without ever holding the secret.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class IntegrationServiceTests
{
    private const string Secret = "n0tinanauditentry-4c5d6e";
    private readonly WebFixture _fixture;

    public IntegrationServiceTests(WebFixture fixture)
    {
        _fixture = fixture;
    }

    private IntegrationService Integrations => _fixture.Services.GetRequiredService<IntegrationService>();

    private static Action1Input Input(string clientId = "api-key-1@action1.com", string? secret = Secret,
        Action1Region region = Action1Region.Europe, bool enabled = true) =>
        new(clientId, secret, region, enabled);

    private async Task ResetAsync()
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var existing = await db.Integrations.ToListAsync();
        db.Integrations.RemoveRange(existing);
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Only_admins_manage_the_Action1_integration()
    {
        await ResetAsync();

        var saved = await Integrations.SaveAction1Async(WebFixture.Technician(), Input());

        Assert.False(saved.Success);
        await Assert.ThrowsAnyAsync<Exception>(() => Integrations.GetAction1Async(WebFixture.Technician()));
    }

    [Fact]
    public async Task The_client_secret_is_stored_encrypted_and_never_returned_or_audited()
    {
        await ResetAsync();

        Assert.True((await Integrations.SaveAction1Async(WebFixture.Admin(), Input())).Success);

        var view = await Integrations.GetAction1Async(WebFixture.Admin());
        Assert.NotNull(view);
        Assert.Equal("api-key-1@action1.com", view!.CredentialName);
        Assert.Equal(Action1Region.Europe, view.Region);
        Assert.Equal(IntegrationStatus.Unknown, view.Status);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var row = await db.Integrations.AsNoTracking().SingleAsync(i => i.Type == IntegrationType.Action1);
        Assert.DoesNotContain(Secret, row.EncryptedCredentials);
        var credentials = IntegrationCredentials.Unprotect(_fixture.Database.SecretProtector, row.Id, row.EncryptedCredentials);
        Assert.Equal(Secret, credentials.ClientSecret);

        var audit = await db.AuditEntries.AsNoTracking().Where(a => a.TargetType == "Integration")
            .OrderByDescending(a => a.Id).FirstAsync();
        Assert.DoesNotContain(Secret, audit.DetailsJson);
        Assert.Contains("secretChanged", audit.DetailsJson);
    }

    [Fact]
    public async Task A_blank_secret_keeps_the_stored_one_while_the_client_id_and_region_change()
    {
        await ResetAsync();
        Assert.True((await Integrations.SaveAction1Async(WebFixture.Admin(), Input())).Success);

        Assert.True((await Integrations.SaveAction1Async(WebFixture.Admin(),
            Input("api-key-2@action1.com", secret: "  ", region: Action1Region.NorthAmerica))).Success);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var row = await db.Integrations.AsNoTracking().SingleAsync(i => i.Type == IntegrationType.Action1);
        var credentials = IntegrationCredentials.Unprotect(_fixture.Database.SecretProtector, row.Id, row.EncryptedCredentials);
        Assert.Equal(Secret, credentials.ClientSecret);
        Assert.Equal("api-key-2@action1.com", credentials.ClientId);
        Assert.Equal(Action1Region.NorthAmerica, row.Region);
    }

    [Fact]
    public async Task A_first_save_without_a_secret_says_what_to_enter()
    {
        await ResetAsync();

        var result = await Integrations.SaveAction1Async(WebFixture.Admin(), Input(secret: null));

        Assert.False(result.Success);
        Assert.Contains("client secret", result.Problem);
    }

    [Fact]
    public async Task One_organization_belongs_to_one_client_and_one_client_to_one_organization()
    {
        await ResetAsync();
        Assert.True((await Integrations.SaveAction1Async(WebFixture.Admin(), Input())).Success);
        var first = await _fixture.Database.CreateClientAsync("MAP" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant());
        var second = await _fixture.Database.CreateClientAsync("MAP" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant());

        Assert.True((await Integrations.SaveMappingAsync(WebFixture.Admin(), first.Id, "org-1", "Contoso")).Success);

        // The same organization under another client is refused, and it names the client that holds it.
        var taken = await Integrations.SaveMappingAsync(WebFixture.Admin(), second.Id, "org-1", "Contoso");
        Assert.False(taken.Success);
        Assert.Contains(first.Code, taken.Problem);

        // Mapping the same client to another organization replaces its mapping instead of adding a second one.
        Assert.True((await Integrations.SaveMappingAsync(WebFixture.Admin(), first.Id, "org-2", "Contoso EU")).Success);
        var view = await Integrations.GetAction1Async(WebFixture.Admin());
        var mapping = Assert.Single(view!.Mappings.Where(m => m.ClientId == first.Id));
        Assert.Equal("org-2", mapping.TenantId);
        Assert.Equal("Contoso EU", mapping.TenantName);
        Assert.Equal(first.Code, mapping.ClientCode);
    }

    [Fact]
    public async Task Deleting_a_client_takes_its_mapping_with_it()
    {
        await ResetAsync();
        Assert.True((await Integrations.SaveAction1Async(WebFixture.Admin(), Input())).Success);
        var client = await _fixture.Database.CreateClientAsync("DEL" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant());
        Assert.True((await Integrations.SaveMappingAsync(WebFixture.Admin(), client.Id, "org-9", "Gone")).Success);

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            db.Clients.Remove(await db.Clients.SingleAsync(c => c.Id == client.Id));
            await db.SaveChangesAsync();
        }

        var view = await Integrations.GetAction1Async(WebFixture.Admin());
        Assert.Empty(view!.Mappings);
    }

    [Fact]
    public async Task Removing_the_integration_removes_its_mappings_and_is_audited()
    {
        await ResetAsync();
        Assert.True((await Integrations.SaveAction1Async(WebFixture.Admin(), Input())).Success);
        var client = await _fixture.Database.CreateClientAsync("RMV" + Guid.NewGuid().ToString("N")[..5].ToUpperInvariant());
        Assert.True((await Integrations.SaveMappingAsync(WebFixture.Admin(), client.Id, "org-3", "Leaving")).Success);

        Assert.True((await Integrations.RemoveAction1Async(WebFixture.Admin())).Success);

        Assert.Null(await Integrations.GetAction1Async(WebFixture.Admin()));
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.Empty(await db.IntegrationMappings.AsNoTracking().ToListAsync());
        Assert.Contains(await db.AuditEntries.AsNoTracking().Where(a => a.TargetType == "Integration").ToListAsync(),
            a => a.Action == "integration.removed");
    }

    [Fact]
    public async Task Testing_the_connection_before_anything_is_configured_says_so()
    {
        await ResetAsync();

        var result = await Integrations.TestAction1Async(WebFixture.Admin());

        Assert.False(result.Success);
        Assert.Contains("Action1 integration", result.Problem);
    }
}
