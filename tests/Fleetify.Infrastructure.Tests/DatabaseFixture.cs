global using Xunit;
using Fleetify.Testing;

namespace Fleetify.Infrastructure.Tests;

public sealed class DatabaseFixture : IAsyncLifetime
{
    public TestDatabase Database { get; private set; } = null!;

    public async Task InitializeAsync() => Database = await TestDatabase.CreateAsync("infrastructure");

    public async Task DisposeAsync() => await Database.DisposeAsync();
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "infrastructure-database";
}
