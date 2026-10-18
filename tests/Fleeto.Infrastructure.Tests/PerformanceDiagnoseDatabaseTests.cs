using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Data;
using Fleeto.Infrastructure.Performance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Diagnose against a real database (0.7.0 step 2): Npgsql's tracing gives every statement with its fingerprint and caller while
/// Diagnose runs and nothing outside it, the fingerprints are upserted once a minute by their own component, web may analyze a
/// table through the one function that allows it and nothing more, plans are made without running anything, and duplicate
/// indexes are found in the catalog.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class PerformanceDiagnoseDatabaseTests
{
    private readonly Testing.TestDatabase _db;

    public PerformanceDiagnoseDatabaseTests(DatabaseFixture fixture) => _db = fixture.Database;

    [Fact]
    public async Task While_Diagnose_runs_every_statement_is_recorded_with_its_fingerprint_caller_and_N_plus_one_and_written_once_a_minute()
    {
        var now = DateTime.UtcNow;
        var time = new FakeTimeProvider(new DateTimeOffset(now));
        var cache = new PerfSettingsCache(PerformanceSettings.Default with { DiagnoseUntil = now.AddMinutes(30) });
        var recorder = new PerfRecorder("web", cache, time);
        var builder = new NpgsqlDataSourceBuilder(_db.ConnectionString);
        builder.ConfigureTracing(PerfStatementTracer.Configure);
        await using var traced = builder.Build();
        Perf.Use(recorder);
        PerfStatementTracer.Listen();
        try
        {
            var scope = PerfContext.Begin("interaction:/clients");
            for (var i = 0; i < 12; i++)
            {
                await QueryAsync(traced, i);
            }

            recorder.Complete(PerfCollector.Interactions, PerfKind.Interaction, "/clients", "load", scope, error: false);

            // Outside Diagnose nothing is traced.
            cache.Set(PerformanceSettings.Default);
            await QueryAsync(traced, 99);

            var minute = PerfRecorder.MinuteOf(now);
            var window = recorder.Swap(minute.AddMinutes(1));
            var statement = Assert.Single(window.Rows(), r => r.Series is { Kind: PerfKind.Statement, SubKey: "interaction:/clients" });
            Assert.Equal(12, statement.Count);
            Assert.Single(window.Rows(), r => r.Series is { Kind: PerfKind.NPlusOne, Key: "interaction:/clients" } && r.Series.SubKey == statement.Series.Key);

            // The flusher writes the minute: the swapped window is gone, so put the rows back through a fresh record.
            cache.Set(PerformanceSettings.Default with { DiagnoseUntil = now.AddMinutes(30) });
            await using (var db = _db.DbFactory.CreateSystem())
            {
                await db.PerfFingerprints.ExecuteDeleteAsync();
            }

            var flusher = new PerfFlusher(recorder, _db.DataSource, time, NullLogger<PerfFlusher>.Instance);
            recorder.Record(PerfCollector.Interactions, PerfKind.Interaction, "/clients", "load", 10);
            await flusher.FlushAsync(minute.AddMinutes(2), CancellationToken.None);

            await using var check = _db.DbFactory.CreateSystem();
            var fingerprint = await check.PerfFingerprints.AsNoTracking().SingleAsync(f => f.Hash == statement.Series.Key);
            Assert.Equal("web", fingerprint.Component);
            Assert.Contains("\"Clients\"", fingerprint.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("99", fingerprint.Sql, StringComparison.Ordinal);
            Assert.Contains(fingerprint.Callers, c => c.EndsWith($"{nameof(PerformanceDiagnoseDatabaseTests)}.{nameof(QueryAsync)}", StringComparison.Ordinal));
            var runtime = await check.PerfRuntimeMinutes.AsNoTracking().Where(r => r.Component == "web").OrderByDescending(r => r.Id).FirstOrDefaultAsync();
            Assert.True(runtime is null || runtime.Level == PerfLevel.Diagnose);
        }
        finally
        {
            Perf.Use(PerfRecorder.Disabled);
        }
    }

    private static async Task QueryAsync(NpgsqlDataSource dataSource, int value)
    {
        // A literal on purpose: the fingerprint must not keep it.
#pragma warning disable CA2100 // test-only statement built from a number
        await using var command = dataSource.CreateCommand($"""SELECT count(*) FROM "Clients" WHERE "Code" = 'CODE{value}'""");
#pragma warning restore CA2100
        await command.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Fingerprints_are_upserted_by_their_own_component_with_their_callers_merged()
    {
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_gateway");
        await Execute(connection, Upsert("gateway", "Fleeto.Gateway.A"));
        await Execute(connection, Upsert("gateway", "Fleeto.Gateway.B"));
        await Execute(connection, "SAVEPOINT other");
        var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, Upsert("web", "Fleeto.Web.A")));
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
        await Execute(connection, "ROLLBACK TO SAVEPOINT other");
        await Execute(connection, "RESET ROLE");
        await using var read = new NpgsqlCommand("""SELECT "Callers" FROM "PerfFingerprints" WHERE "Hash" = 'test0000test0000'""", connection);
        Assert.Equal(["Fleeto.Gateway.A", "Fleeto.Gateway.B"], (string[])(await read.ExecuteScalarAsync())!);
        await transaction.RollbackAsync();
    }

    private static string Upsert(string component, string caller) => $"""
        INSERT INTO "PerfFingerprints" ("Component", "Hash", "Sql", "Callers", "FirstSeen", "LastSeen")
        VALUES ('{component}', 'test0000test0000', 'SELECT ?', ARRAY['{caller}'], now(), now())
        ON CONFLICT ("Component", "Hash") DO UPDATE SET "Callers" = (SELECT array_agg(DISTINCT c ORDER BY c) FROM unnest("PerfFingerprints"."Callers" || EXCLUDED."Callers") c)
        """;

    [Fact]
    public async Task Web_may_analyze_a_table_and_plan_a_statement_through_its_functions_and_nothing_more()
    {
        await EnsureRolesAsync();
        await using var connection = await _db.DataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await Execute(connection, DatabaseGrants.BuildSql());
        await Execute(connection, "SET LOCAL ROLE fleeto_web");
        await Execute(connection, """SELECT fleeto_perf_analyze('Clients')""");
        foreach (var name in new[] { "NoSuchTable", "Clients; DROP TABLE \"Clients\"", "pg_class" })
        {
            await Execute(connection, "SAVEPOINT attempt");
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, $"SELECT fleeto_perf_analyze('{name.Replace("'", "''", StringComparison.Ordinal)}')"));
            Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);
            await Execute(connection, "ROLLBACK TO SAVEPOINT attempt");
        }

        // Planning runs with web's own rights: a table web may not read cannot be planned, and one statement only.
        await Execute(connection, """SELECT fleeto_perf_explain('SELECT "Code" FROM "Clients" WHERE "Id" = $1')""");
        foreach (var (statement, code) in new[]
                 {
                     ("""SELECT * FROM "InstanceSigningKeys" """, PostgresErrorCodes.InsufficientPrivilege),
                     ("""SELECT 1; DELETE FROM "Clients" """, PostgresErrorCodes.SyntaxError)
                 })
        {
            await Execute(connection, "SAVEPOINT attempt");
            var error = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, $"SELECT fleeto_perf_explain('{statement.Replace("'", "''", StringComparison.Ordinal)}')"));
            Assert.Equal(code, error.SqlState);
            await Execute(connection, "ROLLBACK TO SAVEPOINT attempt");
        }

        await Execute(connection, "RESET ROLE");
        await Execute(connection, "SET LOCAL ROLE fleeto_gateway");
        foreach (var call in new[] { """SELECT fleeto_perf_analyze('Clients')""", """SELECT fleeto_perf_explain('SELECT 1')""" })
        {
            await Execute(connection, "SAVEPOINT attempt");
            var refused = await Assert.ThrowsAsync<PostgresException>(() => Execute(connection, call));
            Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, refused.SqlState);
            await Execute(connection, "ROLLBACK TO SAVEPOINT attempt");
        }

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task A_stored_statement_is_planned_without_running_it_and_a_change_is_refused()
    {
        var plan = await PerformancePlanner.ExplainAsync(_db.DataSource, """SELECT "Id", "Name" FROM "Clients" WHERE "Code" = ? ORDER BY "Name" LIMIT ?""",
            CancellationToken.None);
        Assert.True(plan.Planned, plan.Problem);
        Assert.Contains(plan.Nodes, n => n.Relation == "Clients");

        var refused = await PerformancePlanner.ExplainAsync(_db.DataSource, """DELETE FROM "Clients" WHERE "Code" = ?""", CancellationToken.None);
        Assert.False(refused.Planned);
        Assert.Contains("SELECT and WITH", refused.Problem);

        var broken = await PerformancePlanner.ExplainAsync(_db.DataSource, """SELECT "Nope" FROM "NoSuchTable" WHERE "x" = ?""", CancellationToken.None);
        Assert.False(broken.Planned);
        Assert.StartsWith("PostgreSQL could not plan it", broken.Problem);
    }

    [Fact]
    public async Task An_index_on_the_same_columns_as_another_is_a_duplicate()
    {
        await using (var command = _db.DataSource.CreateCommand("""
            DROP TABLE IF EXISTS "PerfDuplicateTest";
            CREATE TABLE "PerfDuplicateTest" ("Id" int PRIMARY KEY, "Value" int);
            CREATE INDEX "IX_PerfDuplicateTest_A" ON "PerfDuplicateTest" ("Value");
            CREATE INDEX "IX_PerfDuplicateTest_B" ON "PerfDuplicateTest" ("Value");
            CREATE INDEX "IX_PerfDuplicateTest_Partial" ON "PerfDuplicateTest" ("Value") WHERE "Value" > 0;
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        try
        {
            await using var db = _db.DbFactory.CreateSystem();
            var indexes = (await PerformanceReader.ReadIndexesAsync(db, CancellationToken.None)).Where(i => i.Table == "PerfDuplicateTest").ToList();
            Assert.Equal("IX_PerfDuplicateTest_A", indexes.Single(i => i.Name == "IX_PerfDuplicateTest_B").DuplicateOf);
            Assert.Null(indexes.Single(i => i.Name == "IX_PerfDuplicateTest_A").DuplicateOf);
            Assert.Null(indexes.Single(i => i.Name == "IX_PerfDuplicateTest_Partial").DuplicateOf);
            Assert.True(indexes.Single(i => i.Name == "PK_PerfDuplicateTest" || i.Primary).Primary);
            Assert.Equal("Value", indexes.Single(i => i.Name == "IX_PerfDuplicateTest_A").Columns);
        }
        finally
        {
            await using var drop = _db.DataSource.CreateCommand("""DROP TABLE IF EXISTS "PerfDuplicateTest" """);
            await drop.ExecuteNonQueryAsync();
        }
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
