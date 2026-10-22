using System.Net;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Security;
using Fleeto.Testing;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Blocklists (0.7.0, security monitor step 3): a list is read into canonical networks without the ranges the instance uses
/// itself or networks that are too wide, a broken list is refused, and an address on a list is blocked unless it is allowed.
/// </summary>
public sealed class IpFeedRuleTests
{
    [Fact]
    public void A_plain_list_is_read_without_comments_private_ranges_or_networks_that_are_too_wide()
    {
        const string text = """
            # FireHOL level1
            ;comment
            0.0.0.0/8
            10.0.0.0/8
            172.16.0.0/12
            1.10.16.0/20
            81.2.69.160 # a C2 server
            81.2.69.160
            2001:db8::/32
            2a01:4f8:1::/48
            3.0.0.0/6
            not an address
            """;
        var parsed = IpFeedParser.Parse(IpFeedFormat.PlainList, text.Replace("\r", ""));
        Assert.Equal(["1.10.16.0/20", "2a01:4f8:1::/48", "81.2.69.160/32"], parsed.Networks.Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(6, parsed.Skipped);
    }

    [Fact]
    public void A_json_list_reads_the_cidr_of_every_line_and_skips_its_metadata()
    {
        const string text = """
            {"cidr":"1.10.16.0/20","sblid":"SBL256894","rir":"apnic"}
            {"cidr":"2.26.75.0/24","sblid":"SBL698389","rir":"ripencc"}
            {"type":"metadata","timestamp":1761000000,"size":2}
            """;
        var parsed = IpFeedParser.Parse(IpFeedFormat.JsonLinesCidr, text.Replace("\r", ""));
        Assert.Equal(2, parsed.Networks.Count);
        Assert.Equal(0, parsed.Skipped);
    }

    [Fact]
    public void A_list_that_shrinks_by_half_or_grows_too_large_is_refused()
    {
        Assert.Null(IpFeedParser.Refusal(0, 1));
        Assert.Null(IpFeedParser.Refusal(50, 1));
        Assert.Null(IpFeedParser.Refusal(1000, 500));
        Assert.NotNull(IpFeedParser.Refusal(1000, 499));
        Assert.NotNull(IpFeedParser.Refusal(0, IpFeeds.MaxEntries + 1));
    }

    [Fact]
    public void An_address_on_a_list_is_blocked_unless_allowed_and_a_block_outranks_a_list()
    {
        var list = new IpBlockList(null!, new InMemoryNotificationBus(), TimeProvider.System, NullLogger<IpBlockList>.Instance);
        list.Load([("81.2.69.7/32", IpBlockSource.Manual, null)], ["81.2.69.9/32"], ["81.2.69.0/24", "10.0.0.0/8"]);
        Assert.Equal(1, list.FeedCount);
        Assert.Equal(IpVerdict.BlockedFeed, list.Check(IPAddress.Parse("81.2.69.1")));
        Assert.Equal(IpVerdict.Allowed, list.Check(IPAddress.Parse("81.2.69.9")));
        Assert.Equal(IpVerdict.BlockedManual, list.Check(IPAddress.Parse("81.2.69.7")));
        Assert.Equal(IpVerdict.None, list.Check(IPAddress.Parse("10.1.2.3")));
    }

    [Fact]
    public void Every_list_of_the_catalog_has_a_source_and_terms()
    {
        Assert.Equal(IpFeeds.All.Count, IpFeeds.All.Select(f => f.Key).Distinct().Count());
        Assert.All(IpFeeds.All, f =>
        {
            Assert.StartsWith("https://", f.TermsUrl);
            Assert.All(f.Urls, u => Assert.StartsWith("https://", u));
            Assert.True(f.Key == IpFeeds.CrowdSec || f.Urls.Count > 0);
        });
        Assert.False(IpFeeds.Find(IpFeeds.CrowdSec)!.DefaultEnabled);
    }
}
