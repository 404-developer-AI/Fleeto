using System.Net;
using System.Text.RegularExpressions;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The way back in for an admin who is locked out, <c>install.sh --fqdn &lt;name&gt; --unblock [--allow &lt;address&gt;]</c>, run
/// against a real database with the SQL taken from deploy/install.sh: it lifts every block, and with --allow it also allows that one
/// address, so a blocklist (feed) that lists it no longer refuses it either. Both are in the audit log.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class UnblockCommandTests
{
    private readonly Testing.TestDatabase _db;

    public UnblockCommandTests(DatabaseFixture fixture) => _db = fixture.Database;

    /// <summary>The SQL of command_unblock, with the psql variable :'allow' replaced as psql would (a quoted literal).</summary>
    private static string UnblockSql(string allow)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "deploy", "install.sh")))
        {
            dir = dir.Parent;
        }

        var script = File.ReadAllText(Path.Combine(dir!.FullName, "deploy", "install.sh")).Replace("\r", "");
        var function = script[script.IndexOf("\ncommand_unblock() {", StringComparison.Ordinal)..];
        var sql = Regex.Match(function, "<<'SQL'\n(.*?)\nSQL\n", RegexOptions.Singleline).Groups[1].Value;
        Assert.Contains("DELETE FROM \"IpBlocks\"", sql);
        Assert.Contains(":'allow'", sql);
        return sql.Replace(":'allow'", $"'{allow.Replace("'", "''")}'", StringComparison.Ordinal);
    }

    private async Task<string> RunAsync(string allow)
    {
        await using var connection = await _db.DataSource.OpenConnectionAsync();
#pragma warning disable CA2100 // Test only: the fixed text of install.sh with a constant address.
        await using var command = new NpgsqlCommand(UnblockSql(allow), connection);
#pragma warning restore CA2100
        return Convert.ToString(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)!;
    }

    [Fact]
    public async Task Unblock_lifts_every_block_and_allow_lets_a_listed_address_back_in()
    {
        var now = _db.Time.GetUtcNow().UtcDateTime;
        await using (var db = _db.DbFactory.CreateSystem())
        {
            await db.IpBlocks.ExecuteDeleteAsync();
            await db.IpAllows.ExecuteDeleteAsync();
            await db.IpFeedEntries.ExecuteDeleteAsync();
            await db.IpFeeds.Where(f => f.Key == IpFeeds.FireHolLevel1)
                .ExecuteUpdateAsync(s => s.SetProperty(f => f.Enabled, true).SetProperty(f => f.Version, f => f.Version + 1));
            // A blocklist lists the admin's network, which nobody signed in from in the last 30 days.
            db.IpFeedEntries.Add(new IpFeedEntry { Feed = IpFeeds.FireHolLevel1, Network = "81.2.75.0/24" });
            db.IpBlocks.Add(new IpBlock { Id = Guid.NewGuid(), Network = "81.2.75.20/32", Source = IpBlockSource.Manual, Reason = "lockout",
                CreatedAt = now, CreatedByName = "t" });
            await db.SaveChangesAsync();
        }

        try
        {
            var list = new IpBlockList(_db.DataSource, _db.Bus, _db.Time, NullLogger<IpBlockList>.Instance);
            Assert.True(await list.ReloadAsync(CancellationToken.None));
            Assert.Equal(IpVerdict.BlockedManual, list.Check(IPAddress.Parse("81.2.75.20")));

            // Without --allow: the block is lifted, the blocklist still refuses the address, and the message says what to do.
            Assert.Equal("1 0", await RunAsync(string.Empty));
            Assert.True(await list.ReloadAsync(CancellationToken.None));
            Assert.Equal(IpVerdict.BlockedFeed, list.Check(IPAddress.Parse("81.2.75.20")));

            // With --allow: the address is allowed, which no blocklist overrides.
            Assert.Equal("0 1", await RunAsync("81.2.75.20"));
            Assert.Equal("0 0", await RunAsync("81.2.75.20"));
            Assert.Equal("0 1", await RunAsync("2a01:4f8:0:a::7"));
            Assert.True(await list.ReloadAsync(CancellationToken.None));
            Assert.Equal(IpVerdict.Allowed, list.Check(IPAddress.Parse("81.2.75.20")));
            Assert.Equal(IpVerdict.BlockedFeed, list.Check(IPAddress.Parse("81.2.75.21")));

            await using var db = _db.DbFactory.CreateSystem();
            Assert.Equal(["2a01:4f8:0:a::7/128", "81.2.75.20/32"], await db.IpAllows.OrderBy(a => a.Network).Select(a => a.Network).ToListAsync());
            Assert.True(IpNetworks.TryParse("2a01:4f8:0:a::7", out var v6) && IpNetworks.Format(v6) == "2a01:4f8:0:a::7/128");
            Assert.Equal(2, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.IpAllowCreated && a.ActorId == "install.sh"));
            Assert.True(await db.AuditEntries.AnyAsync(a => a.Action == AuditActions.IpBlockRemoved && a.ActorId == "install.sh"));

            // Not an address: PostgreSQL refuses it and nothing changes.
            await Assert.ThrowsAsync<PostgresException>(() => RunAsync("81.2.75.0/24'; DELETE FROM \"IpAllows\"; --"));
            Assert.Equal(2, await db.IpAllows.CountAsync());
        }
        finally
        {
            await using var db = _db.DbFactory.CreateSystem();
            await db.IpFeedEntries.ExecuteDeleteAsync();
            await db.IpAllows.ExecuteDeleteAsync();
        }
    }
}
