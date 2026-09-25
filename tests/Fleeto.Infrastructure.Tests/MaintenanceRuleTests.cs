using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Testing;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Guarantees the maintenance mode rule: active from its start until its end (none means until turned off), effective on an
/// endpoint through its own, its site's or its client's maintenance or a running window of its policy (the site's, else the default
/// policy, for the endpoint's class), and identical in C#, in the EF Core predicate and in the SQL twin used by the workers.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MaintenanceRuleTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly TestDatabase _db;

    public MaintenanceRuleTests(DatabaseFixture fixture)
    {
        _db = fixture.Database;
    }

    [Fact]
    public void Maintenance_is_active_from_its_start_until_its_end()
    {
        Assert.False(MaintenanceRules.IsActive(null, null, Now));
        Assert.True(MaintenanceRules.IsActive(Now.AddHours(-1), null, Now));
        Assert.True(MaintenanceRules.IsActive(Now.AddHours(-1), Now.AddMinutes(1), Now));
        Assert.False(MaintenanceRules.IsActive(Now.AddHours(-1), Now, Now));
        Assert.False(MaintenanceRules.IsActive(Now.AddHours(-2), Now.AddHours(-1), Now));
        Assert.False(MaintenanceRules.IsActive(Now.AddHours(1), null, Now));
    }

    [Fact]
    public void The_effective_maintenance_is_the_one_that_lasts_longest()
    {
        var endpoint = new MaintenancePeriod(Now.AddHours(-1), Now.AddHours(1), "Tech", "Patch");
        var site = new MaintenancePeriod(Now.AddHours(-1), Now.AddHours(4), "Tech", null);
        var client = new MaintenancePeriod(Now.AddHours(-1), null, "Admin", "Migration");
        var expired = new MaintenancePeriod(Now.AddHours(-3), Now.AddHours(-2), "Tech", null);

        Assert.Null(MaintenanceRules.Effective(MaintenancePeriod.None, expired, MaintenancePeriod.None, Now));
        Assert.Equal(MaintenanceSource.Endpoint, MaintenanceRules.Effective(endpoint, MaintenancePeriod.None, expired, Now)!.Source);
        Assert.Equal(MaintenanceSource.Site, MaintenanceRules.Effective(endpoint, site, expired, Now)!.Source);
        var effective = MaintenanceRules.Effective(endpoint, site, client, Now)!;
        Assert.Equal(MaintenanceSource.Client, effective.Source);
        Assert.Null(effective.EndsAt);
        Assert.Equal("Migration", effective.Reason);
        Assert.Equal(MaintenanceSource.Endpoint, MaintenanceRules.Effective(endpoint, endpoint, MaintenancePeriod.None, Now)!.Source);
    }

    [Fact]
    public void An_end_time_must_lie_in_the_future_and_within_a_year()
    {
        Assert.Null(MaintenanceRules.ValidateEnd(null, Now));
        Assert.Null(MaintenanceRules.ValidateEnd(Now.AddHours(1), Now));
        Assert.NotNull(MaintenanceRules.ValidateEnd(Now, Now));
        Assert.NotNull(MaintenanceRules.ValidateEnd(Now.AddMinutes(-5), Now));
        Assert.NotNull(MaintenanceRules.ValidateEnd(Now.AddDays(400), Now));
    }

    private enum Variant
    {
        None,
        UntilTurnedOff,
        EndsLater,
        Expired,
        StartsLater
    }

    private static (DateTime? StartedAt, DateTime? EndsAt) Period(Variant variant) => variant switch
    {
        Variant.UntilTurnedOff => (Now.AddHours(-1), null),
        Variant.EndsLater => (Now.AddHours(-1), Now.AddHours(1)),
        Variant.Expired => (Now.AddHours(-3), Now.AddHours(-2)),
        Variant.StartsLater => (Now.AddHours(1), null),
        _ => (null, null)
    };

    [Fact]
    public async Task The_csharp_rule_the_ef_predicate_and_the_sql_twin_agree()
    {
        var variants = Enum.GetValues<Variant>();
        var expected = new Dictionary<Guid, bool>();

        foreach (var clientVariant in variants)
        {
            var client = await _db.CreateClientAsync();
            await using (var db = _db.DbFactory.CreateSystem())
            {
                (var started, var ends) = Period(clientVariant);
                await db.Clients.Where(c => c.Id == client.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(c => c.MaintenanceStartedAt, started).SetProperty(c => c.MaintenanceEndsAt, ends));
            }

            foreach (var siteVariant in variants)
            {
                var site = await _db.CreateSiteAsync(client.Id, "Site " + siteVariant);
                await using (var db = _db.DbFactory.CreateSystem())
                {
                    (var started, var ends) = Period(siteVariant);
                    await db.Sites.Where(s => s.Id == site.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(x => x.MaintenanceStartedAt, started).SetProperty(x => x.MaintenanceEndsAt, ends));
                }

                foreach (var endpointVariant in variants)
                {
                    var endpoint = await _db.CreateEndpointAsync(site, hostname: $"EP-{clientVariant}-{siteVariant}-{endpointVariant}");
                    (var started, var ends) = Period(endpointVariant);
                    await using (var db = _db.DbFactory.CreateSystem())
                    {
                        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
                            .SetProperty(e => e.MaintenanceStartedAt, started).SetProperty(e => e.MaintenanceEndsAt, ends));
                    }

                    var c = Period(clientVariant);
                    var s = Period(siteVariant);
                    expected[endpoint.Id] = MaintenanceRules.Effective(
                        new MaintenancePeriod(started, ends, null, null),
                        new MaintenancePeriod(s.StartedAt, s.EndsAt, null, null),
                        new MaintenancePeriod(c.StartedAt, c.EndsAt, null, null), Now) is not null;
                }
            }
        }

        var ids = expected.Keys.ToList();
        await using var context = _db.DbFactory.CreateSystem();
        var byEf = await context.Endpoints.Where(e => ids.Contains(e.Id))
            .Where(MaintenanceRules.EndpointInMaintenance(Now, context.MaintenanceWindowOccurrences, EffectivePolicies.Query(context)))
            .Select(e => e.Id).ToListAsync();
        var bySql = await context.Database.SqlQueryRaw<Guid>(
                """SELECT e."Id" AS "Value" FROM "Endpoints" e WHERE e."Id" = ANY(@ids) AND """ + MaintenanceSql.EndpointInMaintenance,
                new NpgsqlParameter("ids", ids.ToArray()), new NpgsqlParameter("now", Now))
            .ToListAsync();

        Assert.Equal(125, expected.Count);
        Assert.Equal(expected.Where(x => x.Value).Select(x => x.Key).Order(), byEf.Order());
        Assert.Equal(expected.Where(x => x.Value).Select(x => x.Key).Order(), bySql.Order());
        Assert.Contains(expected, x => !x.Value);
    }

    private enum WindowVariant
    {
        DefaultPolicy,
        RunningForAll,
        RunningForServers,
        Ended,
        StartsLater
    }

    [Fact]
    public async Task Policy_windows_agree_in_csharp_ef_and_sql()
    {
        var defaultPolicyId = await DefaultPolicyIdAsync();
        var client = await _db.CreateClientAsync();
        var expected = new Dictionary<Guid, bool>();
        var endpoints = new List<(Guid Id, Guid SiteId, EndpointClass Class)>();
        var policyIds = new List<Guid>();
        try
        {
            // The default policy runs a window for workstations only.
            await AddOccurrenceAsync(defaultPolicyId, Now.AddHours(-1), Now.AddHours(1), CheckAppliesTo.Workstation);

            foreach (var variant in Enum.GetValues<WindowVariant>())
            {
                var site = await _db.CreateSiteAsync(client.Id, "Window " + variant);
                if (variant != WindowVariant.DefaultPolicy)
                {
                    var policyId = Guid.NewGuid();
                    policyIds.Add(policyId);
                    await using (var db = _db.DbFactory.CreateSystem())
                    {
                        db.Policies.Add(new Policy { Id = policyId, Name = "Windows " + policyId.ToString("N")[..8], CreatedAt = Now, UpdatedAt = Now });
                        db.SitePolicies.Add(new SitePolicy { SiteId = site.Id, ClientId = client.Id, PolicyId = policyId, CreatedAt = Now });
                        await db.SaveChangesAsync();
                    }

                    var (starts, ends, appliesTo) = variant switch
                    {
                        WindowVariant.RunningForAll => (Now.AddMinutes(-5), Now.AddMinutes(5), CheckAppliesTo.All),
                        WindowVariant.RunningForServers => (Now.AddHours(-2), Now.AddHours(2), CheckAppliesTo.Server),
                        WindowVariant.Ended => (Now.AddHours(-2), Now, CheckAppliesTo.All),
                        _ => (Now.AddSeconds(1), Now.AddHours(1), CheckAppliesTo.All)
                    };
                    await AddOccurrenceAsync(policyId, starts, ends, appliesTo);
                }

                foreach (var endpointClass in Enum.GetValues<EndpointClass>())
                {
                    var endpoint = await _db.CreateEndpointAsync(site, hostname: $"WIN-{variant}-{endpointClass}", endpointClass: endpointClass);
                    endpoints.Add((endpoint.Id, site.Id, endpointClass));
                }
            }

            await using var context = _db.DbFactory.CreateSystem();
            var running = await MaintenanceWindowSchedule.RunningByEndpointAsync(context, endpoints.Select(e => e.Id).ToList(), Now, CancellationToken.None);
            foreach (var endpoint in endpoints)
            {
                expected[endpoint.Id] = MaintenanceRules.Effective(MaintenancePeriod.None, MaintenancePeriod.None, MaintenancePeriod.None, Now,
                    MaintenanceWindowSchedule.PeriodFor(running, endpoint.Id, endpoint.Class)) is not null;
            }

            var ids = expected.Keys.ToList();
            var byEf = await context.Endpoints.Where(e => ids.Contains(e.Id))
                .Where(MaintenanceRules.EndpointInMaintenance(Now, context.MaintenanceWindowOccurrences, EffectivePolicies.Query(context)))
                .Select(e => e.Id).ToListAsync();
            var bySql = await context.Database.SqlQueryRaw<Guid>(
                    """SELECT e."Id" AS "Value" FROM "Endpoints" e WHERE e."Id" = ANY(@ids) AND """ + MaintenanceSql.EndpointInMaintenance,
                    new NpgsqlParameter("ids", ids.ToArray()), new NpgsqlParameter("now", Now))
                .ToListAsync();

            // Default workstation, running-for-all (both classes) and running-for-servers server.
            Assert.Equal(4, expected.Count(x => x.Value));
            Assert.Equal(expected.Where(x => x.Value).Select(x => x.Key).Order(), byEf.Order());
            Assert.Equal(expected.Where(x => x.Value).Select(x => x.Key).Order(), bySql.Order());
            var serverWindow = MaintenanceWindowSchedule.PeriodFor(running, endpoints.Single(e => e.Class == EndpointClass.Server &&
                running.ContainsKey(e.Id) && running[e.Id].Any(o => o.Occurrence.AppliesTo == CheckAppliesTo.Server)).Id, EndpointClass.Server)!;
            var effective = MaintenanceRules.Effective(MaintenancePeriod.None, MaintenancePeriod.None, MaintenancePeriod.None, Now, serverWindow)!;
            Assert.Equal(MaintenanceSource.PolicyWindow, effective.Source);
            Assert.StartsWith("Windows ", effective.SourceName);
            Assert.Null(effective.StartedByName);
        }
        finally
        {
            await using var db = _db.DbFactory.CreateSystem();
            await db.MaintenanceWindowOccurrences.Where(o => o.PolicyId == defaultPolicyId).ExecuteDeleteAsync();
            await db.Policies.Where(p => policyIds.Contains(p.Id)).ExecuteDeleteAsync();
        }
    }

    private async Task<Guid> DefaultPolicyIdAsync()
    {
        await using var db = _db.DbFactory.CreateSystem();
        return await db.Policies.Where(p => p.IsDefault).Select(p => p.Id).SingleAsync();
    }

    private async Task AddOccurrenceAsync(Guid policyId, DateTime startsAt, DateTime endsAt, CheckAppliesTo appliesTo)
    {
        await using var db = _db.DbFactory.CreateSystem();
        db.MaintenanceWindowOccurrences.Add(new MaintenanceWindowOccurrence
        {
            PolicyId = policyId, WindowIndex = 0, StartsAt = startsAt, EndsAt = endsAt, AppliesTo = appliesTo, Name = "Patch night"
        });
        await db.SaveChangesAsync();
    }
}
