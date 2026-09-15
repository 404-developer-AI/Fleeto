using Fleetify.Core.Entities;
using Fleetify.Infrastructure.Identity;
using Fleetify.Infrastructure.Settings;
using Fleetify.Testing;
using Fleetify.Workers.Alerts;
using Fleetify.Workers.Checks;
using Fleetify.Workers.Configuration;
using Fleetify.Workers.Email;
using Fleetify.Workers.Endpoints;
using Fleetify.Workers.Hosting;
using Fleetify.Workers.Licensing;
using Fleetify.Workers.Options;
using Fleetify.Workers.Retention;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Fleetify.Workers.Tests;

[CollectionDefinition(Name)]
public sealed class WorkersCollection : ICollectionFixture<WorkersFixture>
{
    public const string Name = "workers";
}

/// <summary>One migrated test database for every workers test, plus builders for the services under test.</summary>
public sealed class WorkersFixture : IAsyncLifetime
{
    private TestDatabase? _db;

    public TestDatabase Db => _db ?? throw new InvalidOperationException("The fixture is not initialised.");

    public async Task InitializeAsync() => _db = await TestDatabase.CreateAsync("workers");

    public async Task DisposeAsync()
    {
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }

    public DateTime Now => Db.Time.GetUtcNow().UtcDateTime;

    public SettingsStore Settings() => new(Db.DbFactory, Db.SecretProtector, Db.Time);

    public WorkerHeartbeat Heartbeat() => new(Db.Time);

    public ConfigChangeFanoutService Fanout() =>
        new(Db.DbFactory, Db.Bus, Heartbeat(), Db.Time, NullLogger<ConfigChangeFanoutService>.Instance);

    public CheckEvaluationService CheckEvaluation() =>
        new(Db.DbFactory, Db.Bus, Db.Licenses, new AlertNotificationService(Db.Time), MsOptions.Create(new CheckEvaluationOptions()),
            Heartbeat(), Db.Time, NullLogger<CheckEvaluationService>.Instance);

    public CheckRunRequestService CheckRunRequests() =>
        new(Db.DbFactory, Db.Bus, Db.Licenses, new AlertNotificationService(Db.Time), Heartbeat(), Db.Time,
            NullLogger<CheckRunRequestService>.Instance);

    public AlertHoldService AlertHolds() =>
        new(Db.DbFactory, Db.Bus, new AlertNotificationService(Db.Time), Heartbeat(), Db.Time, NullLogger<AlertHoldService>.Instance);

    public EndpointHealthService EndpointHealth() =>
        new(Db.DbFactory, Db.Bus, Db.Licenses, new AlertNotificationService(Db.Time), Heartbeat(), Db.Time,
            NullLogger<EndpointHealthService>.Instance);

    public MaintenanceExpiryService MaintenanceExpiry() =>
        new(Db.DbFactory, Db.Bus, Heartbeat(), Db.Time, NullLogger<MaintenanceExpiryService>.Instance);

    public EndpointEventService EndpointEvents() =>
        new(Db.DbFactory, Db.Bus, new AlertNotificationService(Db.Time), Heartbeat(), Db.Time, NullLogger<EndpointEventService>.Instance);

    public OutboxEmailService Outbox(IEmailTransportFactory transports, EmailOptions? options = null) =>
        new(Db.DbFactory, Db.Bus, transports, MsOptions.Create(options ?? new EmailOptions { CircuitBreakerFailures = 1000 }), Heartbeat(),
            Db.Time, NullLogger<OutboxEmailService>.Instance);

    public LicenseMonitorService LicenseMonitor() =>
        new(Db.DbFactory, Db.Licenses, Db.SecretProtector, Heartbeat(), Db.Time, NullLogger<LicenseMonitorService>.Instance);

    public RetentionService Retention() =>
        new(Db.DbFactory, Settings(), Heartbeat(), Db.Time, NullLogger<RetentionService>.Instance);

    public async Task<(Client Client, Site Site)> CreateClientAndSiteAsync(string siteName = "Monitoring")
    {
        var client = await Db.CreateClientAsync();
        var site = await Db.CreateSiteAsync(client.Id, siteName);
        return (client, site);
    }

