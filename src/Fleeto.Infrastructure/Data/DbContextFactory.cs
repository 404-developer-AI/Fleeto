using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Npgsql;

namespace Fleeto.Infrastructure.Data;

/// <summary>Creates a context per operation, scoped to a set of clients or to the system.</summary>
public interface IFleetoDbContextFactory
{
    /// <summary>A context filtered to <paramref name="scope"/>.</summary>
    FleetoDbContext Create(IClientScope scope);

    /// <summary>A context that sees every client. Only for background services, the signer and tooling.</summary>
    FleetoDbContext CreateSystem();
}

public sealed class FleetoDbContextFactory : IFleetoDbContextFactory
{
    private readonly DbContextOptions<FleetoDbContext> _options;

    public FleetoDbContextFactory(DbContextOptions<FleetoDbContext> options)
    {
        _options = options;
    }

    public FleetoDbContext Create(IClientScope scope) => new(_options, scope);

    public FleetoDbContext CreateSystem() => new(_options, SystemClientScope.Instance);

    public static DbContextOptions<FleetoDbContext> BuildOptions(NpgsqlDataSource dataSource) =>
        new DbContextOptionsBuilder<FleetoDbContext>()
            .UseNpgsql(dataSource, npgsql => npgsql.MigrationsAssembly(typeof(FleetoDbContext).Assembly.GetName().Name))
            .Options;
}

/// <summary>
/// Design-time factory for <c>dotnet ef migrations add</c>. Uses the local development database with the
/// migrator role, reading the password from the development secrets directory.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<FleetoDbContext>
{
    public FleetoDbContext CreateDbContext(string[] args)
    {
        var secrets = new SecretFiles(string.Empty);
        var password = secrets.Exists(FleetoComponent.Tool.DatabasePasswordFile())
            ? secrets.ReadText(FleetoComponent.Tool.DatabasePasswordFile())
            : "design-time";
        var connectionString = new NpgsqlConnectionStringBuilder
        {
            Host = "localhost",
            Database = "fleeto_dev",
            Username = DatabaseRoles.Migrator,
            Password = password
        }.ConnectionString;

        var options = new DbContextOptionsBuilder<FleetoDbContext>().UseNpgsql(connectionString).Options;
        return new FleetoDbContext(options);
    }
}
