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
    public void A_link_the_rules_refuse_never_becomes_a_body() =>
        Assert.Throws<ArgumentException>(() => Action1AgentInstall.Body("https://example.test/agent.msi"));
}
