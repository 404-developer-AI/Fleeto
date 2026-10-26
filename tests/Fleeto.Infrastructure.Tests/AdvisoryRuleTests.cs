using Fleeto.Core.Domain;
using Fleeto.Core.Entities;
using Fleeto.Infrastructure.Advisories;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Security advisories (0.8.0 step 0): the RSS feed of the CCB is read into plain text with a link only to its own host, a summary
/// that only repeats the feed name is dropped, broken items are left out and a document that is not a feed is refused, entities are
/// never resolved, and a source that fails for 48 hours is an instance health problem.
/// </summary>
public sealed class AdvisoryRuleTests
{
    private static readonly AdvisorySourceInfo Ccb = AdvisorySources.Find(AdvisorySources.Ccb)!;

    // The shape of https://ccb.belgium.be/advisories.xml as fetched on 2026-10-26; titles made up.
    private const string CcbFeed = """
        <?xml version="1.0" encoding="utf-8"?>
        <rss xmlns:atom="http://www.w3.org/2005/Atom" version="2.0" xml:base="https://ccb.belgium.be/">
          <channel>
            <title>CCB Advisories Feed</title>
            <link>https://ccb.belgium.be/</link>
            <description><![CDATA[CCB Advisories]]></description>
            <language>en</language>
            <pubDate>Mon, 26 Oct 26 08:15:40 +0100</pubDate>
        <item>
          <title>Warning: RCE Vulnerability in Example DNS, CVE-2026-12345, Patch Immediately!
        </title>
          <link>https://ccb.belgium.be/advisories/warning-rce-example-dns</link>
          <description>CCB Advisories</description>
          <guid isPermaLink="true">https://ccb.belgium.be/advisories/warning-rce-example-dns</guid>
                  <pubDate>Mon, 26 Oct 2026 08:15:40 +0100
        </pubDate>
        </item>
        <item>
          <title>Warning: Critical Vulnerability in Example Gateway</title>
          <link>https://elsewhere.example/phish</link>
          <description>CCB Advisories</description>
          <guid isPermaLink="true">https://ccb.belgium.be/advisories/warning-example-gateway</guid>
          <pubDate>Sat, 24 Oct 2026 13:36:16 +0200</pubDate>
        </item>
        <item>
          <title>No date</title>
          <guid>https://ccb.belgium.be/advisories/no-date</guid>
        </item>
        <item>
          <title>Warning: RCE Vulnerability in Example DNS, CVE-2026-12345, Patch Immediately!</title>
          <guid isPermaLink="true">https://ccb.belgium.be/advisories/warning-rce-example-dns</guid>
          <pubDate>Mon, 26 Oct 2026 08:15:40 +0100</pubDate>
        </item>
          </channel>
        </rss>
        """;

    [Fact]
    public void The_ccb_feed_is_read_into_plain_text_with_links_only_to_its_own_host()
    {
        var parse = AdvisoryFeedParser.Parse(Ccb, CcbFeed);

        Assert.Equal(2, parse.Skipped);
        Assert.Collection(parse.Advisories,
            first =>
            {
                Assert.Equal("Warning: RCE Vulnerability in Example DNS, CVE-2026-12345, Patch Immediately!", first.Title);
                Assert.Equal("https://ccb.belgium.be/advisories/warning-rce-example-dns", first.Link);
                Assert.Equal(new DateTime(2026, 10, 26, 7, 15, 40, DateTimeKind.Utc), first.PublishedAt);
                Assert.Equal(["CVE-2026-12345"], first.Cves);
                // "CCB Advisories" in every item is the name of the feed, not a summary.
                Assert.Null(first.Summary);
            },
            second =>
            {
                Assert.Equal("https://ccb.belgium.be/advisories/warning-example-gateway", second.SourceId);
                Assert.Null(second.Link);
                Assert.Equal(new DateTime(2026, 10, 24, 11, 36, 16, DateTimeKind.Utc), second.PublishedAt);
            });
    }

