using Fleeto.Core.Domain;

namespace Fleeto.Infrastructure.Tests;

/// <summary>
/// The installer link and the body of the job that puts the Action1 agent on an endpoint (0.4.0 step 3). An admin pastes
/// that link by hand, and fleeto-signer puts it in a script that runs as SYSTEM, so what counts as a valid link is
/// narrow on purpose: https, Action1's own domain, an MSI, and nothing that could end the quoted string it lands in.
/// </summary>
public class Action1AgentInstallTests
{
    [Theory]
    [InlineData("https://app.eu.action1.com/agent/9f2c/Windows/agent(Contoso).msi")]
    [InlineData("https://app.action1.com/agent/abc/Windows/agent.msi")]
    [InlineData("https://action1.com/agent/abc/Windows/agent.msi")]
    public void An_Action1_download_link_is_accepted(string url) => Assert.True(Action1AgentInstall.IsValidInstallerUrl(url));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    // Not Action1, so not ours to install from.
    [InlineData("https://example.test/agent.msi")]
    [InlineData("https://app.eu.action1.com.evil.test/agent.msi")]
    // Not https: an installer that runs as SYSTEM is never downloaded over a link anybody can rewrite.
    [InlineData("http://app.eu.action1.com/agent/abc/Windows/agent.msi")]
    // Not an installer.
    [InlineData("https://app.eu.action1.com/agent/abc/Windows/agent.exe")]
    // Anything that could end the quoted string in the script, or add a command to it.
    [InlineData("https://app.eu.action1.com/a.msi'; Remove-Item C:\\ -Recurse; '")]
    [InlineData("https://app.eu.action1.com/a.msi; shutdown /r")]
    [InlineData("https://app.eu.action1.com/a.msi $(whoami)")]
    public void Anything_else_is_refused(string? url) => Assert.False(Action1AgentInstall.IsValidInstallerUrl(url));

    [Fact]
    public void The_body_downloads_and_installs_that_link_and_restarts_nothing()
    {
        const string url = "https://app.eu.action1.com/agent/9f2c/Windows/agent(Contoso).msi";

        var body = Action1AgentInstall.Body(url);

        Assert.Contains($"Invoke-WebRequest -Uri '{url}'", body);
        Assert.Contains("msiexec.exe", body);
        Assert.Contains("/quiet", body);
        Assert.Contains("/norestart", body);
        // The installer is removed again, whatever happened.
        Assert.Contains("Remove-Item -LiteralPath $installer", body);
    }

    [Fact]
    public void The_install_stops_without_changes_when_an_Action1_agent_is_already_there()
    {
        var body = Action1AgentInstall.Body("https://app.eu.action1.com/agent/abc/Windows/agent.msi");

        // The check comes before the download, and it reads the key the Fleeto agent reads.
        Assert.True(body.IndexOf("if (Test-Action1Agent)", StringComparison.Ordinal) < body.IndexOf("Invoke-WebRequest", StringComparison.Ordinal));
        Assert.Contains("agent.guid", body);
        Assert.Contains("exit 0", body);
    }

    [Fact]
    public void The_removal_uninstalls_every_Action1_agent_by_product_code_and_restarts_nothing()
    {
        var body = Action1AgentRemoval.Body();

        Assert.Contains("'Action1 Agent*'", body);
        Assert.Contains("'/x', $agent.PSChildName", body);
        Assert.Contains("/norestart", body);
        // Already gone and a restart that is needed count as done, not as a failure.
        Assert.Contains("@(0, 1605, 3010)", body);
        Assert.Equal(body, Action1AgentRemoval.Body());
    }

    [Theory]
    [InlineData("/API/automations/instances/abc-123", "abc-123")]
    [InlineData("/API/automations/instances/org-1/abc-123/", "abc-123")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("/API/automations/instances/{instance-id}", "")]
    public void The_last_run_of_an_automation_is_the_last_segment_of_its_link(string? link, string expected) =>
        Assert.Equal(expected, Fleeto.Infrastructure.Integrations.Action1.Action1AutomationInfo.LastSegment(link));

    [Fact]
    public void A_link_the_rules_refuse_never_becomes_a_body() =>
        Assert.Throws<ArgumentException>(() => Action1AgentInstall.Body("https://example.test/agent.msi"));
}
