using System.Globalization;
using Fleeto.Core.Entities;
using Fleeto.Web.Components.Shared;
using Fleeto.Web.Services;

namespace Fleeto.Web.Tests;

/// <summary>
/// The endpoint Summary states what the agent or watchdog installer waits for (0.2.2), with the ring date of the current release, and shows
/// nothing once the component runs the release it waited for.
/// </summary>
public class UpdateWaitTextTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private static string Absolute(DateTime? utc) => utc!.Value.ToString("d MMM HH:mm", CultureInfo.InvariantCulture);

    private static EndpointDetail Endpoint(ReleaseRingTiming? ring, string? release = "0.2.2") =>
        new(Guid.NewGuid(), "WS-01", Guid.NewGuid(), "ACME", "Acme", Guid.NewGuid(), "Main", true, EndpointTier.Managed, EndpointSource.Agent,
            EndpointClass.Workstation, null, "windows", "Windows 11", "10.0", "amd64", "0.2.1", Now, Now, 1, 1, 1, null, 0, 0, [], null, null, null,
            false, ReleaseVersion: release, ReleaseRing: ring);

    private static EndpointComponentView Wait(ComponentUpdateWait reason, DateTime? until = null, string version = "0.2.2") =>
        new(AgentComponent.Agent, ComponentServiceState.Unknown, "", null, "", null, "", null, version, reason, until, Now);

    [Fact]
    public void A_ring_wait_names_the_ring_and_the_day_it_reaches_the_release()
    {
        var endpoint = Endpoint(new ReleaseRingTiming(UpdateRing.Standard, new DateTime(2026, 9, 23, 14, 0, 0, DateTimeKind.Utc), false));
        Assert.Equal("Waiting for the update ring: release 0.2.2 reaches the Standard ring on 23 Sep 14:00",
            Ui.UpdateWaitText(Wait(ComponentUpdateWait.UpdateRing), "0.2.1", endpoint, Now, Absolute));
    }

    [Fact]
    public void A_ring_wait_for_a_paused_release_says_it_is_paused()
    {
        var endpoint = Endpoint(new ReleaseRingTiming(UpdateRing.Preview, Now.AddDays(-1), true));
        Assert.Equal("Waiting: release 0.2.2 is paused", Ui.UpdateWaitText(Wait(ComponentUpdateWait.UpdateRing), "0.2.1", endpoint, Now, Absolute));
    }

    [Fact]
    public void A_ring_wait_for_another_release_shows_no_date()
    {
        var endpoint = Endpoint(new ReleaseRingTiming(UpdateRing.Standard, Now.AddDays(3), false), release: "0.2.3");
        Assert.Equal("Waiting for the update ring: release 0.2.2", Ui.UpdateWaitText(Wait(ComponentUpdateWait.UpdateRing), "0.2.1", endpoint, Now, Absolute));
    }

    [Fact]
    public void A_retry_wait_names_the_time_of_the_next_attempt()
    {
        Assert.Equal("Next attempt to install 0.2.2 after 16 Sep 13:06",
            Ui.UpdateWaitText(Wait(ComponentUpdateWait.NextAttempt, Now.AddMinutes(66)), "0.2.1", Endpoint(null), Now, Absolute));
    }

    [Fact]
    public void A_missing_watchdog_shows_its_wait()
    {
        Assert.Equal("Waiting until the agent runs 0.2.2: the agent updates the watchdog after its own update",
            Ui.UpdateWaitText(Wait(ComponentUpdateWait.InstallerUpdate), "", Endpoint(null), Now, Absolute));
    }

    [Theory]
    [InlineData("0.2.2")]
    [InlineData("0.2.3")]
    public void No_wait_is_shown_once_the_component_runs_the_release(string installed)
    {
        Assert.Null(Ui.UpdateWaitText(Wait(ComponentUpdateWait.RolledBack), installed, Endpoint(null), Now, Absolute));
    }

    [Fact]
    public void No_wait_is_shown_when_none_was_reported()
    {
        var none = new EndpointComponentView(AgentComponent.Agent, ComponentServiceState.Running, "", null, "0.2.1", ComponentUpdateState.Installed, "", Now);
        Assert.Null(Ui.UpdateWaitText(none, "0.2.1", Endpoint(null), Now, Absolute));
    }
}
