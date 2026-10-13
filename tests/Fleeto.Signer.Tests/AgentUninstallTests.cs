using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Protocol.Agent.V1;
using Fleeto.Signer.Handlers;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Signer.Tests;

/// <summary>
/// Guarantees the uninstall command of a deleted endpoint (0.6.0): signed only for an endpoint that no longer exists, for the
/// endpoint and user web named, by an admin or technician, valid for the waiting period; anything else is refused and the row says why.
/// </summary>
[Collection(SignerCollection.Name)]
public sealed class AgentUninstallTests
{
    private readonly SignerFixture _fixture;

    public AgentUninstallTests(SignerFixture fixture) => _fixture = fixture;

    private async Task<AgentUninstall> RowAsync(Guid userId, Guid? endpointId = null)
    {
        var uninstall = new AgentUninstall
        {
            Id = Guid.NewGuid(), ClientId = Guid.NewGuid(), ClientCode = "GONE", EndpointId = endpointId ?? Guid.NewGuid(), Hostname = "WS-GONE",
            Fingerprints = [new string('a', 64)], RequestedByUserId = userId, RequestedByName = "Tess Tech", RequestedAt = _fixture.Now,
            ExpiresAt = _fixture.Now + AgentUninstallRules.Lifetime
        };
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        db.AgentUninstalls.Add(uninstall);
        await db.SaveChangesAsync();
        return uninstall;
    }

    private async Task<(SigningRequest Request, AgentUninstall Row)> SignAsync(AgentUninstall uninstall, byte[]? binding = null)
    {
        var request = await _fixture.ProcessAsync(SigningRequestKind.AgentUninstall, null, uninstall.Id,
            binding ?? AgentUninstallRules.Binding(uninstall.EndpointId, uninstall.RequestedByUserId), "web:198.51.100.40");
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        return (request, await db.AgentUninstalls.AsNoTracking().SingleAsync(u => u.Id == uninstall.Id));
    }

    [Fact]
    public async Task The_command_of_a_deleted_endpoint_is_signed_for_that_endpoint_until_the_row_expires()
    {
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician, displayName: "Tess Tech");
        var uninstall = await RowAsync(technician.Id);

        var (request, signed) = await SignAsync(uninstall);

        Assert.Equal(SigningRequestState.Completed, request.State);
        Assert.NotNull(signed.SignedAt);
        Assert.True(Ed25519.Verify(_fixture.KeyRing.SigningPublicKey, SignatureContexts.Uninstall, signed.Payload, signed.Signature!));
        var command = UninstallCommand.Parser.ParseFrom(signed.Payload);
        Assert.Equal(uninstall.EndpointId.ToString("D"), command.EndpointId);
        Assert.Equal(uninstall.Id.ToString("D"), command.UninstallId);
        Assert.Equal(_fixture.KeyRing.InstanceId.ToString("D"), command.InstanceId);
        Assert.Equal(uninstall.ExpiresAt, command.ValidUntil.ToDateTime(), TimeSpan.FromSeconds(1));
        Assert.Equal("Tess Tech", command.RequestedBy);
    }

    [Fact]
    public async Task An_endpoint_that_still_exists_another_endpoint_or_a_read_only_user_is_refused()
    {
        var technician = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var client = await _fixture.Database.CreateClientAsync();
        var site = await _fixture.Database.CreateSiteAsync(client.Id);
        var existing = await _fixture.Database.CreateEndpointAsync(site, EndpointTier.AgentOnly, "WS-STILL-HERE");

        var (_, alive) = await SignAsync(await RowAsync(technician.Id, existing.Id));
        Assert.Equal((AgentUninstallHandler.EndpointExistsReason, (DateTime?)null), (alive.RefusalReason, alive.SignedAt));

        var other = await RowAsync(technician.Id);
        var (_, rebound) = await SignAsync(other, AgentUninstallRules.Binding(Guid.NewGuid(), technician.Id));
        Assert.Equal(AgentUninstallHandler.BindingReason, rebound.RefusalReason);

        var readOnly = await _fixture.Database.CreateUserAsync(FleetoRoles.ReadOnly);
        var (_, refused) = await SignAsync(await RowAsync(readOnly.Id));
        Assert.Equal(AgentUninstallHandler.UserReason, refused.RefusalReason);
        Assert.Null(refused.Payload);
    }
}
