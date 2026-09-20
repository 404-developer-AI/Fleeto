using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// What the database guarantees about integrations (0.4.0): the credentials are stored as ciphertext bound to the row,
/// an organization maps to one client and a client to one organization, and deleting a client takes its mapping with it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class IntegrationStoreTests
{
    private readonly DatabaseFixture _fixture;

    public IntegrationStoreTests(DatabaseFixture fixture)
    {
        _fixture = fixture;
    }

    private async Task<Integration> CreateIntegrationAsync()
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.Integrations.RemoveRange(await db.Integrations.ToListAsync());
        await db.SaveChangesAsync();

        var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        var integration = new Integration
        {
            Id = Guid.NewGuid(),
            Type = IntegrationType.Action1,
            Region = Action1Region.Europe,
            CredentialName = "api-key-store@action1.com",
            CreatedAt = now,
            UpdatedAt = now
        };
        integration.EncryptedCredentials = IntegrationCredentials.Protect(_fixture.Database.SecretProtector, integration.Id,
            new Action1Credentials(integration.CredentialName, "store-secret"));
        db.Integrations.Add(integration);
        await db.SaveChangesAsync();
        return integration;
    }

    [Fact]
    public async Task A_mapping_is_stored_for_a_client_and_read_back()
    {
        var integration = await CreateIntegrationAsync();
        var client = await _fixture.Database.CreateClientAsync("ST" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant());

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var tracked = await db.Integrations.Include(i => i.Mappings).SingleAsync(i => i.Id == integration.Id);
            db.IntegrationMappings.Add(new IntegrationMapping
            {
                Id = Guid.NewGuid(),
                IntegrationId = tracked.Id,
                ClientId = client.Id,
                ExternalTenantId = "org-store",
                ExternalTenantName = "Store",
                CreatedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime
            });
            tracked.UpdatedAt = _fixture.Database.Time.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync();
        }

        await using var read = _fixture.Database.DbFactory.CreateSystem();
        var mapping = await read.IntegrationMappings.AsNoTracking().SingleAsync(m => m.IntegrationId == integration.Id);
        Assert.Equal(client.Id, mapping.ClientId);
        Assert.Equal("org-store", mapping.ExternalTenantId);
    }

    [Fact]
    public async Task The_credentials_are_unreadable_when_they_are_moved_to_another_row()
    {
        var integration = await CreateIntegrationAsync();

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var stored = await db.Integrations.AsNoTracking().SingleAsync(i => i.Id == integration.Id);

        Assert.ThrowsAny<System.Security.Cryptography.CryptographicException>(() =>
            IntegrationCredentials.Unprotect(_fixture.Database.SecretProtector, Guid.NewGuid(), stored.EncryptedCredentials));
    }
}
