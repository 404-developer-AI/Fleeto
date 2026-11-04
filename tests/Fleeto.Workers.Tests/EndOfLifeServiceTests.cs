using System.Net;
using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Settings;
using Fleeto.Workers.VulnerabilityReporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The end-of-life report in the workers (0.8.0 step 4, VULNERABILITIES.md §12.4): the dates of endoflife.date are fetched once a day
/// and replaced per product, a broken answer keeps what was fetched, the report judges every managed endpoint (never an agent-only one),
/// counts Ubuntu Pro and a marked extension, runs on its schedule once, and its message goes to the channels that chose it, holding the
/// clients each routes.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class EndOfLifeServiceTests
{
    private readonly WorkersFixture _fixture;

    public EndOfLifeServiceTests(WorkersFixture fixture) => _fixture = fixture;

    // Dates that are still to come lie far ahead: the fake clock is shared by every test of the collection, and others move it forward.
    /// <summary>Answers every product of endoflife.date with a release list in the shape of the API v1 (checked 2026-11-04).</summary>
    private sealed class FakeEndOfLifeDate : HttpMessageHandler
    {
        public Dictionary<string, string> Bodies { get; } = new(StringComparer.Ordinal)
        {
            ["windows"] = Product(("11-25h2-w", "11 25H2 (W)", "2099-10-12", null, "10.0.26200"), ("10-22h2", "10 22H2", "2025-10-14", "2099-10-10", "10.0.19045")),
            ["windows-server"] = Product(("2022", "Windows Server 2022 (LTSC)", "2099-10-14", null, "10.0.20348")),
            ["ubuntu"] = Product(("24.04", "24.04 'Noble Numbat' (LTS)", "2099-05-31", "2099-04-25", "24.04.5"),
                ("20.04", "20.04 'Focal Fossa' (LTS)", "2025-05-29", "2099-04-02", "20.04.6")),
            ["debian"] = Product(("12", "12 (Bookworm)", "2099-06-30", "2099-06-30", "12.15")),
            ["almalinux"] = Product(("9", "9", "2099-05-31", null, "9.8")),
            ["proxmox-ve"] = Product(("9", "9", null, null, "9.2"), ("8", "8", "2026-08-31", null, "8.4")),
        };

        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);
            var slug = url[EndOfLifeService.ApiBase.Length..];
            return Task.FromResult(new HttpResponseMessage(Status)
            {
                Content = new StringContent(Bodies.GetValueOrDefault(slug, "{}"), Encoding.UTF8, "application/json")
            });
        }
    }

    private static string Product(params (string Name, string Label, string? Eol, string? Eoes, string Latest)[] releases) =>
        "{\"schema_version\":\"1.2.1\",\"result\":{\"name\":\"x\",\"releases\":[" + string.Join(',', releases.Select(r =>
            $"{{\"name\":\"{r.Name}\",\"label\":\"{r.Label.Replace("'", "\\u0027")}\",\"eolFrom\":{Json(r.Eol)},\"eoesFrom\":{Json(r.Eoes)},\"latest\":{{\"name\":\"{r.Latest}\"}}}}")) +
        "]}}";

    private static string Json(string? value) => value is null ? "null" : $"\"{value}\"";

    private EndOfLifeService Service(FakeEndOfLifeDate handler) =>
        new(_fixture.Db.DbFactory, _fixture.Settings(), _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<EndOfLifeService>.Instance, handler);

    private async Task ResetAsync()
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.EndOfLifeReports.ExecuteDeleteAsync();
        await db.EndOfLifeRefreshRequests.ExecuteDeleteAsync();
        await db.EndOfLifeReleases.ExecuteDeleteAsync();
        await db.EndOfLifeSources.ExecuteDeleteAsync();
        await db.Settings.Where(s => s.Key == SettingKeys.EndOfLifeSchedule || s.Key == SettingKeys.EndOfLifePeriodMonths).ExecuteDeleteAsync();
        await db.NotificationChannels.Where(c => c.EndOfLifeReports).ExecuteUpdateAsync(s => s.SetProperty(c => c.EndOfLifeReports, false));
    }

    private async Task<Endpoint> EndpointAsync(Site site, string hostname, string platform, string name, string version,
        EndpointTier tier = EndpointTier.Managed, string? packageInfo = null, string? extension = null)
    {
        var endpoint = await _fixture.Db.CreateEndpointAsync(site, tier, hostname);
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        await db.Endpoints.Where(e => e.Id == endpoint.Id).ExecuteUpdateAsync(s => s
            .SetProperty(e => e.OsPlatform, platform).SetProperty(e => e.OsName, name).SetProperty(e => e.OsVersion, version)
            .SetProperty(e => e.EndOfLifeExtensionRelease, extension));
        if (packageInfo is not null)
        {
            db.InventorySnapshots.Add(new InventorySnapshot
            {
                EndpointId = endpoint.Id, ClientId = endpoint.ClientId, ReceivedAt = _fixture.Now, PackageInfoJson = packageInfo
            });
            await db.SaveChangesAsync();
        }

        return endpoint;
    }

    [Fact]
    public async Task The_dates_are_fetched_once_a_day_and_a_broken_answer_keeps_the_dates_before()
    {
        await ResetAsync();
        var handler = new FakeEndOfLifeDate();
        var service = Service(handler);

        var fetched = await service.FetchDueAsync(EndOfLife.FetchEvery, CancellationToken.None);
        Assert.Equal(6, fetched.Count);
        Assert.DoesNotContain(EndOfLifeProduct.SynologyDsm, fetched);
        Assert.Contains(EndOfLifeService.ApiBase + "proxmox-ve", handler.Requests);
        Assert.Empty(await service.FetchDueAsync(EndOfLife.FetchEvery, CancellationToken.None));

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var windows = await db.EndOfLifeReleases.AsNoTracking().SingleAsync(r => r.Product == EndOfLifeProduct.Windows && r.Release == "10-22h2");
            Assert.Equal((new DateOnly(2025, 10, 14), new DateOnly(2099, 10, 10), "10.0.19045"), (windows.EndOfLifeOn, windows.ExtensionUntil, windows.LatestBuild));
            // Only Windows releases carry a build.
            Assert.Null((await db.EndOfLifeReleases.AsNoTracking().SingleAsync(r => r.Product == EndOfLifeProduct.Ubuntu && r.Release == "24.04")).LatestBuild);
            Assert.Null((await db.EndOfLifeReleases.AsNoTracking().SingleAsync(r => r.Product == EndOfLifeProduct.ProxmoxVe && r.Release == "9")).EndOfLifeOn);
        }

        // A day later Ubuntu answers with nothing usable, and Proxmox VE with a server error: the dates before stay, and they say why.
        _fixture.Db.Time.Advance(EndOfLife.FetchEvery);
        handler.Bodies["ubuntu"] = "{\"result\":{\"releases\":[]}}";
        handler.Bodies["proxmox-ve"] = "<html>";
        await service.FetchDueAsync(EndOfLife.FetchEvery, CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(2, await db.EndOfLifeReleases.CountAsync(r => r.Product == EndOfLifeProduct.Ubuntu));
            Assert.Equal(2, await db.EndOfLifeReleases.CountAsync(r => r.Product == EndOfLifeProduct.ProxmoxVe));
            var ubuntu = await db.EndOfLifeSources.AsNoTracking().SingleAsync(s => s.Product == EndOfLifeProduct.Ubuntu);
            Assert.Contains("no releases", ubuntu.Problem);
            Assert.NotNull(ubuntu.FailingSince);
            Assert.Null((await db.EndOfLifeSources.AsNoTracking().SingleAsync(s => s.Product == EndOfLifeProduct.Debian)).Problem);
        }

        // After 7 days without a fetch it is an instance health problem.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var states = await db.EndOfLifeSources.AsNoTracking()
                .Select(s => new EndOfLifeSourceState(s.Product.ToString(), s.LastSuccessAt, s.FailingSince, s.Problem)).ToListAsync();
            Assert.Empty(EndOfLife.HealthFindings(states, _fixture.Now));
            var finding = Assert.Single(EndOfLife.HealthFindings(states.Where(s => s.Product == nameof(EndOfLifeProduct.Ubuntu)), _fixture.Now.AddDays(7)));
            Assert.Equal("end-of-life:Ubuntu", finding.Key);
        }
    }

    [Fact]
    public async Task The_report_judges_every_managed_endpoint_and_counts_Ubuntu_Pro_and_a_marked_extension()
    {
        await ResetAsync();
        var service = Service(new FakeEndOfLifeDate());
        await service.FetchDueAsync(EndOfLife.FetchEvery, CancellationToken.None);
        var (client, site) = await _fixture.CreateClientAndSiteAsync();
        const string pro = """{"distributionId":"ubuntu","distributionVersion":"20.04","packageManager":"dpkg","ubuntuProServices":["esm-infra","esm-apps"],"livepatchActive":false,"livepatchFixes":[],"restartRequired":false}""";
        var modern = await EndpointAsync(site, "WS-NEW", "windows", "Windows 11 Pro", "10.0.26200");
        var old = await EndpointAsync(site, "WS-OLD", "windows", "Windows 10 Pro", "10.0.19045");
        var esu = await EndpointAsync(site, "WS-ESU", "windows", "Windows 10 Pro", "10.0.19045", extension: "10-22h2");
        var focal = await EndpointAsync(site, "SRV-FOCAL", "linux", "Ubuntu 20.04.6 LTS", "5.4.0", packageInfo: pro);
        var focalNoPro = await EndpointAsync(site, "SRV-FOCAL2", "linux", "Ubuntu 20.04.6 LTS", "5.4.0");
        var pve8 = await EndpointAsync(site, "PVE-8", "linux", "Proxmox VE 8.4.21 (Debian GNU/Linux 12 (bookworm))", "6.8.12");
        var pve9 = await EndpointAsync(site, "PVE-9", "linux", "Proxmox VE 9.2.3 (Debian GNU/Linux 13 (trixie))", "6.14.8");
        var nas = await EndpointAsync(site, "NAS-00", "linux", "Synology DSM 7.1.1-42962 Update 8", "4.4.180+");
        var fedora = await EndpointAsync(site, "WS-FEDORA", "linux", "Fedora Linux 41", "6.11.0");
        var free = await EndpointAsync(site, "WS-FREE", "windows", "Windows 10 Pro", "10.0.19045", EndpointTier.AgentOnly);

        var reportId = await service.RunReportAsync(false, Guid.NewGuid(), "Tester", CancellationToken.None);

        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var report = await db.EndOfLifeReports.AsNoTracking().SingleAsync(r => r.Id == reportId);
        Assert.Equal((false, "Tester", EndOfLife.DefaultPeriodMonths), (report.Scheduled, report.RequestedByName, report.PeriodMonths));
        var rows = await db.EndOfLifeReportRows.AsNoTracking().Where(r => r.ReportId == reportId && r.ClientId == client.Id).ToDictionaryAsync(r => r.EndpointId);
        Assert.False(rows.ContainsKey(modern.Id));
        Assert.False(rows.ContainsKey(free.Id));
        Assert.Equal(EndOfLifeStatus.EndOfLife, rows[old.Id].Status);
        Assert.Equal(new DateOnly(2025, 10, 14), rows[old.Id].EndOfLifeOn);
        Assert.Equal((EndOfLifeStatus.Extended, true), (rows[esu.Id].Status, rows[esu.Id].ExtensionCounted));
        Assert.Equal(EndOfLifeStatus.Extended, rows[focal.Id].Status);
        Assert.Equal(EndOfLifeStatus.EndOfLife, rows[focalNoPro.Id].Status);
        Assert.Equal(EndOfLifeStatus.EndOfLife, rows[pve8.Id].Status);
        Assert.Equal(EndOfLifeStatus.NoDate, rows[pve9.Id].Status);
        Assert.Equal((EndOfLifeStatus.EndOfLife, EndOfLifeProduct.SynologyDsm, "7.1"), (rows[nas.Id].Status, rows[nas.Id].Product, rows[nas.Id].Release));
        Assert.Equal((EndOfLifeStatus.NotRecognised, (EndOfLifeProduct?)null), (rows[fedora.Id].Status, rows[fedora.Id].Product));

        var counts = await db.EndOfLifeReportCounts.AsNoTracking().Where(c => c.ReportId == reportId && c.ClientId == client.Id)
            .ToDictionaryAsync(c => c.Status, c => c.Endpoints);
        Assert.Equal(1, counts[EndOfLifeStatus.Supported]);
        Assert.Equal(4, counts[EndOfLifeStatus.EndOfLife]);
        Assert.Equal(2, counts[EndOfLifeStatus.Extended]);
        Assert.Equal(9, counts.Values.Sum());
    }

    [Fact]
    public async Task A_scheduled_run_happens_once_and_its_message_holds_the_clients_each_channel_routes()
    {
        await ResetAsync();
        var handler = new FakeEndOfLifeDate();
        var service = Service(handler);
        await _fixture.ClearOutboxAsync();
        var (first, firstSite) = await _fixture.CreateClientAndSiteAsync();
        var (second, secondSite) = await _fixture.CreateClientAndSiteAsync();
        await EndpointAsync(firstSite, "WS-FIRST", "windows", "Windows 10 Pro", "10.0.19045");
        await EndpointAsync(secondSite, "WS-SECOND", "windows", "Windows 10 Pro", "10.0.19045");

        var everything = $"all-{Guid.NewGuid():N}@test.example";
        var onlyFirst = $"first-{Guid.NewGuid():N}@test.example";
        var now = _fixture.Now;
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var routed = new NotificationChannel
            {
                Id = Guid.NewGuid(), Name = "EOL first", Recipients = onlyFirst, AllClients = false, EndOfLifeReports = true, CreatedAt = now, UpdatedAt = now
            };
            routed.Clients.Add(new NotificationChannelClient { NotificationChannelId = routed.Id, ClientId = first.Id });
            db.NotificationChannels.AddRange(
                new NotificationChannel { Id = Guid.NewGuid(), Name = "EOL all", Recipients = everything, EndOfLifeReports = true, CreatedAt = now, UpdatedAt = now },
                routed);
            await db.SaveChangesAsync();
        }

        // The first pass stores the default schedule (monthly) and runs nothing.
        Assert.False(await service.ScheduledRunDueAsync(_fixture.Now, CancellationToken.None));
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.True(await db.Settings.AnyAsync(s => s.Key == SettingKeys.EndOfLifeSchedule));
        }

        // A daily schedule at an hour that already passed today runs tomorrow, once.
        await _fixture.Settings().SetStringAsync(SettingKeys.EndOfLifeSchedule,
            EndOfLife.ScheduleJson(new EndOfLifeSchedule(EndOfLifeScheduleKind.Daily, DayOfWeek.Monday, 1, 0, "UTC")), false, null);
        Assert.False(await service.ScheduledRunDueAsync(_fixture.Now, CancellationToken.None));
        _fixture.Db.Time.Advance(TimeSpan.FromDays(1));
        Assert.True(await service.ScheduledRunDueAsync(_fixture.Now, CancellationToken.None));
        await service.RunPassAsync(CancellationToken.None);
        Assert.False(await service.ScheduledRunDueAsync(_fixture.Now, CancellationToken.None));

        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var report = await db.EndOfLifeReports.AsNoTracking().SingleAsync(r => r.Scheduled);
            Assert.True(report.Notified);
            var toAll = await db.OutboxEmails.AsNoTracking().SingleAsync(e => e.ToAddress == everything);
            Assert.StartsWith("End-of-life report:", toAll.Subject);
            Assert.Contains(first.Code, toAll.TextBody);
            Assert.Contains(second.Code, toAll.TextBody);
            var toFirst = await db.OutboxEmails.AsNoTracking().SingleAsync(e => e.ToAddress == onlyFirst);
            Assert.Contains("WS-FIRST", toFirst.TextBody);
            Assert.DoesNotContain("WS-SECOND", toFirst.TextBody);
            Assert.DoesNotContain(second.Code, toFirst.TextBody);
        }

        // A refresh by hand makes a report without a message.
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            db.EndOfLifeRefreshRequests.Add(new EndOfLifeRefreshRequest { Id = Guid.NewGuid(), RequestedAt = _fixture.Now, UserId = Guid.NewGuid(), UserName = "Tester" });
            await db.SaveChangesAsync();
        }

        await _fixture.ClearOutboxAsync();
        await service.RunPassAsync(CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(2, await db.EndOfLifeReports.CountAsync());
            Assert.False(await db.EndOfLifeRefreshRequests.AnyAsync(r => r.HandledAt == null));
            Assert.False(await db.OutboxEmails.AnyAsync(e => e.SentAt == null && (e.ToAddress == everything || e.ToAddress == onlyFirst)));
        }
    }
}
