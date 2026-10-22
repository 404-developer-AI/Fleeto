using System.Net;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Core.Interfaces;
using Fleeto.Infrastructure.Security;
using Fleeto.Web.Security;
using Fleeto.Web.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Fleeto.Web.Tests;

/// <summary>
/// The security monitor in web (0.7.0): only a caller with the permission and every client blocks or allows addresses, nobody
/// blocks a reserved range, a network that is too wide or their own address, every change is audited; a blocked address gets a
/// 403 before anything else runs unless it is allowed, and a scanner probe gets a bare 404 and counts as a signal.
/// </summary>
[Collection(WebCollection.Name)]
public sealed class SecurityServiceTests
{
    private readonly WebFixture _fixture;

    public SecurityServiceTests(WebFixture fixture) => _fixture = fixture;

    private SecurityService Security => _fixture.Services.GetRequiredService<SecurityService>();

    private static Caller Admin() => WebFixture.Admin() with { IpAddress = "81.2.69.20" };

    private async Task ClearAsync()
    {
        await using var db = _fixture.Database.DbFactory.CreateSystem();
        await db.IpBlocks.ExecuteDeleteAsync();
        await db.IpAllows.ExecuteDeleteAsync();
    }

    [Fact]
    public async Task Only_a_caller_with_the_permission_blocks_or_allows()
    {
        await ClearAsync();
        Assert.False((await Security.BlockAsync(WebFixture.Technician(), "81.2.70.1", "attack", null)).Success);
        Assert.False((await Security.AllowAsync(WebFixture.Technician(), "81.2.70.1", "office")).Success);
        await Assert.ThrowsAsync<AccessDeniedException>(() => Security.ListBlocksAsync(WebFixture.Technician()));

        Assert.Contains(Permissions.SecurityManage, BuiltInRoles.AdminPermissions);
        Assert.DoesNotContain(Permissions.SecurityView, BuiltInRoles.TechnicianPermissions);
    }

    [Theory]
    [InlineData("10.0.0.0/8", "private or reserved")]
    [InlineData("8.0.0.0/7", "too wide")]
    [InlineData("81.2.69.0/24", "your own address")]
    [InlineData("rmm.example.com", "Enter an IP address")]
    public async Task A_block_that_would_hurt_the_instance_or_the_admin_is_refused(string network, string problem)
    {
        await ClearAsync();
        var result = await Security.BlockAsync(Admin(), network, "attack", null);
        Assert.False(result.Success);
        Assert.Contains(problem, result.Problem);
    }

