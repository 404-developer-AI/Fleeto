using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Authentication;
using Fleeto.Core.Entities;
using Fleeto.Core.Performance;
using Fleeto.Infrastructure.Performance;
using Fleeto.Testing;
using Fleeto.Workers.Health;
using Fleeto.Workers.Options;
using Fleeto.Workers.Performance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace Fleeto.Workers.Tests;

/// <summary>
/// The performance monitor in the workers, step 3 (0.7.0): the edge check judges what the instance URL answers, the probe loop runs
/// what admins ask for once and expires what waited too long, the benchmark measures CPU, disk and database, and a critical finding
/// becomes a problem of Instance health that stays quiet for its cooldown after it resolved.
/// </summary>
[Collection(WorkersCollection.Name)]
public sealed class PerformanceProbeTests
{
    private readonly WorkersFixture _fixture;

    public PerformanceProbeTests(WorkersFixture fixture) => _fixture = fixture;

    // ---- The edge check ----

    private sealed class Responder(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Page()
    {
        var html = """
            <html><head>
            <link href="_content/MudBlazor/MudBlazor.min.css" rel="stylesheet" />
            <link rel="stylesheet" href="app.ab12cd.css" />
            </head><body>Sign in</body></html>
            """;
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version20, Content = new StringContent(html) };
        response.Content.Headers.ContentEncoding.Add("br");
        response.Headers.TryAddWithoutValidation("Strict-Transport-Security", "max-age=2592000");
        response.Headers.TryAddWithoutValidation("Content-Security-Policy", "default-src 'self'");
        return response;
    }

