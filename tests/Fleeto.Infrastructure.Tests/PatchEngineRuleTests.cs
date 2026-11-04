using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Which engine can patch an endpoint (0.9.0, <c>PATCHING.md</c> §2): Action1 wherever its agent runs or it reports the endpoint,
/// otherwise Fleeto on the operating systems it patches, and nobody on agent-only endpoints, in C#, SQL and EF Core alike.
/// </summary>
[Collection(DatabaseCollection.Name)]
public class PatchEngineRuleTests
{
    private static readonly string EngineQuerySql =
        """SELECT """ + PatchEngineRules.EngineSql + """ AS "Value" FROM "Endpoints" e WHERE e."Id" = @id""";

    private readonly TestDatabase _db;

    public PatchEngineRuleTests(DatabaseFixture fixture) => _db = fixture.Database;

    private sealed record Case(string Name, EndpointTier Tier, string Platform, string OsName, EndpointSource Source = EndpointSource.Agent,
        string Action1AgentId = "", bool ReportedByAction1 = false, bool NoInventory = false);

    private async Task<Endpoint> EndpointAsync(Site site, Case c)
    {
        var endpoint = await _db.CreateEndpointAsync(site, c.Tier, c.Name);
        await using var db = _db.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, c.Platform)
            .SetProperty(e => e.OsName, c.OsName)
            .SetProperty(e => e.Source, c.Source));
        if (!c.NoInventory)
        {
            db.InventorySnapshots.Add(new InventorySnapshot
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = DateTime.UtcNow, Hash = "h", Action1AgentId = c.Action1AgentId
            });
        }

        if (c.ReportedByAction1)
        {
            db.EndpointPatchStates.Add(new EndpointPatchState
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ExternalEndpointId = "a1-" + c.Name, ExternalTenantId = "t1", UpdatedAt = DateTime.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return endpoint;
    }

    private async Task<(EndpointPatchEngine CSharp, EndpointPatchEngine Sql, EndpointPatchEngine Ef)> ResolveAsync(Guid endpointId, LicenseStatus license)
    {
        await using var db = _db.DbFactory.CreateSystem();
        var endpoint = await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpointId);
        var inventory = await db.InventorySnapshots.AsNoTracking().SingleOrDefaultAsync(i => i.EndpointId == endpointId);
        var state = await db.EndpointPatchStates.AsNoTracking().SingleOrDefaultAsync(s => s.EndpointId == endpointId);
        var csharp = PatchEngineRules.EngineFor(endpoint, license, inventory, state).Engine;
        var sql = await db.Database.SqlQueryRaw<string>(EngineQuerySql, new NpgsqlParameter("id", endpointId),
            new NpgsqlParameter("allowsManaged", license.AllowsManaged)).SingleAsync();
        var ef = (await PatchEngines.Query(db, license.AllowsManaged).SingleAsync(r => r.EndpointId == endpointId)).Engine;
        return (csharp, Enum.Parse<EndpointPatchEngine>(sql), ef);
    }

    [Fact]
    public async Task Every_form_of_the_rule_gives_each_endpoint_the_same_engine()
    {
        var client = await _db.CreateClientAsync();
        var site = await _db.CreateSiteAsync(client.Id);
        var cases = new (Case Case, EndpointPatchEngine Expected)[]
        {
            (new("WIN11", EndpointTier.Managed, "windows", "Windows 11 Pro"), EndpointPatchEngine.Fleeto),
            (new("SRV2022", EndpointTier.Managed, "windows", "Windows Server 2022 Standard"), EndpointPatchEngine.Fleeto),
            (new("SRV-SPACE", EndpointTier.Managed, "windows", "  windows server 2019 datacenter "), EndpointPatchEngine.Fleeto),
            (new("UBUNTU", EndpointTier.Managed, "linux", "Ubuntu 24.04.1 LTS"), EndpointPatchEngine.Fleeto),
            (new("DEBIAN", EndpointTier.Managed, "linux", "Debian GNU/Linux 12 (bookworm)"), EndpointPatchEngine.Fleeto),
            (new("ALMA", EndpointTier.Managed, "linux", "AlmaLinux 9.4 (Seafoam Ocelot)"), EndpointPatchEngine.Fleeto),
            (new("PVE", EndpointTier.Managed, "linux", RemoteSessionRules.ProxmoxOsPrefix + "8.2.4"), EndpointPatchEngine.Fleeto),
            (new("NAS", EndpointTier.Managed, "linux", RemoteSessionRules.SynologyOsPrefix + "7.1.1-42962"), EndpointPatchEngine.None),
            (new("FEDORA", EndpointTier.Managed, "linux", "Fedora Linux 40"), EndpointPatchEngine.None),
            (new("MAC", EndpointTier.Managed, "darwin", "macOS 15"), EndpointPatchEngine.None),
            (new("EMPTY", EndpointTier.Managed, "windows", "", NoInventory: true), EndpointPatchEngine.None),
            (new("AGENTONLY", EndpointTier.AgentOnly, "windows", "Windows 11 Pro"), EndpointPatchEngine.None),
            (new("AGENTONLY-A1", EndpointTier.AgentOnly, "windows", "Windows 11 Pro", Action1AgentId: "a1-agent"), EndpointPatchEngine.None),
            (new("VM", EndpointTier.Managed, "linux", "Ubuntu 22.04.4 LTS", Source: EndpointSource.Integration), EndpointPatchEngine.None),
            (new("A1-AGENT", EndpointTier.Managed, "windows", "Windows 11 Pro", Action1AgentId: "a1-agent"), EndpointPatchEngine.Action1),
            (new("A1-REPORTED", EndpointTier.Managed, "windows", "Windows 10 Pro", ReportedByAction1: true), EndpointPatchEngine.Action1),
            (new("A1-LINUX", EndpointTier.Managed, "linux", "Ubuntu 24.04.1 LTS", Action1AgentId: "a1-linux"), EndpointPatchEngine.Action1),
            (new("NO-INVENTORY", EndpointTier.Managed, "linux", "Debian GNU/Linux 13 (trixie)", NoInventory: true), EndpointPatchEngine.Fleeto)
        };

        var valid = LicenseStatus.Evaluate(100, DateTime.UtcNow.AddYears(1), DateTime.UtcNow);
        var expired = LicenseStatus.Evaluate(100, DateTime.UtcNow.AddDays(-LicenseStatus.GraceDays - 1), DateTime.UtcNow);
        Assert.True(valid.AllowsManaged);
        Assert.False(expired.AllowsManaged);
        foreach (var (c, expected) in cases)
        {
            var endpoint = await EndpointAsync(site, c);

            var (csharp, sql, ef) = await ResolveAsync(endpoint.Id, valid);
            Assert.True(expected == csharp, $"{c.Name}: C# gave {csharp}, expected {expected}");
            Assert.True(expected == sql, $"{c.Name}: SQL gave {sql}, expected {expected}");
            Assert.True(expected == ef, $"{c.Name}: EF Core gave {ef}, expected {expected}");

            // After the license grace period every endpoint behaves as agent-only, so nobody patches anything.
            var afterGrace = await ResolveAsync(endpoint.Id, expired);
            Assert.Equal((EndpointPatchEngine.None, EndpointPatchEngine.None, EndpointPatchEngine.None), afterGrace);
        }
    }

    [Fact]
    public void An_endpoint_without_an_engine_says_why()
    {
        Assert.Equal(PatchEngineReason.NoAgent,
            PatchEngineRules.EngineFor(EndpointSource.Integration, EndpointTier.Managed, "linux", "Ubuntu 24.04 LTS", null, false).Reason);
        Assert.Equal(PatchEngineReason.AgentOnly,
            PatchEngineRules.EngineFor(EndpointSource.Agent, EndpointTier.AgentOnly, "windows", "Windows 11 Pro", "a1", true).Reason);
        Assert.Equal(PatchEngineReason.UpdatesItself,
            PatchEngineRules.EngineFor(EndpointSource.Agent, EndpointTier.Managed, "linux", RemoteSessionRules.SynologyOsPrefix + "7.2", null, false).Reason);
        Assert.Equal(PatchEngineReason.NotSupported,
            PatchEngineRules.EngineFor(EndpointSource.Agent, EndpointTier.Managed, "linux", "Arch Linux", null, false).Reason);
        Assert.Equal(PatchEngineResult.Fleeto,
            PatchEngineRules.EngineFor(EndpointSource.Agent, EndpointTier.Managed, "linux", "AlmaLinux 10.0", null, false));
        // Action1 wins over Fleeto on a platform both cover, so one endpoint is never patched twice.
        Assert.Equal(PatchEngineResult.Action1,
            PatchEngineRules.EngineFor(EndpointSource.Agent, EndpointTier.Managed, "windows", "Windows 11 Pro", string.Empty, true));

        foreach (var reason in Enum.GetValues<PatchEngineReason>().Where(r => r != PatchEngineReason.None))
        {
            Assert.False(string.IsNullOrWhiteSpace(PatchEngineRules.Describe(reason)));
        }

        Assert.Null(PatchEngineRules.Describe(PatchEngineReason.None));
    }

    [Fact]
    public async Task A_patch_policy_is_patched_by_Action1_unless_it_says_otherwise_and_refuses_another_engine()
    {
        await using var db = _db.DbFactory.CreateSystem();
        var now = DateTime.UtcNow;
        var policy = new PatchPolicy { Id = Guid.NewGuid(), Name = "Engine " + Guid.NewGuid(), CreatedAt = now, UpdatedAt = now };
        db.PatchPolicies.Add(policy);
        await db.SaveChangesAsync();
        Assert.Equal("Action1", await db.Database.SqlQueryRaw<string>(
            """SELECT "Engine" AS "Value" FROM "PatchPolicies" WHERE "Id" = @id""", new NpgsqlParameter("id", policy.Id)).SingleAsync());

        var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(
            """UPDATE "PatchPolicies" SET "Engine" = 'Both' WHERE "Id" = @id""", new NpgsqlParameter("id", policy.Id)));
        Assert.Equal("CK_PatchPolicies_Engine", error.ConstraintName);

        policy.Engine = (PatchEngine)7;
        Assert.NotNull(PatchPolicyRules.Validate(policy));
    }
}