    [Fact]
    public async Task A_block_and_an_allow_are_stored_canonically_and_audited_and_a_block_can_be_lifted()
    {
        await ClearAsync();
        Assert.True((await Security.BlockAsync(Admin(), " 81.2.70.77/24 ", "Credential stuffing from this range", TimeSpan.FromHours(24))).Success);
        Assert.False((await Security.BlockAsync(Admin(), "81.2.70.0/24", "again", null)).Success);
        Assert.True((await Security.AllowAsync(Admin(), "81.2.70.5", "Office of the customer")).Success);

        var blocks = await Security.ListBlocksAsync(Admin());
        var block = Assert.Single(blocks);
        Assert.Equal(("81.2.70.0/24", IpBlockSource.Manual, true), (block.Network, block.Source, block.Active));
        // PostgreSQL stores microseconds.
        Assert.InRange((block.ExpiresAt!.Value - _fixture.Database.Time.GetUtcNow().UtcDateTime.AddHours(24)).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.Equal("81.2.70.5/32", Assert.Single(await Security.ListAllowsAsync(Admin())).Network);

        Assert.True((await Security.RemoveBlockAsync(Admin(), block.Id)).Success);
        Assert.Empty(await Security.ListBlocksAsync(Admin()));

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var actions = await db.AuditEntries.Where(a => a.TargetType == "IpBlock" || a.TargetType == "IpAllow").Select(a => a.Action).ToListAsync();
        Assert.Contains(AuditActions.IpBlockCreated, actions);
        Assert.Contains(AuditActions.IpBlockRemoved, actions);
        Assert.Contains(AuditActions.IpAllowCreated, actions);
    }

    [Fact]
    public async Task A_sign_in_remembers_its_network_and_a_failure_counts_for_its_address()
    {
        var user = await _fixture.Database.CreateUserAsync(FleetoRoles.Technician);
        var security = _fixture.Services.GetRequiredService<SignInSecurity>();
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Parse("81.2.73.44");
        context.Request.Headers.UserAgent = new string('a', 400);

        var first = _fixture.Database.Time.GetUtcNow().UtcDateTime;
        await security.SucceededAsync(user.Id, context);
        _fixture.Database.Time.Advance(TimeSpan.FromHours(3));
        context.Connection.RemoteIpAddress = IPAddress.Parse("81.2.73.200");
        await security.SucceededAsync(user.Id, context);
        security.Failed(context);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        var network = Assert.Single(await db.UserSignInNetworks.Where(n => n.UserId == user.Id).ToListAsync());
        Assert.Equal("81.2.73.0/24", network.Network);
        Assert.True(network.LastSeen - network.FirstSeen >= TimeSpan.FromHours(3) - TimeSpan.FromMilliseconds(1));
        Assert.InRange((network.FirstSeen - first).Duration(), TimeSpan.Zero, TimeSpan.FromMilliseconds(1));
        Assert.Equal(SignInSecurity.MaxUserAgentLength, SignInSecurity.UserAgent(context)!.Length);
        Assert.True(_fixture.Services.GetRequiredService<SecuritySignalRecorder>().Current()
            .ContainsKey((SecuritySignalKind.SignInFailed, "81.2.73.200/32", null)));
    }

    [Fact]
    public async Task An_incident_is_acknowledged_and_resolved_with_a_comment_whose_text_stays_out_of_the_audit_log()
    {
        Guid id;
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.SecurityIncidents.ExecuteDeleteAsync();
            var now = _fixture.Database.Time.GetUtcNow().UtcDateTime;
            var incident = new SecurityIncident
            {
                Id = Guid.NewGuid(), Key = "brute-force:81.2.74.1/32", Kind = SecurityIncidentKind.BruteForce, Severity = AlertSeverity.Critical,
                Title = "12 failed sign-ins from 81.2.74.1/32 in 10 minutes", Detail = "d", Network = "81.2.74.1/32", Count = 12, OpenedAt = now,
                LastSeenAt = now
            };
            db.SecurityIncidents.Add(incident);
            await db.SaveChangesAsync();
            id = incident.Id;
        }

        var tile = (await Security.GetTileAsync(Admin()))!;
        Assert.Equal((1, 1, "12 failed sign-ins from 81.2.74.1/32 in 10 minutes"), (tile.OpenIncidents, tile.Critical, tile.Latest));
        Assert.Null(await Security.GetTileAsync(WebFixture.Technician()));

        Assert.False((await Security.AcknowledgeAsync(Admin(), id, "  ")).Success);
        Assert.False((await Security.AcknowledgeAsync(WebFixture.Technician(), id, "seen")).Success);
        Assert.True((await Security.AcknowledgeAsync(Admin(), id, "Our own penetration test, ticket 4711")).Success);
        Assert.Equal(0, (await Security.GetTileAsync(Admin()))!.OpenIncidents);

        Assert.True((await Security.ResolveAsync(Admin(), id, "Test is over")).Success);
        Assert.False((await Security.ResolveAsync(Admin(), id, "again")).Success);
        var view = Assert.Single(await Security.ListIncidentsAsync(Admin()));
        Assert.Equal(("Our own penetration test, ticket 4711", "Test is over"), (view.AcknowledgeComment, view.ResolveComment));

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var details = await db.AuditEntries.Where(a => a.TargetId == id.ToString()).Select(a => a.DetailsJson).ToListAsync();
            Assert.Equal(2, details.Count);
            Assert.All(details, d => Assert.DoesNotContain("penetration", d));
        }
    }

    [Fact]
    public async Task A_blocklist_is_switched_and_CrowdSec_needs_its_integration_whose_password_stays_encrypted_and_out_of_the_audit_log()
    {
        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            await db.Settings.Where(s => s.Key == Infrastructure.Settings.SettingKeys.CrowdSecFeed).ExecuteDeleteAsync();
            await db.IpFeeds.ExecuteUpdateAsync(s => s.SetProperty(f => f.Enabled, false).SetProperty(f => f.RefreshRequestedAt, (DateTime?)null));
        }

        var feeds = await Security.ListFeedsAsync(Admin());
        Assert.Equal(IpFeeds.All.Count, feeds.Count);
        Assert.False(feeds.Single(f => f.Key == IpFeeds.CrowdSec).Configured);

        Assert.False((await Security.SetFeedEnabledAsync(WebFixture.Technician(), IpFeeds.SpamhausDrop, true)).Success);
        Assert.True((await Security.SetFeedEnabledAsync(Admin(), IpFeeds.SpamhausDrop, true)).Success);
        Assert.True((await Security.ListFeedsAsync(Admin())).Single(f => f.Key == IpFeeds.SpamhausDrop) is { Enabled: true, RefreshRequested: true });

        Assert.False((await Security.SetFeedEnabledAsync(Admin(), IpFeeds.CrowdSec, true)).Success);
        Assert.False((await Security.SetCrowdSecAsync(Admin(), "../evil", "user", "pw")).Success);
        Assert.True((await Security.SetCrowdSecAsync(Admin(), "abc-123", "fleeto", "s3cret-password")).Success);
        var crowdSec = (await Security.ListFeedsAsync(Admin())).Single(f => f.Key == IpFeeds.CrowdSec);
        Assert.Equal((true, true, "abc-123"), (crowdSec.Configured, crowdSec.Enabled, crowdSec.CrowdSecIntegrationId));

        await using (var db = _fixture.Database.DbFactory.CreateSystem())
        {
            var setting = await db.Settings.SingleAsync(s => s.Key == Infrastructure.Settings.SettingKeys.CrowdSecFeed);
            Assert.True(setting.IsEncrypted);
            Assert.DoesNotContain("s3cret", setting.Value);
            Assert.All(await db.AuditEntries.Where(a => a.Action == AuditActions.IpFeedChanged).Select(a => a.DetailsJson).ToListAsync(),
                d => Assert.DoesNotContain("s3cret", d));
        }

        Assert.True((await Security.RemoveCrowdSecAsync(Admin())).Success);
        Assert.False((await Security.ListFeedsAsync(Admin())).Single(f => f.Key == IpFeeds.CrowdSec).Configured);
    }

    [Fact]
    public async Task Automatic_blocking_is_switched_by_an_admin_and_audited()
    {
        Assert.False((await Security.SetAutomaticBlockingAsync(WebFixture.Technician(), false)).Success);
        Assert.True((await Security.SetAutomaticBlockingAsync(Admin(), false)).Success);
        Assert.False((await Security.GetSettingsAsync(Admin())).AutomaticBlocking);
        Assert.True((await Security.SetAutomaticBlockingAsync(Admin(), true)).Success);
        Assert.True((await Security.GetSettingsAsync(Admin())).AutomaticBlocking);

        await using var db = _fixture.Database.DbFactory.CreateSystem();
        Assert.True(await db.AuditEntries.CountAsync(a => a.Action == AuditActions.SecuritySettingsChanged) >= 2);
    }

    private static async Task<(WebApplication App, SecuritySignalRecorder Signals)> StartAsync(IpBlockList list, string remoteAddress)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var signals = new SecuritySignalRecorder("web", new FakeTimeProvider(DateTimeOffset.UtcNow));
        builder.Services.AddSingleton(list);
        builder.Services.AddSingleton(signals);
        builder.Services.AddRouting();
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
            return next(context);
        });
        app.UseMiddleware<IpBlockMiddleware>();
        app.UseRouting();
        app.UseMiddleware<ScannerDetectionMiddleware>();
        app.MapGet("/clients", () => "clients");
        await app.StartAsync();
        return (app, signals);
    }

    private static IpBlockList List()
    {
        var list = new IpBlockList(null!, new Testing.InMemoryNotificationBus(), TimeProvider.System, NullLogger<IpBlockList>.Instance);
        list.Load([("81.2.71.0/24", IpBlockSource.Manual, null)], ["81.2.71.9/32"]);
        return list;
    }

    [Fact]
    public async Task A_blocked_address_gets_403_before_anything_else_unless_it_is_allowed()
    {
        var (blocked, blockedSignals) = await StartAsync(List(), "81.2.71.8");
        await using (blocked)
        {
            var response = await blocked.GetTestClient().GetAsync("/clients");
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Contains("81.2.71.8", await response.Content.ReadAsStringAsync());
            Assert.Equal(1, blockedSignals.Current()[(SecuritySignalKind.Blocked, "81.2.71.8/32", null)]);
        }

        var (allowed, _) = await StartAsync(List(), "81.2.71.9");
        await using (allowed)
        {
            Assert.Equal(HttpStatusCode.OK, (await allowed.GetTestClient().GetAsync("/clients")).StatusCode);
        }
    }

    [Fact]
    public async Task A_probe_gets_a_bare_404_and_counts_and_so_does_enumeration()
    {
        var (app, signals) = await StartAsync(List(), "81.2.72.1");
        await using (app)
        {
            var client = app.GetTestClient();
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/.env")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/wp-login.php")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/no-such-page")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/clients?search=cmd.exe")).StatusCode);

            var counts = signals.Current();
            Assert.Equal(1, counts[(SecuritySignalKind.SensitivePath, "81.2.72.1/32", null)]);
            Assert.Equal(1, counts[(SecuritySignalKind.ScannerPath, "81.2.72.1/32", null)]);
            Assert.Equal(1, counts[(SecuritySignalKind.NotFound, "81.2.72.1/32", null)]);
            Assert.Equal(3, counts.Count);
        }
    }
}
