using Bunit;
using Fleeto.Web.Components.Shared;
using Fleeto.Web.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Fleeto.Web.Tests;

/// <summary>
/// A time renders in the browser's zone as soon as it is known, also below a page: after a full reload the first render is in the
/// server zone (UTC), and Blazor does not render a child again whose parameters did not change (found on the production instance,
/// 2026-11-27: one hour off in Belgium until the user went to another page).
/// </summary>
public sealed class TimeTextTests : BunitContext
{
    [Fact]
    public async Task A_time_renders_again_in_the_browser_zone_once_it_is_known()
    {
        var display = new TimeDisplay(TimeProvider.System);
        display.SetZone("UTC");
        Services.AddSingleton(display);

        var text = Render<TimeText>(p => p.Add(t => t.Value, new DateTime(2026, 11, 25, 12, 2, 28, DateTimeKind.Utc)).Add(t => t.Absolute, true));
        Assert.Equal("25 Nov 2026 12:02", text.Find("span").TextContent);

        display.SetZone("Europe/Brussels");
        text.WaitForAssertion(() => Assert.Equal("25 Nov 2026 13:02", text.Find("span").TextContent));

        // Disposed with its page: a later change of zone reaches it no more.
        await DisposeComponentsAsync();
        display.SetZone("Asia/Tokyo");
    }
}
