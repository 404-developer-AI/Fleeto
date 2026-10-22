using System.Net;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The security monitor against a real database (0.7.0): a minute of signals is written in one batch, a component writes only its
/// own signals, web writes only manual blocks and the gateway none, every change of the blocked or allowed addresses raises a
/// notification, and the list loads what is active from the tables.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SecurityMonitorDatabaseTests
{
    private readonly Testing.TestDatabase _db;

    public SecurityMonitorDatabaseTests(DatabaseFixture fixture) => _db = fixture.Database;

    private async Task ClearAsync()
    {
        await using var db = _db.DbFactory.CreateSystem();
        await db.SecuritySignals.ExecuteDeleteAsync();
        await db.IpBlocks.ExecuteDeleteAsync();
        await db.IpAllows.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task A_minute_of_signals_is_written_in_one_batch()
    {
        await ClearAsync();
        var recorder = new SecuritySignalRecorder("web", _db.Time);
        for (var i = 0; i < 12; i++)
        {
            recorder.Record(SecuritySignalKind.ScannerPath, IPAddress.Parse("81.2.69.160"));
        }

        recorder.Record(SecuritySignalKind.ApiKeyInvalid, IPAddress.Parse("2a01:4f8:1:2::9"), "key-1");
        var flusher = new SecuritySignalFlusher(recorder, _db.DataSource, _db.Time, NullLogger<SecuritySignalFlusher>.Instance);
        await flusher.FlushAsync(_db.Time.GetUtcNow().UtcDateTime.AddMinutes(1), CancellationToken.None);

        await using var db = _db.DbFactory.CreateSystem();
        var rows = await db.SecuritySignals.AsNoTracking().OrderByDescending(s => s.Count).ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal((SecuritySignalKind.ScannerPath, "81.2.69.160/32", 12, "web"), (rows[0].Kind, rows[0].Network, rows[0].Count, rows[0].Component));
        Assert.Equal((SecuritySignalKind.ApiKeyInvalid, "2a01:4f8:1:2::/64", "key-1"), (rows[1].Kind, rows[1].Network, rows[1].Subject));
        Assert.Empty(recorder.Current());
    }

    [Fact]
    public async Task Each_component_writes_only_what_it_may()
    {
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());

        await Execute(connection, "SET LOCAL ROLE fleeto_gateway");
        await Execute(connection, InsertSignal("gateway"));
        await ExpectRefusedAsync(connection, InsertSignal("web"));
        await ExpectRefusedAsync(connection, InsertBlock("Manual"));
        await Execute(connection, """SELECT count(*) FROM "IpBlocks" """);
        await Execute(connection, """SELECT count(*) FROM "IpAllows" """);
        await ExpectRefusedAsync(connection, """SELECT count(*) FROM "SecuritySignals" """);

        await Execute(connection, "RESET ROLE");
        await Execute(connection, "SET LOCAL ROLE fleeto_web");
        await Execute(connection, InsertBlock("Manual"));
        await ExpectRefusedAsync(connection, InsertBlock("Automatic", "81.2.69.1/32"));
        await Execute(connection, """INSERT INTO "IpAllows" ("Id", "Network", "Description", "CreatedAt", "CreatedByName") VALUES (gen_random_uuid(), '81.2.69.2/32', 'office', now(), 'test')""");

        await Execute(connection, "RESET ROLE");
        await Execute(connection, "SET LOCAL ROLE fleeto_workers");
        await Execute(connection, InsertBlock("Automatic", "81.2.69.3/32"));
        await ExpectRefusedAsync(connection, InsertSignal("workers"));
        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_change_of_the_blocked_or_allowed_addresses_raises_a_notification()
    {
        await ClearAsync();
        await using var listener = await _db.DataSource.OpenConnectionAsync();
        var received = new List<string>();
        listener.Notification += (_, e) => received.Add(e.Channel);
        await Execute(listener, "LISTEN fleeto_ip_blocks");

        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.IpBlocks.Add(new IpBlock { Id = Guid.NewGuid(), Network = "81.2.69.9/32", Reason = "test", CreatedAt = DateTime.UtcNow, CreatedByName = "test" });
            await db.SaveChangesAsync();
            db.IpAllows.Add(new IpAllow { Id = Guid.NewGuid(), Network = "81.2.69.10/32", Description = "test", CreatedAt = DateTime.UtcNow, CreatedByName = "test" });
            await db.SaveChangesAsync();
        }

        while (received.Count < 2 && await listener.WaitAsync(TimeSpan.FromSeconds(5)))
        {
        }

        Assert.Equal(["fleeto_ip_blocks", "fleeto_ip_blocks"], received);
    }

    [Fact]
    public async Task The_list_loads_what_is_active()
    {
        await ClearAsync();
        var now = _db.Time.GetUtcNow().UtcDateTime;
        await using (var db = _db.DbFactory.CreateSystem())
        {
            db.IpBlocks.AddRange(
                new IpBlock { Id = Guid.NewGuid(), Network = "81.2.69.0/24", Source = IpBlockSource.Manual, Reason = "a", CreatedAt = now, CreatedByName = "t" },
                new IpBlock { Id = Guid.NewGuid(), Network = "81.2.70.7/32", Source = IpBlockSource.Automatic, Reason = "b", CreatedAt = now.AddHours(-2),
                    ExpiresAt = now.AddHours(-1), CreatedByName = "t" });
            db.IpAllows.Add(new IpAllow { Id = Guid.NewGuid(), Network = "81.2.69.5/32", Description = "office", CreatedAt = now, CreatedByName = "t" });
            await db.SaveChangesAsync();
        }

        var list = new IpBlockList(_db.DataSource, _db.Bus, _db.Time, NullLogger<IpBlockList>.Instance);
        Assert.True(await list.ReloadAsync(CancellationToken.None));
        Assert.Equal((1, 1), (list.BlockCount, list.AllowCount));
        Assert.Equal(IpVerdict.BlockedManual, list.Check(IPAddress.Parse("81.2.69.4")));
        Assert.Equal(IpVerdict.Allowed, list.Check(IPAddress.Parse("81.2.69.5")));
        Assert.Equal(IpVerdict.None, list.Check(IPAddress.Parse("81.2.70.7")));
    }

    private static string InsertSignal(string component) => $"""
        INSERT INTO "SecuritySignals" ("Minute", "Component", "Kind", "Network", "Count") VALUES (now(), '{component}', 'ScannerPath', '81.2.69.1/32', 1)
        """;

    private static string InsertBlock(string source, string network = "81.2.69.0/32") => $"""
        INSERT INTO "IpBlocks" ("Id", "Network", "Source", "Reason", "CreatedAt", "CreatedByName") VALUES (gen_random_uuid(), '{network}', '{source}', 'test', now(), 'test')
        """;

    private static async Task ExpectRefusedAsync(NpgsqlConnection connection, string sql)
    {
        await Execute(connection, "SAVEPOINT refused");
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, sql));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        await Execute(connection, "ROLLBACK TO SAVEPOINT refused");
    }

    private async Task EnsureRolesAsync()
    {
        await using var command = _db.DataSource.CreateCommand(string.Join('\n', Hosting.DatabaseRoles.Application.Select(r =>
            $"DO $$ BEGIN IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '{r}') THEN CREATE ROLE {r} NOLOGIN; END IF; END $$;")));
        await command.ExecuteNonQueryAsync();
    }

    private static async Task Execute(NpgsqlConnection connection, string sql)
    {
#pragma warning disable CA2100 // test-only statements built from constants
        await using var command = new NpgsqlCommand(sql, connection);
#pragma warning restore CA2100
        await command.ExecuteNonQueryAsync();
    }
}
