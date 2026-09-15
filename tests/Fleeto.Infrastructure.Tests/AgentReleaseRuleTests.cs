using System.Text;
using Fleeto.Core.Domain;
using Fleeto.Core.Entities;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// Rules of agent releases (0.2.1): version order equal to the Go agent and install.sh, the release manifest shape the gateway accepts, and the
/// update rings counted from installation, with pause and release to all.
/// </summary>
public sealed class AgentReleaseRuleTests
{
    private const string Manifest = """
        {"formatVersion":1,"version":"0.2.1","images":{"web":"sha256:0"},"agentBinaries":[
          {"component":"agent","platform":"windows","architecture":"amd64","file":"windows-amd64/fleeto-agent.exe","sha256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","size":10},
          {"component":"watchdog","platform":"linux","architecture":"arm64","file":"linux-arm64/fleeto-watchdog","sha256":"9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08","size":10}]}
        """;

    [Fact]
    public void Versions_order_like_semantic_versioning()
    {
        string[] ordered = ["0.1.0", "0.2.0-alpha.1", "0.2.0-alpha.2", "0.2.0-alpha.10", "0.2.0-beta", "0.2.0", "0.2.1", "0.10.0", "1.0.0"];
        for (var i = 0; i < ordered.Length - 1; i++)
        {
            Assert.True(SemanticVersion.IsOlder(ordered[i], ordered[i + 1]), $"{ordered[i]} should be older than {ordered[i + 1]}");
            Assert.False(SemanticVersion.IsOlder(ordered[i + 1], ordered[i]));
        }

        Assert.False(SemanticVersion.IsOlder("0.2.1", "0.2.1"));
        Assert.False(SemanticVersion.IsOlder("0.2.1+build.5", "0.2.1"));
        Assert.False(SemanticVersion.IsOlder("garbage", "0.2.1"));
        Assert.True(SemanticVersion.IsOlder("0.0.0-dev", "0.2.1"));
    }

    [Fact]
    public void A_valid_manifest_lists_its_agent_binaries()
    {
        var manifest = ReleaseManifest.TryParse(Encoding.UTF8.GetBytes(Manifest), out var problem);
        Assert.Null(problem);
        Assert.Equal("0.2.1", manifest!.Version);
        Assert.Equal(2, manifest.AgentBinaries.Count);
        Assert.Contains(manifest.AgentBinaries, b => b.Component == AgentComponent.Watchdog && b.File == "linux-arm64/fleeto-watchdog");
    }

    [Theory]
    [InlineData("\"formatVersion\":1", "\"formatVersion\":2")]
    [InlineData("\"version\":\"0.2.1\"", "\"version\":\"latest\"")]
    [InlineData("windows-amd64/fleeto-agent.exe", "windows-amd64/../../evil.exe")]
    [InlineData("windows-amd64/fleeto-agent.exe", "linux-amd64/fleeto-agent")]
    [InlineData("\"size\":10}", "\"size\":999999999999}")]
    [InlineData("\"component\":\"agent\"", "\"component\":\"shell\"")]
    public void An_invalid_manifest_is_refused(string from, string to)
    {
        var bad = Manifest.Replace(from, to, StringComparison.Ordinal);
        Assert.NotEqual(Manifest, bad);
        Assert.Null(ReleaseManifest.TryParse(Encoding.UTF8.GetBytes(bad), out var problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public void Update_rings_count_from_installation_and_follow_pause_and_release_to_all()
    {
        var installed = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
        var release = new AgentRelease { Version = "0.2.1", InstalledAt = installed };

        Assert.True(UpdateRings.IsAllowed(release, UpdateRing.Preview, installed));
        Assert.False(UpdateRings.IsAllowed(release, UpdateRing.Standard, installed.AddDays(6)));
        Assert.True(UpdateRings.IsAllowed(release, UpdateRing.Standard, installed.AddDays(7)));
        Assert.False(UpdateRings.IsAllowed(release, UpdateRing.Delayed, installed.AddDays(13)));
        Assert.True(UpdateRings.IsAllowed(release, UpdateRing.Delayed, installed.AddDays(14)));

        release.ReleasedToAllAt = installed.AddDays(1);
        Assert.True(UpdateRings.IsAllowed(release, UpdateRing.Delayed, installed.AddDays(1)));
        Assert.Equal(installed.AddDays(1), UpdateRings.AvailableAt(release, UpdateRing.Delayed));
        Assert.Equal(installed, UpdateRings.AvailableAt(release, UpdateRing.Preview));

        release.PausedAt = installed.AddDays(2);
        Assert.False(UpdateRings.IsAllowed(release, UpdateRing.Preview, installed.AddDays(30)));
    }
}