    [Fact]
    public async Task The_edge_check_follows_the_sign_in_redirect_and_judges_the_page_and_a_fingerprinted_stylesheet()
    {
        var handler = new Responder(request => request.RequestUri!.AbsolutePath switch
        {
            "/" => new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("/login", UriKind.Relative) } },
            "/login" => Page(),
            "/app.ab12cd.css" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[4096]),
                Headers = { CacheControl = CacheControlHeaderValue.Parse("max-age=31536000, immutable") }
            },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound)
        });
        var check = new PerfEdgeCheck(handler, (_, _) => Task.FromResult(new List<PerfProbeItem> { new("DNS", "1 ms", null) }));

        var items = await check.RunAsync("rmm.test.example", CancellationToken.None);

        Assert.Equal(["/", "/login", "/app.ab12cd.css"], handler.Requests.Select(u => u.AbsolutePath));
        Assert.All(handler.Requests, u => Assert.Equal("rmm.test.example", u.Host));
        var byName = items.ToDictionary(i => i.Name);
        Assert.Equal("DNS", items[0].Name);
        Assert.Equal((true, "HTTP/2"), (byName["HTTP version"].Good!.Value, byName["HTTP version"].Value));
        Assert.True(byName["HSTS"].Good);
        Assert.True(byName["Content Security Policy"].Good);
        Assert.True(byName["Page compression"].Good);
        Assert.False(byName["Static file compression"].Good);
        Assert.True(byName["Static file caching"].Good);
        Assert.DoesNotContain(items, i => i.Name == "Keep-alive");
    }

    [Fact]
    public void The_edge_check_names_what_is_wrong_and_how_to_fix_it()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK) { Version = HttpVersion.Version11, Content = new ByteArrayContent(new byte[50_000]) };
        response.Headers.ConnectionClose = true;
        var items = PerfEdgeCheck.PageItems(response.Version, response.Headers, response.Content.Headers).ToDictionary(i => i.Name);
        Assert.False(items["HTTP version"].Good);
        Assert.False(items["Keep-alive"].Good);
        Assert.False(items["Page compression"].Good);
        Assert.False(items["HSTS"].Good);
        Assert.False(items["Content Security Policy"].Good);
        Assert.All(items.Values.Where(i => i.Good == false), i => Assert.False(string.IsNullOrWhiteSpace(i.Advice)));

        var asset = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) };
        asset.Headers.CacheControl = CacheControlHeaderValue.Parse("no-cache");
        var assetItems = PerfEdgeCheck.AssetItems(200, asset.Headers, asset.Content.Headers).ToDictionary(i => i.Name);
        Assert.Null(assetItems["Static file compression"].Good);
        Assert.False(assetItems["Static file caching"].Good);
        Assert.False(Assert.Single(PerfEdgeCheck.AssetItems(404, asset.Headers, asset.Content.Headers)).Good);

        var now = new DateTime(2026, 10, 26, 0, 0, 0, DateTimeKind.Utc);
        var tls = PerfEdgeCheck.TlsItems(SslProtocols.Tls13, "h2", SslPolicyErrors.None, now.AddDays(60), now).ToDictionary(i => i.Name);
        Assert.True(tls["TLS version"].Good);
        Assert.True(tls["HTTP/2 offered"].Good);
        Assert.True(tls["Certificate"].Good);
        var expiring = PerfEdgeCheck.TlsItems(SslProtocols.Tls12, "http/1.1", SslPolicyErrors.None, now.AddDays(3), now).ToDictionary(i => i.Name);
        Assert.True(expiring["TLS version"].Good);
        Assert.False(expiring["HTTP/2 offered"].Good);
        Assert.False(expiring["Certificate"].Good);
        Assert.False(PerfEdgeCheck.TlsItems(SslProtocols.Tls13, "h2", SslPolicyErrors.RemoteCertificateNameMismatch, null, now).Last().Good);

        Assert.Equal(31_536_000, PerfEdgeCheck.HstsAge("max-age=31536000; includeSubDomains"));
        Assert.Equal(0, PerfEdgeCheck.HstsAge("includeSubDomains"));
        Assert.Null(PerfEdgeCheck.StylesheetOf("<html></html>"));
    }

    // ---- The probe loop ----

    private PerfProbeService Probes(PerfEdgeCheck? edge = null) =>
        new(_fixture.Db.DbFactory, _fixture.Db.Bus, _fixture.Db.DataSource,
            MsOptions.Create(new BackupOptions { TempDirectory = Path.Combine(Path.GetTempPath(), "fleeto-tests-" + Guid.NewGuid().ToString("N")[..8]) }),
            _fixture.Heartbeat(), _fixture.Db.Time, NullLogger<PerfProbeService>.Instance, edge);

    private async Task<Guid> RequestAsync(PerfProbeKind kind, DateTime? at = null)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var probe = new PerfProbe { Id = Guid.NewGuid(), Kind = kind, RequestedAt = at ?? _fixture.Now };
        db.PerfProbes.Add(probe);
        await db.SaveChangesAsync();
        return probe.Id;
    }

    private async Task<PerfProbe> ReadAsync(Guid id)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        return await db.PerfProbes.AsNoTracking().SingleAsync(p => p.Id == id);
    }

    [Fact]
    public async Task A_requested_probe_runs_once_and_one_that_waited_too_long_expires()
    {
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.PerfProbes.ExecuteDeleteAsync();
        }

        var handler = new Responder(_ => Page());
        var hosts = new List<string>();
        var edge = new PerfEdgeCheck(handler, (fqdn, _) =>
        {
            hosts.Add(fqdn);
            return Task.FromResult(new List<PerfProbeItem>());
        });
        var expired = await RequestAsync(PerfProbeKind.EdgeCheck, _fixture.Now - TimeSpan.FromMinutes(30));
        var id = await RequestAsync(PerfProbeKind.EdgeCheck);
        var service = Probes(edge);

        Assert.True(await service.ProcessAsync(CancellationToken.None));
        Assert.False(await service.ProcessAsync(CancellationToken.None));

        Assert.Equal([TestDatabase.Fqdn], hosts);
        var done = await ReadAsync(id);
        Assert.NotNull(done.CompletedAt);
        Assert.Null(done.Problem);
        Assert.Contains(PerformanceReader.ParseItems(done.ItemsJson), i => i.Name == "HTTP version");

        var late = await ReadAsync(expired);
        Assert.Null(late.StartedAt);
        Assert.Contains("within 10 minutes", late.Problem);
    }

    [Fact]
    public async Task The_benchmark_measures_cpu_disk_and_database()
    {
        var id = await RequestAsync(PerfProbeKind.Benchmark);
        var service = Probes();
        while (await service.ProcessAsync(CancellationToken.None))
        {
        }

        var probe = await ReadAsync(id);
        Assert.Null(probe.Problem);
        var names = PerformanceReader.ParseItems(probe.ItemsJson).Select(i => i.Name).ToList();
        Assert.Equal(["CPU, one core", "Disk, sequential write", "Disk, fsync", "Database round trip"], names);
    }

    [Fact]
    public void A_failed_probe_says_why_without_its_exception_text()
    {
        var problem = PerfProbeService.Problem(PerfProbeKind.EdgeCheck, new HttpRequestException("secret detail 10.0.0.1"));
        Assert.DoesNotContain("10.0.0.1", problem, StringComparison.Ordinal);
        Assert.Contains("did not answer over HTTPS", problem, StringComparison.Ordinal);
        Assert.Contains("InvalidCastException", PerfProbeService.Problem(PerfProbeKind.Benchmark, new InvalidCastException()), StringComparison.Ordinal);
    }

    // ---- Instance health ----

    private InstanceHealthService Health() =>
        new(_fixture.Db.DbFactory, _fixture.Settings(), new HostMetricsReader(), _fixture.Heartbeat(), _fixture.Db.Bus, _fixture.Db.Time,
            NullLogger<InstanceHealthService>.Instance);

    private async Task SlowPageAsync(DateTime minute)
    {
        await using var db = _fixture.Db.DbFactory.CreateSystem();
        var histogram = new int[PerfHistogram.BucketCount];
        histogram[PerfHistogram.BucketOf(8_000)] = 50;
        db.PerfMinutes.Add(new PerfMinute
        {
            Minute = PerfRecorder.MinuteOf(minute), Component = "web", Kind = PerfKind.Interaction, Key = "/perf-health-test", SubKey = "load", Count = 50,
            Sum = 400_000, Min = 8_000, Max = 8_000, Histogram = PerfHistogram.Trim(histogram)
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task A_critical_finding_opens_a_problem_of_instance_health_which_stays_quiet_for_its_cooldown_after_it_resolved()
    {
        var key = PerformanceHealth.KeyOf("code-slow-interaction:/perf-health-test:load");
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            await db.InstanceHealthIssues.Where(i => i.Component == HealthComponent.Performance).ExecuteDeleteAsync();
            await db.PerfMinutes.Where(m => m.Key == "/perf-health-test").ExecuteDeleteAsync();
        }

        await SlowPageAsync(_fixture.Now.AddMinutes(-10));
        var service = Health();
        var findings = await service.SampleAsync(Fleeto.Core.Domain.HostHealth.Unknown, CancellationToken.None);
        Assert.Contains(findings, f => f.Key == key);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            var issue = await db.InstanceHealthIssues.SingleAsync(i => i.Key == key && i.ResolvedAt == null);
            Assert.Equal((HealthComponent.Performance, AlertSeverity.Warning), (issue.Component, issue.Severity));
            Assert.Contains("/perf-health-test", issue.Title, StringComparison.Ordinal);
            await db.PerfMinutes.Where(m => m.Key == "/perf-health-test").ExecuteDeleteAsync();
        }

        // Judged again after 15 minutes: the page is fine, and the problem resolves once it stayed away 15 minutes.
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await service.SampleAsync(Fleeto.Core.Domain.HostHealth.Unknown, CancellationToken.None);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.NotNull((await db.InstanceHealthIssues.SingleAsync(i => i.Key == key)).ResolvedAt);
        }

        // Slow again within the cooldown of 24 hours: no new problem.
        await SlowPageAsync(_fixture.Now.AddMinutes(-5));
        _fixture.Db.Time.Advance(TimeSpan.FromMinutes(16));
        await SlowPageAsync(_fixture.Now.AddMinutes(-5));
        var again = await service.SampleAsync(Fleeto.Core.Domain.HostHealth.Unknown, CancellationToken.None);
        Assert.DoesNotContain(again, f => f.Key == key);
        await using (var db = _fixture.Db.DbFactory.CreateSystem())
        {
            Assert.Equal(1, await db.InstanceHealthIssues.CountAsync(i => i.Key == key));
            await db.PerfMinutes.Where(m => m.Key == "/perf-health-test").ExecuteDeleteAsync();
            await db.InstanceHealthIssues.Where(i => i.Component == HealthComponent.Performance).ExecuteDeleteAsync();
        }
    }
}
