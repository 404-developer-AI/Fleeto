using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Audit;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Hosting;
using Fleeto.Infrastructure.Licensing;
using Fleeto.Infrastructure.Notifications;
using Fleeto.Infrastructure.Security;
using Fleeto.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Fleeto.Infrastructure;

public static class DependencyInjection
{
    /// <summary>
    /// Registers what every component shares: secrets, the database (as the component's own role), the
    /// notification bus, audit, licensing and the domain services. Keys are registered only for the components
    /// that may hold them: the root key for web, workers and the tool; the signer key for the signer and the tool.
    /// </summary>
    public static IServiceCollection AddFleetoInfrastructure(this IServiceCollection services, IConfiguration configuration,
        FleetoComponent component)
    {
        services.AddSingleton(TimeProvider.System);

        services.Configure<SecretsOptions>(configuration.GetSection(SecretsOptions.SectionName));
        services.AddSingleton<SecretFiles>();

        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));
        // One data source per process: a connection pool for the component's role.
        services.AddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<DatabaseOptions>>().Value;
            var secrets = sp.GetRequiredService<SecretFiles>();
            return new NpgsqlDataSourceBuilder(options.BuildConnectionString(component, secrets)).Build();
        });
        services.AddSingleton(sp => FleetoDbContextFactory.BuildOptions(sp.GetRequiredService<NpgsqlDataSource>()));
        services.AddSingleton<IFleetoDbContextFactory, FleetoDbContextFactory>();

        // Why the bus is a hosted service: it owns the LISTEN connection and reconnects on its own.
        services.AddSingleton<PostgresNotificationBus>();
        services.AddSingleton<INotificationBus>(sp => sp.GetRequiredService<PostgresNotificationBus>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<PostgresNotificationBus>());

        services.AddSingleton<IAuditLog, AuditLog>();

        if (component.UsesRootKey())
        {
            services.AddSingleton(sp => new RootKey(sp.GetRequiredService<SecretFiles>().ReadKey(SecretFiles.RootKeyFile)));
            services.AddSingleton<ISecretProtector, EnvelopeSecretProtector>();
            services.AddSingleton<Settings.SettingsStore>();
        }

        if (component.UsesSignerKey())
        {
            services.AddSingleton(sp => new SignerKey(sp.GetRequiredService<SecretFiles>().ReadKey(SecretFiles.SignerKeyFile)));
        }

        services.AddSingleton(sp => new LicenseService(
            sp.GetRequiredService<IFleetoDbContextFactory>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetService<ISecretProtector>(),
            sp.GetRequiredService<IAuditLog>()));
        services.AddSingleton<AgentConfigBuilder>();
        services.AddSingleton<EndpointTierService>();
        services.AddSingleton<EndpointRevocationService>();

        return services;
    }
}