    /// <summary>A global monitoring template with one check, linked to <paramref name="site"/> when given.</summary>
    public async Task<CheckDefinition> CreateCheckAsync(Site? site, CheckType type, double? warning, double? critical,
        int failuresBeforeAlert = 1, CheckAppliesTo appliesTo = CheckAppliesTo.All, string name = "Check", string parameters = "{}")
    {
        await using var db = Db.DbFactory.CreateSystem();
        var now = Now;
        var template = new MonitoringTemplate { Id = Guid.NewGuid(), Name = "Template " + Guid.NewGuid().ToString("N")[..10], CreatedAt = now, UpdatedAt = now };
        var definition = new CheckDefinition
        {
            Id = Guid.NewGuid(),
            MonitoringTemplateId = template.Id,
            Name = name,
            Type = type,
            WarningThreshold = warning,
            CriticalThreshold = critical,
            FailuresBeforeAlert = failuresBeforeAlert,
            AppliesTo = appliesTo,
            ParametersJson = parameters,
            CreatedAt = now,
            UpdatedAt = now
        };
        template.Checks.Add(definition);
        db.MonitoringTemplates.Add(template);
        if (site is not null)
        {
            db.SiteMonitoringTemplates.Add(new SiteMonitoringTemplate
            {
                SiteId = site.Id, ClientId = site.ClientId, MonitoringTemplateId = template.Id, Source = LinkSource.Manual, CreatedAt = now
            });
        }

        await db.SaveChangesAsync();
        return definition;
    }

    public async Task<CheckResult> InsertResultAsync(Endpoint endpoint, CheckDefinition definition, double value, string error = "",
        string target = "")
    {
        await using var db = Db.DbFactory.CreateSystem();
        var result = new CheckResult
        {
            Time = Now,
            ClientId = endpoint.ClientId,
            EndpointId = endpoint.Id,
            CheckDefinitionId = definition.Id,
            Target = target,
            AgentTime = Now,
            Value = value,
            Error = error,
            ConfigVersion = 1
        };
        db.CheckResults.Add(result);
        await db.SaveChangesAsync();
        return result;
    }

    /// <summary>Inserts a result with an explicit id, to simulate a transaction that commits after higher ids.</summary>
    public async Task InsertResultWithIdAsync(long id, Endpoint endpoint, CheckDefinition definition, double value)
    {
        await using var db = Db.DbFactory.CreateSystem();
        await db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "CheckResults" ("Id", "Time", "ClientId", "EndpointId", "CheckDefinitionId", "Target", "AgentTime", "Value", "Detail", "Error", "ConfigVersion")
            OVERRIDING SYSTEM VALUE
            VALUES (@id, @time, @clientId, @endpointId, @definitionId, '', @time, @value, '', '', 1)
            """,
            new NpgsqlParameter("id", id), new NpgsqlParameter("time", Now), new NpgsqlParameter("clientId", endpoint.ClientId),
            new NpgsqlParameter("endpointId", endpoint.Id), new NpgsqlParameter("definitionId", definition.Id), new NpgsqlParameter("value", value));
    }

    public async Task<long> MaxResultIdAsync()
    {
        await using var db = Db.DbFactory.CreateSystem();
        return await db.CheckResults.MaxAsync(r => (long?)r.Id) ?? 0;
    }

    public async Task<ApplicationUser> CreateAdminAsync()
    {
        await using var db = Db.DbFactory.CreateSystem();
        var role = await db.Roles.SingleAsync(r => r.NormalizedName == FleetifyRoles.Admin.ToUpperInvariant());
        var name = "admin-" + Guid.NewGuid().ToString("N")[..8];
        var user = new ApplicationUser
        {
            Id = Guid.NewGuid(),
            UserName = name,
            NormalizedUserName = name.ToUpperInvariant(),
            Email = name + "@test.example",
            NormalizedEmail = (name + "@test.example").ToUpperInvariant(),
            EmailConfirmed = true,
            SecurityStamp = Guid.NewGuid().ToString(),
            ConcurrencyStamp = Guid.NewGuid().ToString(),
            DisplayName = "Admin",
            CreatedAt = Now
        };
        db.Users.Add(user);
        db.UserRoles.Add(new IdentityUserRole<Guid> { UserId = user.Id, RoleId = role.Id });
        await db.SaveChangesAsync();
        return user;
    }

    /// <summary>Marks every unsent email as sent, so a test only sees the emails it creates.</summary>
    public async Task ClearOutboxAsync()
    {
        await using var db = Db.DbFactory.CreateSystem();
        var now = Now;
        await db.OutboxEmails.Where(e => e.SentAt == null).ExecuteUpdateAsync(s => s.SetProperty(e => e.SentAt, now));
    }
}
