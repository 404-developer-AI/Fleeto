using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Services;
using Fleeto.Workers.Integrations;
using Microsoft.EntityFrameworkCore;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The patch scans of the Fleeto agent become patch state (0.9.0 step 1): the newest scan of an endpoint is its state and missing
/// updates, a package that fixes an open finding carries its CVEs and severity, a scan that read nothing never makes an endpoint look
/// compliant, an endpoint Action1 patches is left alone, and the "Missing updates" check, "Scan now" and the stale alert follow.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PatchScanWriterTests
{
    private readonly WorkersFixture _fixture;

    public PatchScanWriterTests(WorkersFixture fixture) => _fixture = fixture;

    private static PatchScanUpdate Update(string name, string version, string installed = "1.0", bool security = false, string origin = "jammy-updates",
        bool removal = false, string severity = "") =>
        new($"{name}:amd64", name, version, installed, security, origin, string.Empty, severity, false, removal);

    private async Task<Endpoint> LinuxEndpointAsync(string osName = "Ubuntu 22.04.4 LTS", string action1AgentId = "")
    {
        await _fixture.Db.LoadTestLicenseAsync(1000);
        var (_, site) = await _fixture.CreateClientAndSiteAsync();
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, EndpointTier.Managed, "LNX-" + Guid.NewGuid().ToString("N")[..6], EndpointClass.Server);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, "linux").SetProperty(e => e.OsName, osName).SetProperty(e => e.IsOnline, true)
            .SetProperty(e => e.AgentVersion, PatchScanRules.MinimumAgentVersion));
        db.InventorySnapshots.Add(new InventorySnapshot
        {
            EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = _fixture.Now, Hash = "h", Action1AgentId = action1AgentId
        });
        await db.SaveChangesAsync();
        return await db.Endpoints.AsNoTracking().SingleAsync(e => e.Id == endpoint.Id);
    }

    private async Task<PatchScan> ScanAsync(Endpoint endpoint, IReadOnlyList<PatchScanUpdate> updates, string? problem = null, Guid? requestId = null,
        string tool = "apt")
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var scan = new PatchScan
        {
            Id = Guid.CreateVersion7(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, AgentScanId = Guid.NewGuid(), ReceivedAt = _fixture.Now,
            Tool = tool, Problem = problem, RestartRequired = true, UpdateCount = updates.Count, RequestId = requestId,
            AgentVersion = PatchScanRules.MinimumAgentVersion, UpdatesJson = PatchScanRules.Serialize(updates)
        };
        db.PatchScans.Add(scan);
        await db.SaveChangesAsync();
        return scan;
    }

    private async Task<(EndpointPatchState? State, List<EndpointMissingUpdate> Missing)> StateAsync(Guid endpointId)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return (await db.EndpointPatchStates.AsNoTracking().SingleOrDefaultAsync(s => s.EndpointId == endpointId),
            await db.EndpointMissingUpdates.AsNoTracking().Where(u => u.EndpointId == endpointId).OrderBy(u => u.Name).ToListAsync());
    }

    [Fact]
    public async Task The_newest_scan_becomes_the_patch_state_and_a_row_that_stays_keeps_when_it_was_first_seen()
    {
        var endpoint = await LinuxEndpointAsync();
        await ScanAsync(endpoint, [Update("libssl3", "3.0.2-0ubuntu1.12", "3.0.2-0ubuntu1.10", security: true, origin: "jammy-security"),
            Update("tzdata", "2024a"), Update("oldpkg", "", removal: true)]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        var (state, missing) = await StateAsync(endpoint.Id);
        Assert.NotNull(state);
        Assert.Equal(PatchEngine.Fleeto, state.Engine);
        Assert.Equal("apt", state.ScanTool);
        Assert.Equal(string.Empty, state.ExternalEndpointId);
        Assert.True(state.RebootRequired);
        // The removal is listed but is not a missing update.
        Assert.Equal((0, 2), (state.MissingCritical, state.MissingOther));
        Assert.Equal(["libssl3", "oldpkg", "tzdata"], missing.Select(u => u.Name));
        Assert.Equal(PatchScanRules.SecurityType, missing[0].UpdateType);
        Assert.Equal("jammy-security", missing[0].Origin);
        Assert.True(missing[1].Removal);
        var firstSeen = missing[0].FirstSeenAt;
        var keptId = missing[0].Id;

        _fixture.Db.Time.Advance(TimeSpan.FromHours(6));
        await ScanAsync(endpoint, [Update("libssl3", "3.0.2-0ubuntu1.12", "3.0.2-0ubuntu1.10", security: true, origin: "jammy-security")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        (state, missing) = await StateAsync(endpoint.Id);
        var row = Assert.Single(missing);
        Assert.Equal((keptId, firstSeen), (row.Id, row.FirstSeenAt));
        Assert.Equal(_fixture.Now, state!.ProductLastSeenAt!.Value, TimeSpan.FromSeconds(1));
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        Assert.False(await db.PatchScans.AnyAsync(s => s.EndpointId == endpoint.Id && s.ProcessedAt == null));
    }

    [Fact]
    public async Task A_package_that_fixes_an_open_finding_carries_its_CVEs_its_severity_and_the_date_of_its_advisory()
    {
        var endpoint = await LinuxEndpointAsync();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndpointVulnerabilities.AddRange(
                Finding(endpoint, "CVE-2024-0001", "openssl", ["libssl3", "openssl"], "3.0.2-0ubuntu1.12", VulnerabilitySeverity.High, "USN-6000-1"),
                Finding(endpoint, "CVE-2024-0002", "openssl", ["libssl3", "openssl"], "3.0.2-0ubuntu1.11", VulnerabilitySeverity.Critical, "USN-5990-1"),
                // Fixed only by a newer version than the update installs: not fixed by it.
                Finding(endpoint, "CVE-2024-0003", "openssl", ["libssl3"], "3.0.2-0ubuntu1.20", VulnerabilitySeverity.Critical, "USN-7000-1"));
            var feed = "patch-test-" + Guid.NewGuid().ToString("N")[..8];
            db.VulnerabilityFeeds.Add(new VulnerabilityFeed { Key = feed });
            db.VulnerabilityFeedRecords.Add(new VulnerabilityFeedRecord
            {
                Feed = feed, Distribution = "ubuntu", Release = "22.04", Package = "openssl", Cve = "CVE-2024-0002",
                Status = VulnerabilityRecordStatus.Fixed, FixedVersion = "3.0.2-0ubuntu1.11", Advisory = "USN-5990-1",
                PublishedAt = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc)
            });
            await db.SaveChangesAsync();
        }

        await ScanAsync(endpoint, [Update("libssl3", "3.0.2-0ubuntu1.12", "3.0.2-0ubuntu1.10")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        var (state, missing) = await StateAsync(endpoint.Id);
        var row = Assert.Single(missing);
        Assert.Equal(["CVE-2024-0001", "CVE-2024-0002"], row.Cves);
        Assert.Equal(PatchSeverity.Critical, row.Severity);
        Assert.Equal(PatchScanRules.SecurityType, row.UpdateType);
        Assert.Equal(new DateOnly(2026, 10, 1), row.ReleaseDate);
        Assert.Equal((1, 0), (state!.MissingCritical, state.MissingOther));
    }

    private EndpointVulnerability Finding(Endpoint endpoint, string cve, string package, string[] binaries, string fixedVersion,
        VulnerabilitySeverity severity, string advisory) => new()
    {
        ClientId = endpoint.ClientId, EndpointId = endpoint.Id, Cve = cve, Package = package, Binaries = binaries, InstalledVersion = "3.0.2-0ubuntu1.10",
        FixedVersion = fixedVersion, State = VulnerabilityFixState.Fixable, Severity = severity, Advisory = advisory, FirstSeenAt = _fixture.Now
    };

    [Fact]
    public async Task A_scan_that_read_nothing_never_makes_the_endpoint_look_compliant_and_its_scans_turn_stale()
    {
        var endpoint = await LinuxEndpointAsync();
        await ScanAsync(endpoint, [], problem: "dpkg was interrupted. Run dpkg --configure -a on the endpoint.");
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        var (state, _) = await StateAsync(endpoint.Id);
        Assert.NotNull(state);
        Assert.Null(state.ProductLastSeenAt);
        Assert.Contains("dpkg was interrupted", state.ScanProblem);
        Assert.Equal(_fixture.Now, state.ScanFailingSince!.Value, TimeSpan.FromSeconds(1));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            // One failed scan of a new endpoint is not an alert yet.
            Assert.False(await db.Alerts.AnyAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState));
        }

        // Its scans keep failing for longer than the stale period: the alert opens with the problem.
        _fixture.Db.Time.Advance(PatchScanRules.StaleAfter + TimeSpan.FromHours(1));
        await _fixture.PatchScans().SweepAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState && a.State == AlertState.Open);
            Assert.Contains("dpkg was interrupted", alert.Detail);
        }

        await ScanAsync(endpoint, [Update("curl", "7.81.0-1ubuntu1.20")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);
        await ScanAsync(endpoint, [], problem: "Could not get the lock /var/lib/dpkg/lock-frontend.");
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        var (after, missing) = await StateAsync(endpoint.Id);
        // The failed scan kept what the scan before it read.
        Assert.Equal("curl", Assert.Single(missing).Name);
        Assert.Equal(1, after!.MissingOther);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var alert = await db.Alerts.AsNoTracking().SingleAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState);
            Assert.Equal((AlertState.Resolved, PatchScanWriter.FreshReason), (alert.State, alert.ResolvedReason));
        }

        _fixture.Db.Time.Advance(PatchScanRules.StaleAfter + TimeSpan.FromHours(1));
        await _fixture.PatchScans().SweepAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.True(await db.Alerts.AnyAsync(a => a.EndpointId == endpoint.Id && a.Kind == AlertKind.PatchState && a.State == AlertState.Open));
        }
    }

    [Fact]
    public async Task An_endpoint_Action1_patches_gets_no_Fleeto_state_and_one_that_got_an_Action1_agent_loses_it()
    {
        var action1 = await LinuxEndpointAsync(action1AgentId: "a1-agent");
        await ScanAsync(action1, [Update("curl", "7.81.0-1ubuntu1.20")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);
        Assert.Null((await StateAsync(action1.Id)).State);

        var endpoint = await LinuxEndpointAsync();
        await ScanAsync(endpoint, [Update("curl", "7.81.0-1ubuntu1.20")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);
        Assert.NotNull((await StateAsync(endpoint.Id)).State);

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.InventorySnapshots.Where(i => i.EndpointId == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(i => i.Action1AgentId, "a1-new"));
        }

        Assert.Equal(1, await _fixture.PatchScans().SweepAsync(CancellationToken.None));
        var (state, missing) = await StateAsync(endpoint.Id);
        Assert.Null(state);
        Assert.Empty(missing);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            // The configuration is signed again without the scan interval.
            Assert.True(await db.ConfigChangeEvents.AnyAsync(c => c.Scope == ConfigChangeScope.Endpoint && c.ScopeId == endpoint.Id));
        }
    }

    [Fact]
    public async Task An_agent_only_endpoint_gets_no_patch_state_from_a_scan()
    {
        var endpoint = await LinuxEndpointAsync();
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s.SetProperty(e => e.Tier, EndpointTier.AgentOnly));
        }

        await ScanAsync(endpoint, [Update("curl", "7.81.0-1ubuntu1.20")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        Assert.Null((await StateAsync(endpoint.Id)).State);
    }

    [Fact]
    public async Task A_scan_completes_the_requests_delivered_before_it()
    {
        var endpoint = await LinuxEndpointAsync();
        var request = new PatchScanRequest
        {
            Id = Guid.NewGuid(), ClientId = endpoint.ClientId, EndpointId = endpoint.Id, RequestedByUserId = Guid.NewGuid(), RequestedByName = "Tech",
            RequestedAt = _fixture.Now, ExpiresAt = _fixture.Now.AddMinutes(15), DeliveredAt = _fixture.Now
        };
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.PatchScanRequests.Add(request);
            await db.SaveChangesAsync();
        }

        await ScanAsync(endpoint, [], requestId: request.Id);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        await using var check = _fixture.Db.DbFactory.CreateSystem();
        Assert.NotNull((await check.PatchScanRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id)).CompletedAt);
        // An empty scan without a problem is a real answer: nothing is missing.
        var (state, _) = await StateAsync(endpoint.Id);
        Assert.True(state!.IsCompliant);
        Assert.NotNull(state.ProductLastSeenAt);
    }

    [Fact]
    public async Task A_missing_updates_check_on_Linux_counts_from_the_day_Fleeto_first_saw_an_update_without_an_advisory()
    {
        var endpoint = await LinuxEndpointAsync();
        var check = await _fixture.CreateCheckAsync(new Site { Id = endpoint.SiteId, ClientId = endpoint.ClientId }, CheckType.MissingUpdates,
            warning: 1, critical: 30, name: "Old Linux updates", parameters: """{"severity":"any"}""");
        await ScanAsync(endpoint, [Update("curl", "7.81.0-1ubuntu1.20")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);
        _fixture.Db.Time.Advance(TimeSpan.FromDays(3));
        await ScanAsync(endpoint, [Update("curl", "7.81.0-1ubuntu1.20")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var result = await db.CheckResults.AsNoTracking().Where(r => r.EndpointId == endpoint.Id && r.CheckDefinitionId == check.Id)
            .OrderByDescending(r => r.Time).FirstAsync();
        Assert.Equal(3, result.Value);
        Assert.Contains("curl", result.Detail);
    }

    [Fact]
    public async Task Every_form_of_the_engine_rule_agrees_on_a_scanned_endpoint()
    {
        var endpoint = await LinuxEndpointAsync(RemoteSessionRules.ProxmoxOsPrefix + "8.2.4");
        await ScanAsync(endpoint, [Update("pve-manager", "8.2.7")]);
        await _fixture.PatchScans().ProcessAsync(CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var row = await PatchEngines.Query(db, true).SingleAsync(r => r.EndpointId == endpoint.Id);
        Assert.Equal(EndpointPatchEngine.Fleeto, row.Engine);
        Assert.Equal(PatchEngine.Fleeto, (await StateAsync(endpoint.Id)).State!.Engine);
    }
}
