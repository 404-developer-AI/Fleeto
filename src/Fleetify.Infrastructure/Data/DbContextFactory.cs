using Fleetify.Core.Interfaces;
using Fleetify.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Fleetify.Infrastructure.Data;

/// <summary>Creates a context per operation, scoped to a set of clients or to the system.</summary>
public interface IFleetifyDbContextFactory
{
    /// <summary>A context filtered to <paramref name="scope"/>.</summary>
    FleetifyDbContext Create(IClientScope scope);

    /// <summary>A context that sees every client. Only for background services, the signer and tooling.</summary>
    FleetifyDbContext CreateSystem();
}

public sealed class FleetifyDbContextFactory : IFleetifyDbContextFactory
{
    private readonly DbContextOptions<FleetifyDbContext> _options;

    public FleetifyDbContextFactory(DbContextOptions<FleetifyDbContext> options)
    {
        _options = options;
    }

    public FleetifyDbContext Create(IClientScope scope) => new(_options, scope);

    public FleetifyDbContext CreateSystem() => new(_options, SystemClientScope.Instance);

    public static DbContextOptions<FleetifyDbContext> BuildOptions(NpgsqlDataSource dataSource) =>
        new DbContextOptionsBuilder<FleetifyDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsAssembly(typeof(FleetifyDbContext).Assembly.GetName().Name))
            .Options;
}

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. Uses the local development database with the
/// migrator role, reading the password from the development secrets directory.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FleetifyDbContext>
{
    public FleetifyDbContext CreateDbContext(string[] args)
    {
        var secrets = new SecretFiles(string.Empty);
        var password = secrets.Exists(FleetifyComponent.Tool.DatabasePasswordFile())
            ? secrets.ReadText(FleetifyComponent.Tool.DatabasePasswordFile())
            : "design-time";
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = "fleetify_dev",
            Username = DatabaseRoles.Migrator,
            Password = password
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<FleetifyDbContext>().UseNpgsql(connectionString).Options;
        return new FleetifyDbContext(options);
    }
}