    [Fact]
    public void A_document_that_is_not_a_feed_or_declares_entities_is_refused()
    {
        Assert.Throws<InvalidDataException>(() => AdvisoryFeedParser.Parse(Ccb, "<html><body>Access Denied</body></html>"));
        Assert.Throws<InvalidDataException>(() => AdvisoryFeedParser.Parse(Ccb, "not xml"));
        const string entities = """
            <?xml version="1.0"?>
            <!DOCTYPE rss [<!ENTITY secret SYSTEM "file:///etc/passwd">]>
            <rss version="2.0"><channel><item><title>&secret;</title><guid>x</guid><pubDate>Mon, 26 Oct 2026 08:15:40 +0100</pubDate></item></channel></rss>
            """;
        Assert.Throws<InvalidDataException>(() => AdvisoryFeedParser.Parse(Ccb, entities));
    }

    [Theory]
    [InlineData("https://ccb.belgium.be/advisories/x", "https://ccb.belgium.be/advisories/x")]
    [InlineData("https://www.ccb.belgium.be/advisories/x", "https://www.ccb.belgium.be/advisories/x")]
    [InlineData("http://ccb.belgium.be/advisories/x", null)]
    [InlineData("https://ccb.belgium.be.evil.example/x", null)]
    [InlineData("https://evilccb.belgium.be/x", null)]
    [InlineData("https://user@ccb.belgium.be/x", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("/advisories/x", null)]
    [InlineData(null, null)]
    public void Only_an_https_link_to_the_host_of_the_source_is_kept(string? link, string? expected) =>
        Assert.Equal(expected, AdvisoryRules.SafeLink(link, "ccb.belgium.be"));

    [Fact]
    public void Text_becomes_one_line_cves_are_uppercase_and_distinct_and_dates_are_read_in_utc()
    {
        Assert.Equal("a b c", AdvisoryRules.PlainText("  a\r\n\tb \u0007 c ", 100));
        Assert.Equal("abcd…", AdvisoryRules.PlainText("abcdefgh", 5));
        Assert.Equal(["CVE-2026-1234", "CVE-2025-1234567"], AdvisoryRules.Cves("cve-2026-1234, CVE-2026-1234 and CVE-2025-1234567; CVE-26-1"));

        Assert.True(AdvisoryFeedParser.TryDate("Mon, 26 Oct 26 08:15:40 +0100", out var shortYear));
        Assert.Equal(new DateTime(2026, 10, 26, 7, 15, 40, DateTimeKind.Utc), shortYear);
        Assert.True(AdvisoryFeedParser.TryDate("Tue, 20 Oct 2026 20:27:24 GMT", out var gmt));
        Assert.Equal(new DateTime(2026, 10, 20, 20, 27, 24, DateTimeKind.Utc), gmt);
        Assert.False(AdvisoryFeedParser.TryDate("yesterday", out _));
    }

    [Fact]
    public void A_source_that_fails_for_48_hours_is_an_instance_health_problem()
    {
        var now = new DateTime(2026, 10, 26, 12, 0, 0, DateTimeKind.Utc);
        AdvisorySourceState State(DateTime? failingSince, bool enabled = true) =>
            new(AdvisorySources.Ccb, enabled, now.AddDays(-3), failingSince, "CCB answered 503.");

        Assert.Empty(AdvisoryRules.HealthFindings([State(null)], now));
        Assert.Empty(AdvisoryRules.HealthFindings([State(now.AddHours(-47))], now));
        Assert.Empty(AdvisoryRules.HealthFindings([State(now.AddDays(-5), enabled: false)], now));
        var finding = Assert.Single(AdvisoryRules.HealthFindings([State(now.AddHours(-48))], now));
        Assert.Equal(("advisories:ccb", HealthComponent.Advisories, AlertSeverity.Warning), (finding.Key, finding.Component, finding.Severity));
        Assert.Contains("CCB answered 503.", finding.Detail);
        Assert.Contains("ccb.belgium.be", finding.Detail);
    }
}
