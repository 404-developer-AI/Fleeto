global using Xunit;
using System.Collections.Concurrent;
using Fleetify.Core.Entities;
using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Identity;
using Fleetify.Infrastructure.Licensing;
using Fleetify.Infrastructure.Services;
using Fleetify.Infrastructure.Settings;
using Fleetify.Testing;
using Fleetify.Web;
using Fleetify.Web.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fleetify.Web.Tests;

/// <summary>
/// A migrated test database plus the web services wired as in Program, with the shared infrastructure taken from
/// <see cref="TestDatabase"/> (fake clock, in-memory notification bus) and a logger that records every message.
/// </summary>
public class WebFixtureBase : IAsyncLifetime
{
    private readonly string _databaseName;

    protected WebFixtureBase(string databaseName)
    {
        _databaseName = databaseName;
    }

    public TestDatabase Database { get; private set; } = null!;
    public IServiceProvider Services { get; private set; } = null!;
    public CapturingLoggerProvider Logs { get; } = new();

    public async Task InitializeAsync()
    {
        Database = await TestDatabase.CreateAsync(_databaseName);
        Services = await BuildServicesAsync();
    }

    public virtual async Task DisposeAsync()
    {
        if (Services is IAsyncDisposable disposable)
        {
            await disposable.DisposeAsync();
        }

        await Database.DisposeAsync();
    }

    protected virtual Task<IServiceProvider> BuildServicesAsync()
    {
        var services = new ServiceCollection();
        AddServices(services);
        return Task.FromResult<IServiceProvider>(services.BuildServiceProvider());
    }

    /// <summary>The web services as Program registers them, on the test database's shared infrastructure.</summary>
    protected void AddServices(IServiceCollection services)
    {
        services.AddLogging(logging => logging.AddProvider(Logs).SetMinimumLevel(LogLevel.Trace));
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddSingleton<TimeProvider>(Database.Time);
        services.AddSingleton(Database.DbFactory);
        services.AddSingleton(Database.SecretProtector);
        services.AddSingleton(Database.AuditLog);
        services.AddSingleton<INotificationBus>(Database.Bus);
        services.AddSingleton<LicenseService>(Database.Licenses);
        services.AddSingleton<EndpointTierService>();
        services.AddSingleton<EndpointRevocationService>();
        services.AddSingleton<SettingsStore>();
        services.AddIdentityCore<ApplicationUser>(WebServiceRegistration.ConfigureIdentity)
            .AddRoles<ApplicationRole>()
            .AddFleetifyIdentityStores();
        services.AddFleetifyWebServices();
    }

    /// <summary>A caller with the given roles and scope, as CurrentUser would build it.</summary>
    public static Caller CallerWith(IClientScope scope, params string[] roles) =>
        new(Guid.NewGuid(), "Test user", "test@example.com", roles, scope, "127.0.0.1");

    public static Caller Technician() => CallerWith(Infrastructure.Data.SystemClientScope.Instance, FleetifyRoles.Technician);

    public static Caller Admin() => CallerWith(Infrastructure.Data.SystemClientScope.Instance, FleetifyRoles.Admin);
}

public sealed class WebFixture : WebFixtureBase
{
    public WebFixture() : base("web")
    {
    }
}

/// <summary>A separate database for first-admin setup, which depends on no admin existing yet.</summary>
public sealed class SetupFixture : WebFixtureBase
{
    public SetupFixture() : base("web_setup")
    {
    }
}

[CollectionDefinition(Name)]
public sealed class WebCollection : ICollectionFixture<WebFixture>
{
    public const string Name = "web-database";
}

[CollectionDefinition(Name)]
public sealed class SetupCollection : ICollectionFixture<SetupFixture>
{
    public const string Name = "web-setup-database";
}

/// <summary>Records every log message, so tests can prove secrets never reach a log.</summary>
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<string> Messages { get; } = new();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            provider.Messages.Enqueue(formatter(state, exception) + (exception is null ? string.Empty : " " + exception));
            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var (_, value) in values)
                {
                    provider.Messages.Enqueue(value?.ToString() ?? string.Empty);
                }
            }
        }
    }
}
