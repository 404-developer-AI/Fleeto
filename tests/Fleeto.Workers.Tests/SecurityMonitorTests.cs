using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The security monitor in the workers (0.7.0, step 2): an attack opens one incident and blocks the address, a repeat counts up
/// instead of opening another, a block lasts longer every time, a scanner Fleeto blocked by itself is not sent to every inbox,
/// a network a user signed in from is never blocked, automatic blocking can be switched off, and a quiet incident resolves.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class SecurityMonitorTests
{
    private const string Attacker = "81.2.69.160/32";
    private readonly WorkersFixture _fixture;

    public SecurityMonitorTests(WorkersFixture fixture) => _fixture = fixture;

    private SecurityMonitorService Service() =>
        new(_fixture.Db.DbFactory, _fixture.Settings(), _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<SecurityMonitorService>.Instance);

    private async Task<string> ResetAsync()
    {
        // The audit log is append-only: entries of other tests leave the hour the monitor reads.
        _fixture.Db.Time.Advance(TimeSpan.FromHours(2));
        var recipient = $"security-{Guid.NewGuid():N}@example.com";
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.SecuritySignals.ExecuteDeleteAsync();
        await db.SecurityIncidents.ExecuteDeleteAsync();
        await db.IpBlocks.ExecuteDeleteAsync();
        await db.IpAllows.ExecuteDeleteAsync();
        await db.UserSignInNetworks.ExecuteDeleteAsync();
        await db.NotificationChannels.Where(c => c.SecurityIncidents).ExecuteUpdateAsync(s => s.SetProperty(c => c.Enabled, false));
        db.NotificationChannels.Add(new NotificationChannel
        {
            Id = Guid.NewGuid(), Name = "Security " + recipient[9..17], Recipients = recipient, SecurityIncidents = true, CreatedAt = _fixture.Now,
            UpdatedAt = _fixture.Now
        });
        await db.SaveChangesAsync();
        await _fixture.Settings().SetAsync(SettingKeys.SecurityMonitor, new SecurityMonitorSettings(), encrypted: false, userId: null);
        return recipient;
    }

    private async Task SignalAsync(SecuritySignalKind kind, int count, string network = Attacker)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        db.SecuritySignals.Add(new SecuritySignal { Minute = _fixture.Now.AddMinutes(-1), Component = "web", Kind = kind, Network = network, Count = count });
        await db.SaveChangesAsync();
    }

    private async Task<int> EmailsAsync(string recipient, string category)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.OutboxEmails.CountAsync(e => e.ToAddress == recipient && e.Category == category);
    }

    [Fact]
    public async Task A_brute_force_opens_one_incident_blocks_the_address_and_blocks_longer_when_it_comes_back()
    {
        var recipient = await ResetAsync();
        var service = Service();
        await SignalAsync(SecuritySignalKind.SignInFailed, SecurityRules.BruteForceAttempts);
        await service.EvaluateAsync(CancellationToken.None);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(1));
        await service.EvaluateAsync(CancellationToken.None);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var incident = await db.SecurityIncidents.SingleAsync();
            Assert.Equal((SecurityIncidentKind.BruteForce, Attacker, (DateTime?)null), (incident.Kind, incident.Network, incident.ResolvedAt));
            Assert.Contains("Fleeto blocked the address until", incident.Detail);
            var block = await db.IpBlocks.SingleAsync();
            Assert.Equal((Attacker, IpBlockSource.Automatic, 1), (block.Network, block.Source, block.Times));
            Assert.InRange((block.ExpiresAt!.Value - block.CreatedAt) - TimeSpan.FromHours(1), TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));
            Assert.Equal(1, await db.AuditEntries.CountAsync(a => a.Action == AuditActions.IpBlockCreated && a.ActorId == "security-monitor"));
        }

        Assert.Equal(1, await EmailsAsync(recipient, SecurityMonitorService.CategoryOpened));

        // The block ended and the attack comes back: the same incident counts on, the block lasts a day.
        _fixture.Db.Time.Advance(TimeSpan.FromHours(2));
        await SignalAsync(SecuritySignalKind.SignInFailed, SecurityRules.BruteForceAttempts * 2);
        await service.EvaluateAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(SecurityRules.BruteForceAttempts * 2, (await db.SecurityIncidents.SingleAsync()).Count);
            var block = await db.IpBlocks.SingleAsync();
            Assert.Equal(2, block.Times);
            Assert.InRange((block.ExpiresAt!.Value - block.CreatedAt) - TimeSpan.FromHours(24), TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));
        }

        Assert.Equal(1, await EmailsAsync(recipient, SecurityMonitorService.CategoryOpened));

        // Quiet for a day: the incident resolves.
        _fixture.Db.Time.Advance(SecurityRules.QuietPeriod + TimeSpan.FromMinutes(1));
        await service.EvaluateAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.NotNull((await db.SecurityIncidents.SingleAsync()).ResolvedAt);
        }
    }

    [Fact]
    public async Task A_scanner_blocked_by_itself_is_not_sent_but_one_that_could_not_be_blocked_is()
    {
        var recipient = await ResetAsync();
        await SignalAsync(SecuritySignalKind.SensitivePath, 1);
        await Service().EvaluateAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var block = await db.IpBlocks.SingleAsync();
            Assert.InRange((block.ExpiresAt!.Value - block.CreatedAt) - TimeSpan.FromHours(24), TimeSpan.FromSeconds(-1), TimeSpan.FromSeconds(1));
            Assert.Equal(SecurityIncidentKind.Scanner, (await db.SecurityIncidents.SingleAsync()).Kind);
        }

        Assert.Equal(0, await EmailsAsync(recipient, SecurityMonitorService.CategoryOpened));

        // The same scan from the network a user signed in from: reported, not blocked.
        recipient = await ResetAsync();
        var user = await _fixture.Db.CreateUserAsync(FleetoRoles.Technician);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.UserSignInNetworks.Add(new UserSignInNetwork { UserId = user.Id, Network = "81.2.69.0/24", FirstSeen = _fixture.Now.AddDays(-3), LastSeen = _fixture.Now.AddDays(-1) });
            await db.SaveChangesAsync();
        }

        await SignalAsync(SecuritySignalKind.SensitivePath, 1);
        await Service().EvaluateAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Empty(await db.IpBlocks.ToListAsync());
            Assert.Contains("Not blocked: a user signed in from its network", (await db.SecurityIncidents.SingleAsync()).Detail);
        }

        Assert.Equal(1, await EmailsAsync(recipient, SecurityMonitorService.CategoryOpened));
    }

    [Fact]
    public async Task Nothing_is_blocked_while_automatic_blocking_is_off_or_the_address_is_allowed()
    {
        await ResetAsync();
        await _fixture.Settings().SetAsync(SettingKeys.SecurityMonitor, new SecurityMonitorSettings(AutomaticBlocking: false), encrypted: false, userId: null);
        await SignalAsync(SecuritySignalKind.ApiKeyInvalid, SecurityRules.ApiKeyFailures);
        await Service().EvaluateAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Empty(await db.IpBlocks.ToListAsync());
            Assert.Contains("automatic blocking is off", (await db.SecurityIncidents.SingleAsync()).Detail);
        }

        await ResetAsync();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.IpAllows.Add(new IpAllow { Id = Guid.NewGuid(), Network = "81.2.69.0/24", Description = "office", CreatedAt = _fixture.Now, CreatedByName = "t" });
            await db.SaveChangesAsync();
        }

        await SignalAsync(SecuritySignalKind.ApiKeyInvalid, SecurityRules.ApiKeyFailures);
        await Service().EvaluateAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Empty(await db.IpBlocks.ToListAsync());
            Assert.Contains("it is an allowed address", (await db.SecurityIncidents.SingleAsync()).Detail);
        }
    }

    [Fact]
    public async Task A_privilege_change_in_the_audit_log_is_reported_once()
    {
        var recipient = await ResetAsync();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.AuditEntries.Add(new AuditEntry
            {
                Time = _fixture.Now.AddMinutes(-2), ActorType = AuditActorType.User, ActorId = Guid.NewGuid().ToString(), ActorName = "Sam",
                Action = AuditActions.ApiKeyCreated, TargetType = "ApiKey", TargetId = Guid.NewGuid().ToString(), IpAddress = "81.2.69.7"
            });
            await db.SaveChangesAsync();
        }

        var service = Service();
        await service.EvaluateAsync(CancellationToken.None);
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(1));
        await service.EvaluateAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var incident = await db.SecurityIncidents.SingleAsync(i => i.Kind == SecurityIncidentKind.PrivilegeChange);
            Assert.Equal("Sam created an API key", incident.Title);
            Assert.Empty(await db.IpBlocks.ToListAsync());
        }

        Assert.Equal(1, await EmailsAsync(recipient, SecurityMonitorService.CategoryOpened));
    }
}
